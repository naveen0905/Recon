# Recon Intelligence Platform — Architecture Decision Record

## Problem Statement

Enterprise penetration testing teams need a unified platform to aggregate, version, and query reconnaissance data from multiple internal sources (REST APIs, Azure SQL, Azure Data Explorer). Each team owns different data sources and must be able to register them via configuration. The platform must support scheduled and ad-hoc recon refresh, maintain version history of recon knowledge, expose data to an AI agent that orchestrates queries during pentest engagements, and deduplicate assets across sources.

---

## Confirmed Architectural Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Primary language | C# .NET 8 | Type safety, Azure SDK first-class support, SOC2-auditable |
| Agent / LLM layer | Python 3.11+ | Anthropic/OpenAI SDK maturity; runs as separate Container App |
| Solution structure | Single `.sln`, strict project boundaries | Future-proof: projects can split into separate repos without API changes |
| Hosting | Azure Container Apps (Consumption plan) | Scale-to-zero cost, replaceable, KEDA-native Service Bus triggers, better SOC2 audit trail than Functions |
| IaC | Bicep | Azure-native, no extra toolchain |
| Region | Single (East US 2) initially | Multi-region deferred until usage justifies it |
| Auth | Azure Entra ID — app registration per team | Scope isolation per team, SOC2 access control |
| Compliance | SOC2 Type II | Audit logging, encryption-at-rest, secret management, access control required |
| De-duplication | Configurable match keys per source, expandable per team | See De-Duplication section |
| Plugin / Skills model | Configuration-driven YAML | Agents can generate skill configs; no redeploy needed for new skills |

---

## Why Azure Container Apps over Azure Functions

| Factor | Container Apps (Consumption) | Azure Functions |
|---|---|---|
| Idle cost | $0 (scale-to-zero) | $0 (consumption plan) |
| Active cost | ~$0.000024/vCPU-s | ~$0.000016/GB-s |
| SOC2 auditability | Full container image control, SBOM | Runtime managed by Microsoft |
| Replaceability | Swap container image, same trigger config | Tied to Function runtime version |
| Service Bus trigger | KEDA native | Trigger binding native |
| Long-running pulls | Supported (no 10-min limit) | Requires Durable Functions for >10 min |
| **Verdict** | **Preferred** | Fallback only for Cosmos Change Feed if needed |

All workers, the API, and the agent service run as Container Apps. Azure Functions are **not used** in v1.

---

## System Architecture

```
Team Source APIs / Azure SQL / Azure ADX (team-owned)
          │
          ▼
    Connector Layer (C# .NET 8, pluggable via config)
          │
          ├──▶ Azure Blob Storage (Parquet) ◀── raw archive, source of truth, versioned by date partition
          │         │
          │         ▼
          │    Synapse Serverless SQL Pool ◀── query layer, pay-per-query, T-SQL on Parquet
          │
          ├──▶ Azure Cosmos DB (free tier + autoscale) ◀── hot tier, active engagements, versioned documents
          │         │
          │         └── Change Feed polling ──▶ Worker Container App ──▶ retrigger enrichment
          │
          ├──▶ Azure SQL Serverless ◀── operational metadata (configs, schedules, run logs)
          │
          └──▶ Azure Service Bus ◀── retrigger job queue
                    │
                    ▼
               Connector Worker (Container App, KEDA Service Bus trigger)
                    │
                    └──▶ writes back to Blob + Cosmos

API Layer (ASP.NET Core 8, Container App)
  └──▶ Agent Service (Python, Container App) ◀── LLM calls
```

---

## Storage Layer Decisions

| Layer | Technology | Purpose | Why |
|---|---|---|---|
| Raw archive | Azure Blob Storage (Parquet) | Every pull, immutable, forever | Cheapest at scale, audit trail, replay, source of truth |
| Query layer | Synapse Serverless SQL Pool | T-SQL queries on Parquet | Pay-per-query (~$5/TB), zero idle cost |
| Hot tier | Cosmos DB (free tier + autoscale) | Active engagement assets, versioned | Native TTL, Change Feed, document model |
| Operational metadata | Azure SQL Serverless | Team configs, run logs, engagement records | Relational, auto-pauses when idle |
| Job queue | Azure Service Bus | Retrigger jobs | Durable, decoupled, KEDA-compatible |

### Blob Partition Structure

```
blob://recon-raw/
  {team}/
    {source-id}/
      {year}/{month}/{day}/
        pull_{timestamp}.parquet
```

### What NOT to build

- **DuckDB** — not permitted
- **ADX as primary store** — $700–1300/month; future upgrade path only
- **Azure Functions** — replaced by Container Apps
- **PostgreSQL / Azure SQL as recon store** — rigid schema, no native versioning
- **Hardcoded secrets** — all secrets via Azure Key Vault

---

## De-Duplication

### Problem
The same asset (e.g., `server01.corp.com`) may appear in multiple sources (asset-inventory-api, vuln-db, network-telemetry) with slightly different field values. Without deduplication, the same host gets multiple Cosmos documents and inflated Parquet rows.

### Design

**Configurable match keys per source** — each source config declares which fields constitute identity:

```yaml
sources:
  - id: asset-inventory-api
    dedup:
      match_keys: [host, ip]          # fields used to find existing asset
      conflict_resolution: last_write  # last_write | highest_confidence | source_priority
      source_priority: 10              # lower = higher priority (used when conflict_resolution = source_priority)

  - id: vuln-db
    dedup:
      match_keys: [host, finding]      # different identity for vulnerabilities
      conflict_resolution: source_priority
      source_priority: 5
```

**Conflict resolution strategies:**
- `last_write` — most recent pull wins for conflicting fields
- `highest_confidence` — source with `confidence_score` field wins
- `source_priority` — explicit numeric priority; lower number = higher priority

**Team-level expansion** — teams can register a custom dedup resolver class:

```yaml
team: network-security
dedup:
  custom_resolver: plugins.dedup.NetworkSecurityDeduplicator  # optional override
```

**Asset types with dedup support:**
- `host` — match on `host` + `ip`
- `credential` — match on `host` + `username`
- `finding` / `vulnerability` — match on `host` + `cve_id` or `finding_id`
- `person` — match on `email` or `employee_id`
- `service` — match on `host` + `port` + `service`

The `DeduplicationEngine` in `src/ReconPlatform.Engine` applies match keys before any upsert to Cosmos.

---

## Configuration-Driven Skills / Sub-Agents

New data types and data sources are added via YAML config — no code deployment required for common cases. Agents can generate these configs given context.

### Skill Definition (YAML)

```yaml
# skills/asset-enrichment.yaml
id: asset-enrichment
name: Asset Enrichment
version: 1
trigger:
  type: on_new_asset          # on_new_asset | on_severity_change | scheduled | manual
  filter:
    asset_type: host
    severity: [high, critical]
actions:
  - type: retrigger_sources
    sources: [vuln-db, network-telemetry]
  - type: notify
    channel: service_bus
    queue: enrichment-alerts
enabled: true
```

### Sub-Agent Definition (YAML)

```yaml
# skills/agents/scope-validator.yaml
id: scope-validator
name: Scope Validator Agent
version: 1
type: llm_agent
model: azure-openai/gpt-4o            # or anthropic/claude-sonnet
system_prompt_file: prompts/scope-validator.txt
tools:
  - query_assets
  - get_engagement_scope
  - flag_out_of_scope
enabled: true
```

The `SkillRegistry` loads all YAML files from the `skills/` directory at startup and re-reads on file change (no restart needed). Agents can POST new skill YAML to `/api/skills` to register dynamically.

---

## SOC2 Compliance Requirements

| Control | Implementation |
|---|---|
| Access control | Entra ID app registration per team; RBAC on all Azure resources |
| Audit logging | Every API call, connector run, and data access logged to `connector_run_log` + Azure Monitor |
| Encryption at rest | Cosmos DB, Blob, SQL — Azure-managed keys (CMK upgrade path) |
| Encryption in transit | TLS 1.2+ enforced on all endpoints; no HTTP |
| Secret management | Azure Key Vault; no secrets in code, config files, or logs |
| Data access | All recon queries scoped to engagement; no cross-engagement data leakage |
| Incident response | Dead-letter queue alerts; Azure Monitor alerts on anomalous pull volumes |
| Vulnerability management | Dependabot enabled; container image scanning in CI |
| No secrets in logs | Structured logging with field scrubbing for any field named `*secret*`, `*password*`, `*key*`, `*token*`, `*conn*` |

---

## Cosmos DB Document Schema (Hot Tier)

```json
{
  "id": "host::server01.corp.com",
  "partitionKey": "network-security",
  "type": "host | credential | finding | person | service",
  "team": "network-security",

  "current": {
    "host": "server01.corp.com",
    "ip": "10.10.5.21",
    "ports": [443, 8080],
    "services": ["HTTPS", "HTTP-ALT"],
    "tags": ["internet-facing"],
    "severity": "high | medium | low | info",
    "owner": "team-name",
    "confidence_score": 0.95,
    "source_priority": 5
  },

  "dedup_key": "host::server01.corp.com",
  "contributing_sources": ["asset-inventory-api", "vuln-db"],

  "version": 4,
  "first_seen": "2026-01-15T08:00:00Z",
  "last_pulled": "2026-06-01T10:00:00Z",
  "last_changed": "2026-05-20T14:30:00Z",
  "pull_source": "asset-inventory-api",
  "stale_after_days": 7,
  "is_stale": false,
  "retrigger_scheduled": false,

  "history": [
    {
      "version": 3,
      "snapshot": { "ports": [443], "services": ["HTTPS"] },
      "pulled_at": "2026-05-20T14:30:00Z",
      "diff": {
        "added_ports": [8080],
        "added_services": ["HTTP-ALT"],
        "removed_ports": [],
        "removed_services": []
      }
    }
  ],

  "_ts": 1748779200,
  "ttl": -1
}
```

---

## Canonical Normalized Schema (Parquet + Synapse)

```
asset_id            STRING      -- uuid, generated
team                STRING
source_id           STRING
dedup_key           STRING      -- computed from match_keys
type                STRING      -- host | credential | finding | person | service
host                STRING
ip                  STRING
port                INT
service             STRING
version_str         STRING
severity            STRING      -- high | medium | low | info
tags                ARRAY<STRING>
owner               STRING
evidence            STRING
confidence_score    DOUBLE      -- 0.0-1.0, used for conflict resolution
source_priority     INT
pulled_at           TIMESTAMP
raw                 STRING      -- JSON string of original payload
```

---

## Team Configuration Schema (YAML)

```yaml
team: network-security
stale_after_days: 7
auth:
  entra_app_id: "{{secret:NETWORK_SEC_APP_ID}}"
  tenant_id: "{{secret:AZURE_TENANT_ID}}"

dedup:
  default_conflict_resolution: source_priority
  custom_resolver: null             # optional plugin class

sources:
  - id: asset-inventory-api
    type: rest_api
    stale_after_days: 3
    base_url: https://internal-api.corp.com/assets
    auth:
      type: oauth2
      client_id: "{{secret:ASSET_API_CLIENT_ID}}"
      scope: asset.read
    mapping:
      host: "$.data[*].hostname"
      ip: "$.data[*].ip_address"
      owner: "$.data[*].team"
    dedup:
      match_keys: [host, ip]
      conflict_resolution: source_priority
      source_priority: 10

  - id: vuln-db
    type: azure_sql
    stale_after_days: 1
    connection_string: "{{secret:VULN_DB_CONN}}"
    query: "SELECT host, cve, severity FROM findings WHERE active=1"
    mapping:
      host: host
      finding: cve
      severity: severity
    dedup:
      match_keys: [host, finding]
      conflict_resolution: source_priority
      source_priority: 5

  - id: network-telemetry
    type: azure_adx
    stale_after_days: 7
    cluster: https://telemetry.eastus.kusto.windows.net
    database: NetworkLogs
    query: "NetworkFlows | where timestamp > ago(7d) | summarize by src_ip"
    auth:
      type: managed_identity
    dedup:
      match_keys: [ip]
      conflict_resolution: last_write

  - id: custom-source
    type: plugin
    plugin_class: plugins.MyCustomConnector
    stale_after_days: 14
    config:
      endpoint: https://custom.internal/api
    dedup:
      match_keys: [host]
      conflict_resolution: last_write
```

### Supported Connector Types

- `rest_api` — generic REST with OAuth2, API Key, Bearer token auth
- `azure_sql` — Azure SQL / SQL Server
- `azure_adx` — Azure Data Explorer with KQL
- `plugin` — custom class implementing `IConnector` interface (C#) or `BaseConnector` (Python)

### Secret Resolution

All `{{secret:KEY_NAME}}` values resolved from Azure Key Vault at runtime. Falls back to environment variables for local dev. Never stored in config plaintext. Key Vault access logged for SOC2.

---

## Retrigger Engine

**Mode 1 — Staleness-based (scheduled)**
- Container App scheduled job runs every 6 hours
- Queries Cosmos: `WHERE last_pulled < NOW - stale_after_days AND is_stale = false`
- Sets `is_stale = true`, `retrigger_scheduled = true`
- Pushes asset IDs to Service Bus

**Mode 2 — Ad-hoc (API triggered)**
- `POST /api/recon/retrigger` with team/target
- Pushes to same Service Bus queue

**Mode 3 — Change Feed (reactive enrichment)**
- Worker Container App polls Cosmos change feed
- If new asset → trigger enrichment per matching skill definitions
- If severity → `critical` → cascade retrigger + alert

---

## Azure SQL Serverless Schema

```sql
CREATE TABLE team_configs (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    team_name NVARCHAR(100) NOT NULL,
    config_yaml NVARCHAR(MAX) NOT NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by NVARCHAR(200) NOT NULL
);

CREATE TABLE connector_run_log (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    team_name NVARCHAR(100) NOT NULL,
    source_id NVARCHAR(100) NOT NULL,
    run_at DATETIME2 NOT NULL,
    status NVARCHAR(20) NOT NULL,     -- success | failed | partial
    assets_pulled INT,
    assets_deduped INT,
    error_message NVARCHAR(MAX),
    blob_path NVARCHAR(500),
    caller_identity NVARCHAR(200)     -- SOC2: who triggered this run
);

CREATE TABLE engagements (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    name NVARCHAR(200) NOT NULL,
    scope_teams NVARCHAR(MAX) NOT NULL,
    scope_targets NVARCHAR(MAX) NOT NULL,
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    status NVARCHAR(20) NOT NULL,     -- active | closed | planned
    created_by NVARCHAR(200) NOT NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE user_permissions (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id NVARCHAR(200) NOT NULL,
    team_name NVARCHAR(100) NOT NULL,
    permission NVARCHAR(50) NOT NULL, -- read | write | admin
    granted_by NVARCHAR(200) NOT NULL,
    granted_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE audit_log (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    event_time DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    event_type NVARCHAR(100) NOT NULL,
    actor NVARCHAR(200) NOT NULL,
    team_name NVARCHAR(100),
    resource_id NVARCHAR(500),
    details NVARCHAR(MAX),
    ip_address NVARCHAR(50)
);
```

---

## API Layer (ASP.NET Core 8)

```
POST   /api/teams/{team}/sources             -- register source
GET    /api/teams/{team}/sources             -- list sources
PUT    /api/teams/{team}/sources/{id}        -- update source config
DELETE /api/teams/{team}/sources/{id}        -- remove source
POST   /api/teams/{team}/sources/{id}/test   -- test connection

POST   /api/recon/pull                       -- ad-hoc full pull
POST   /api/recon/retrigger                  -- retrigger asset or team
GET    /api/recon/assets                     -- query via Synapse
GET    /api/recon/assets/{id}                -- get with version history (Cosmos)
GET    /api/recon/assets/{id}/diff           -- diff two versions

POST   /api/engagements                      -- create engagement
GET    /api/engagements                      -- list active engagements
GET    /api/engagements/{id}/assets          -- assets in scope

POST   /api/skills                           -- register skill config (YAML)
GET    /api/skills                           -- list registered skills
DELETE /api/skills/{id}                      -- remove skill

POST   /api/agent/query                      -- natural language query

GET    /api/health                           -- component health check
```

---

## Project Structure (.NET 8 Solution)

```
ReconPlatform.sln
├── src/
│   ├── ReconPlatform.Api/               # ASP.NET Core 8 Web API (Container App)
│   │   ├── Controllers/
│   │   │   ├── TeamsController.cs
│   │   │   ├── ReconController.cs
│   │   │   ├── EngagementsController.cs
│   │   │   ├── SkillsController.cs
│   │   │   └── AgentController.cs
│   │   ├── Middleware/
│   │   │   ├── AuditLoggingMiddleware.cs
│   │   │   └── SecretScrubMiddleware.cs
│   │   └── Program.cs
│   ├── ReconPlatform.Connectors/        # Connector framework
│   │   ├── Interfaces/
│   │   │   └── IConnector.cs
│   │   ├── RestApiConnector.cs
│   │   ├── AzureSqlConnector.cs
│   │   ├── AzureAdxConnector.cs
│   │   └── PluginLoader.cs
│   ├── ReconPlatform.Storage/           # Storage clients
│   │   ├── BlobStorageClient.cs
│   │   ├── CosmosDbClient.cs
│   │   ├── SynapseClient.cs
│   │   └── SqlMetadataClient.cs
│   ├── ReconPlatform.Engine/            # Core business logic
│   │   ├── Normalizer.cs
│   │   ├── DiffEngine.cs
│   │   ├── StalenessChecker.cs
│   │   ├── RetriggerOrchestrator.cs
│   │   └── DeduplicationEngine.cs
│   ├── ReconPlatform.Workers/           # Container App jobs
│   │   ├── ConnectorWorker/             # Service Bus consumer (KEDA)
│   │   ├── StalenessTimer/              # Scheduled job (every 6h)
│   │   └── ChangeFeedWorker/            # Cosmos change feed poller
│   ├── ReconPlatform.Config/            # Config models + validation
│   │   ├── Models/
│   │   │   ├── TeamConfig.cs
│   │   │   ├── SourceConfig.cs
│   │   │   └── DeduplicationConfig.cs
│   │   ├── Validator.cs
│   │   └── SecretResolver.cs
│   ├── ReconPlatform.Skills/            # Skill / sub-agent registry
│   │   ├── SkillRegistry.cs
│   │   ├── SkillExecutor.cs
│   │   └── Models/
│   │       ├── SkillDefinition.cs
│   │       └── AgentDefinition.cs
│   └── ReconPlatform.Shared/            # Shared models, interfaces, constants
│       ├── Models/
│       │   └── CanonicalAsset.cs
│       └── Interfaces/
├── agent/                               # Python LLM agent (Container App)
│   ├── main.py
│   ├── orchestrator.py
│   ├── query_builder.py
│   ├── tools/
│   └── requirements.txt
├── plugins/                             # Drop-in connector plugins (C# or Python)
│   └── ExamplePlugin.cs
├── skills/                              # Config-driven skill definitions (YAML)
│   ├── agents/
│   │   └── scope-validator.yaml
│   └── asset-enrichment.yaml
├── infra/                               # Bicep templates
│   ├── main.bicep
│   ├── blob.bicep
│   ├── cosmos.bicep
│   ├── synapse.bicep
│   ├── sql.bicep
│   ├── servicebus.bicep
│   ├── containerapp.bicep
│   └── keyvault.bicep
├── tests/
│   ├── ReconPlatform.UnitTests/
│   └── ReconPlatform.IntegrationTests/
├── docs/
│   ├── setup.md
│   ├── deployment.md
│   ├── configuration.md
│   └── extending-skills.md
├── .claude/
│   └── context.md
├── ARCHITECTURE.md
├── TASKS.md
├── CLAUDE.md
└── README.md
```

---

## Cost Estimate

| Component | Monthly (low usage) | Notes |
|---|---|---|
| Azure Blob Storage | $2–5 | 10–50GB recon data |
| Synapse Serverless SQL | $1–5 | ~1–2TB scanned/month |
| Cosmos DB | $0–30 | Free tier covers most cases |
| Azure SQL Serverless | $10–20 | Auto-pauses when idle |
| Service Bus | $1–2 | Standard tier |
| Container Apps | $0–10 | Consumption plan, scale-to-zero |
| Key Vault | $1–3 | Secret operations |
| Azure Monitor / Log Analytics | $5–15 | SOC2 audit log retention |
| **Total** | **$20–90/month** | vs $700–1300 for ADX-first |

---

## Open Questions / Deferred Decisions

- Agent LLM choice: Azure OpenAI vs Anthropic API (placeholder, configurable per agent YAML)
- Multi-region Cosmos DB (deferred)
- Blob lifecycle policy — archive tier after 90 days (deferred)
- CMK (Customer Managed Keys) for encryption at rest (deferred, upgrade path noted)
- Rate limiting per connector — v2
