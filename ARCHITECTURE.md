# Recon Intelligence Platform — Architecture Decision Record

## Problem Statement

Enterprise penetration testing teams need a unified platform to aggregate, version, and query reconnaissance data from multiple internal sources (REST APIs, Azure SQL, Azure Data Explorer). Each team owns different data sources and must be able to register them via configuration. The platform must support scheduled and ad-hoc recon refresh, maintain version history of recon knowledge, and expose data to an AI agent that orchestrates queries during pentest engagements.

---

## Final Architecture Decision

```
Team Source APIs / Azure SQL / Azure ADX (team-owned)
          │
          ▼
    Connector Layer (Python, pluggable)
          │
          ├──▶ Azure Blob Storage (Parquet) ◀── raw archive, source of truth, versioned by date partition
          │         │
          │         ▼
          │    Synapse Serverless SQL Pool ◀── query layer, pay-per-query, T-SQL on Parquet
          │         │
          │         └── upgrade path ──▶ Synapse Dedicated Pool ──▶ ADX (same Blob, no data migration)
          │
          ├──▶ Azure Cosmos DB (free tier + autoscale) ◀── hot tier, active engagements, versioned documents
          │         │
          │         └── Change Feed ──▶ Azure Function ──▶ retrigger enrichment
          │
          ├──▶ Azure SQL Serverless ◀── operational metadata only (configs, schedules, run logs)
          │         (auto-pause when idle)
          │
          └──▶ Azure Service Bus ◀── retrigger job queue (staleness + ad-hoc)
                    │
                    ▼
               Azure Functions (Connector Workers, serverless, pay-per-execution)
                    │
                    └──▶ writes back to Blob + Cosmos
```

---

## Storage Layer Decisions

| Layer | Technology | Purpose | Why |
|---|---|---|---|
| Raw archive | Azure Blob Storage (Parquet) | Every pull, immutable, forever | Cheapest at scale, audit trail, replay, source of truth |
| Query layer | Synapse Serverless SQL Pool | T-SQL queries on Parquet | Pay-per-query (~$5/TB), zero idle cost, scales to Dedicated Pool or ADX |
| Hot tier | Cosmos DB (free tier + autoscale) | Active engagement assets, versioned | Native TTL, Change Feed, document model, free tier covers most usage |
| Operational metadata | Azure SQL Serverless | Team configs, run logs, engagement records | Relational by nature, auto-pauses when idle |
| Job queue | Azure Service Bus | Retrigger jobs | Durable, decoupled, serverless consumers |

### Blob Partition Structure

```
blob://recon-raw/
  {team}/
    {source-id}/
      {year}/{month}/{day}/
        pull_{timestamp}.parquet
```

### Why NOT these technologies

- **DuckDB** — not permitted in this environment
- **ADX as primary store** — $700–1300/month cluster always-on cost; overkill at current scale. Reserved as future upgrade path when query volume justifies it. Blob stays as source of truth so migration is zero-effort.
- **PostgreSQL / Azure SQL as primary recon store** — rigid schema, painful migrations as new source types are added, no native versioning
- **Azure SQL as only store** — recon data is document-shaped, not relational; schema changes too frequent

---

## Cosmos DB Document Schema (Hot Tier)

Each recon asset is one document. Version history embedded.

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
    "owner": "team-name"
  },

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

All connectors normalize to this schema before writing to Blob:

```
asset_id        STRING      -- uuid, generated
team            STRING      -- team identifier
source_id       STRING      -- connector source id
type            STRING      -- host | credential | finding | person | service
host            STRING
ip              STRING
port            INT
service         STRING
version_str     STRING      -- service version if known
severity        STRING      -- high | medium | low | info
tags            ARRAY<STRING>
owner           STRING
evidence        STRING
pulled_at       TIMESTAMP
raw             STRING      -- JSON string of original source payload
```

---

## Team Configuration Schema (YAML)

Each team registers sources in this format. Stored in Azure SQL (configs table).

```yaml
team: network-security
stale_after_days: 7        # default for all sources in this team

sources:
  - id: asset-inventory-api
    type: rest_api
    stale_after_days: 3    # override per source
    base_url: https://internal-api.corp.com/assets
    auth:
      type: oauth2
      client_id: "{{secret:ASSET_API_CLIENT_ID}}"   # resolved from Azure Key Vault
      scope: asset.read
    mapping:
      host: "$.data[*].hostname"
      ip: "$.data[*].ip_address"
      owner: "$.data[*].team"

  - id: vuln-db
    type: azure_sql
    stale_after_days: 1
    connection_string: "{{secret:VULN_DB_CONN}}"
    query: "SELECT host, cve, severity FROM findings WHERE active=1"
    mapping:
      host: host
      finding: cve
      severity: severity

  - id: network-telemetry
    type: azure_adx
    stale_after_days: 7
    cluster: https://telemetry.eastus.kusto.windows.net
    database: NetworkLogs
    query: "NetworkFlows | where timestamp > ago(7d) | summarize by src_ip"
    auth:
      type: managed_identity

  - id: custom-source
    type: plugin
    plugin_class: plugins.my_custom_connector.MyConnector
    stale_after_days: 14
    config:
      endpoint: https://custom.internal/api
```

### Supported Connector Types

- `rest_api` — generic REST with OAuth2, API Key, Bearer token auth
- `azure_sql` — Azure SQL / SQL Server with connection string or managed identity
- `azure_adx` — Azure Data Explorer with KQL queries
- `plugin` — custom Python class implementing `BaseConnector` interface

### Auth Types

- `oauth2` — client credentials flow
- `api_key` — header or query param
- `bearer` — static bearer token
- `managed_identity` — Azure MSI (preferred for Azure-to-Azure)

### Secret Resolution

All `{{secret:KEY_NAME}}` values are resolved from Azure Key Vault at runtime. Never stored in config plaintext.

---

## Retrigger Engine

### Three trigger modes

**Mode 1 — Staleness-based (scheduled)**
- Azure Timer Function runs every 6 hours
- Queries Cosmos DB: `WHERE last_pulled < NOW - stale_after_days AND is_stale = false`
- Sets `is_stale = true`, `retrigger_scheduled = true` on matched documents
- Pushes asset IDs to Service Bus queue
- Connector Worker picks up → re-pulls → updates Cosmos + writes new Parquet to Blob

**Mode 2 — Ad-hoc (API triggered)**
- `POST /api/recon/retrigger` with `{ "team": "...", "target": "..." }` or `{ "team": "..." }` for full team retrigger
- Sets `retrigger_scheduled = true` on document
- Pushes to same Service Bus queue → same Connector Worker path

**Mode 3 — Change Feed (reactive enrichment)**
- Cosmos DB Change Feed triggers Azure Function on every document mutation
- If new asset type appears → trigger enrichment from other team sources
- If severity changes to `critical` → alert + cascade retrigger across all sources for that asset

---

## Azure SQL Serverless Schema (Operational Metadata Only)

```sql
-- Team source configurations
CREATE TABLE team_configs (
    id UNIQUEIDENTIFIER PRIMARY KEY,
    team_name NVARCHAR(100),
    config_yaml NVARCHAR(MAX),       -- full YAML stored as text
    created_at DATETIME2,
    updated_at DATETIME2,
    created_by NVARCHAR(200)
);

-- Individual connector run log
CREATE TABLE connector_run_log (
    id UNIQUEIDENTIFIER PRIMARY KEY,
    team_name NVARCHAR(100),
    source_id NVARCHAR(100),
    run_at DATETIME2,
    status NVARCHAR(20),             -- success | failed | partial
    assets_pulled INT,
    error_message NVARCHAR(MAX),
    blob_path NVARCHAR(500)
);

-- Pentest engagement records (scope enforcement)
CREATE TABLE engagements (
    id UNIQUEIDENTIFIER PRIMARY KEY,
    name NVARCHAR(200),
    scope_teams NVARCHAR(MAX),       -- JSON array of team names in scope
    scope_targets NVARCHAR(MAX),     -- JSON array of hosts/IPs/ranges
    start_date DATE,
    end_date DATE,
    status NVARCHAR(20),             -- active | closed | planned
    created_by NVARCHAR(200)
);

-- User access control
CREATE TABLE user_permissions (
    id UNIQUEIDENTIFIER PRIMARY KEY,
    user_id NVARCHAR(200),
    team_name NVARCHAR(100),
    permission NVARCHAR(50)          -- read | write | admin
);
```

---

## API Layer (FastAPI)

### Key Endpoints

```
POST   /api/teams/{team}/sources          -- register a new source
GET    /api/teams/{team}/sources          -- list sources for a team
PUT    /api/teams/{team}/sources/{id}     -- update source config
DELETE /api/teams/{team}/sources/{id}     -- remove source

POST   /api/recon/pull                    -- trigger ad-hoc full pull for a team
POST   /api/recon/retrigger               -- retrigger specific asset or team
GET    /api/recon/assets                  -- query recon assets (Synapse)
GET    /api/recon/assets/{id}             -- get asset with full version history (Cosmos)
GET    /api/recon/assets/{id}/diff        -- diff between two versions

POST   /api/engagements                   -- create engagement (scope)
GET    /api/engagements/{id}/assets       -- all assets in scope for engagement

GET    /api/health                        -- service health
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
| Azure Functions | $0–5 | First 1M executions/month free |
| **Total** | **$14–67/month** | vs $700–1300 for ADX-first |

### Scale Trigger Points

```
$14–67/month    → Current architecture unchanged
$200–400/month  → Move Synapse to Dedicated Pool; Cosmos autoscale increases
$700+/month     → Migrate query layer to ADX; Blob stays, zero data migration
```

---

## Project Structure

```
recon-intelligence-platform/
├── src/
│   ├── api/                    # FastAPI application
│   │   ├── main.py
│   │   ├── routers/
│   │   │   ├── teams.py
│   │   │   ├── recon.py
│   │   │   └── engagements.py
│   │   └── dependencies.py
│   ├── connectors/             # Connector framework
│   │   ├── base.py             # BaseConnector abstract class
│   │   ├── rest_api.py
│   │   ├── azure_sql.py
│   │   ├── azure_adx.py
│   │   └── plugin_loader.py
│   ├── storage/                # Storage clients
│   │   ├── blob.py             # Azure Blob (Parquet write)
│   │   ├── cosmos.py           # Cosmos DB (hot tier)
│   │   ├── synapse.py          # Synapse Serverless SQL (query)
│   │   └── sql.py              # Azure SQL (operational metadata)
│   ├── engine/                 # Core business logic
│   │   ├── normalizer.py       # Raw → canonical schema
│   │   ├── diff.py             # Version diffing
│   │   ├── staleness.py        # Staleness detection
│   │   └── retrigger.py        # Retrigger orchestration
│   ├── functions/              # Azure Functions
│   │   ├── timer_staleness/    # Staleness detection (every 6h)
│   │   ├── change_feed/        # Cosmos change feed handler
│   │   └── connector_worker/   # Service Bus consumer
│   ├── config/                 # Config management
│   │   ├── models.py           # Pydantic models for team config
│   │   ├── validator.py
│   │   └── secrets.py          # Key Vault resolution
│   └── agent/                  # AI agent interface
│       ├── orchestrator.py
│       └── query_builder.py
├── plugins/                    # Custom connector plugins (teams drop in here)
│   └── example_plugin.py
├── infra/                      # IaC (Bicep or Terraform)
│   ├── main.bicep
│   ├── blob.bicep
│   ├── cosmos.bicep
│   ├── synapse.bicep
│   └── functions.bicep
├── tests/
│   ├── unit/
│   └── integration/
├── ARCHITECTURE.md             # this file
├── TASKS.md
├── .claude/
│   └── context.md
├── requirements.txt
├── pyproject.toml
└── README.md
```

---

## Open Questions / Deferred Decisions

- Authentication for the API layer (Azure AD / MSAL — not yet implemented)
- Agent LLM choice (Azure OpenAI vs Anthropic API — placeholder for now)
- Multi-region Cosmos DB (deferred until usage grows)
- Blob lifecycle policy (archive tier after 90 days — deferred)
- Rate limiting per connector (basic retry logic in v1, proper rate limiting in v2)
