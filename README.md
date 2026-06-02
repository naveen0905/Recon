# Recon Intelligence Platform

[![CI](https://github.com/your-org/recon-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/your-org/recon-platform/actions/workflows/ci.yml)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blue)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Python 3.11+](https://img.shields.io/badge/Python-3.11%2B-blue)](https://www.python.org/)
[![Azure](https://img.shields.io/badge/Azure-Container%20Apps-0078D4)](https://azure.microsoft.com/en-us/products/container-apps)

A configuration-driven platform for enterprise penetration testing teams to aggregate, version, deduplicate, and query reconnaissance data from multiple internal sources, with an AI agent interface for natural language queries.

---

## What is this?

The Recon Intelligence Platform lets penetration testing teams register their data sources (REST APIs, Azure SQL, Azure Data Explorer, custom plugins) via YAML configuration — no code deployments needed for common cases. Scheduled and ad-hoc connector pulls ingest assets into a versioned store, deduplicating across sources using configurable match keys. An AI agent backed by Claude answers natural language recon questions within the strict scope of the active engagement.

---

## Architecture

```mermaid
graph TB
    subgraph "External Sources (Team-owned)"
        S1[REST APIs]
        S2[Azure SQL DBs]
        S3[Azure ADX / Kusto]
        S4[Plugin Connectors]
    end

    subgraph "Azure Container Apps"
        API[ReconPlatform.Api<br/>ASP.NET Core 8<br/>:8080]
        AGENT[Python Agent<br/>FastAPI<br/>:8000]
        CW[Connector Worker<br/>KEDA Service Bus]
        ST[Staleness Timer<br/>KEDA Cron]
        CF[Change Feed Worker<br/>Cosmos Change Feed]
    end

    subgraph "Azure Storage"
        BLOB[Blob Storage<br/>Parquet Archives]
        COSMOS[Cosmos DB<br/>Hot Asset Store]
        SQL[Azure SQL<br/>Metadata & Audit]
        SYN[Synapse Serverless<br/>Query Layer]
        SB[Service Bus<br/>connector-jobs queue]
    end

    subgraph "Platform Services"
        KV[Key Vault<br/>Secrets]
        LA[Log Analytics]
        ENTRA[Entra ID<br/>Auth]
    end

    subgraph "Consumers"
        USER[Pentest Team<br/>via API / Agent]
    end

    USER -->|JWT Bearer| API
    API -->|proxy| AGENT
    AGENT -->|tool calls| API
    API --> COSMOS
    API --> SQL
    API --> SYN
    API --> SB
    ST -->|stale query| COSMOS
    ST -->|enqueue| SB
    SB -->|trigger| CW
    CW --> S1 & S2 & S3 & S4
    CW --> BLOB
    CW --> COSMOS
    CW --> SQL
    CF -->|change events| SB
    COSMOS -->|change feed| CF
    API & AGENT & CW & ST & CF -->|logs| LA
    API & AGENT & CW & ST & CF -->|secrets| KV
    ENTRA -->|validate token| API
```

---

## Quick Start

Get running locally in 5 commands:

```bash
# 1. Clone and build
git clone https://github.com/your-org/recon-platform.git && cd recon-platform
dotnet build ReconPlatform.sln --configuration Release

# 2. Start local Azure emulators
docker compose up -d

# 3. Configure local environment
cp .env.example .env  # then edit .env with your values (see docs/setup.md)

# 4. Start the API
dotnet run --project src/ReconPlatform.Api

# 5. Start the Python agent (new terminal)
cd agent && pip install -r requirements.txt && uvicorn main:app --reload --port 8000
```

API Swagger UI: `http://localhost:5000/swagger`
Agent Swagger UI: `http://localhost:8000/docs`
Health check: `curl http://localhost:5000/api/health`

See [docs/setup.md](docs/setup.md) for the complete setup guide including VS Code extensions, Docker configuration, and common troubleshooting.

---

## Key Scenarios

### Scenario 1: Team Registers a New Data Source

```mermaid
sequenceDiagram
    participant T as Team Lead
    participant API as Recon API
    participant SQL as Azure SQL
    participant KV as Key Vault

    T->>API: PUT /api/teams/alpha/config\n(YAML with new source)
    API->>API: Validate schema & auth type
    API->>SQL: WriteAudit (pre-action)
    API->>SQL: UpsertTeamConfig
    API-->>T: 200 OK
    Note over T,KV: Secrets referenced as {{secret:KEY}}\nresolved at pull time from Key Vault
```

### Scenario 2: Scheduled Pull and Deduplication

```mermaid
sequenceDiagram
    participant ST as Staleness Timer
    participant SB as Service Bus
    participant CW as Connector Worker
    participant Src as Source (API/SQL/ADX)
    participant BLOB as Blob Storage
    participant CDB as Cosmos DB

    ST->>CDB: Query stale assets
    ST->>SB: Enqueue retrigger messages
    SB->>CW: Trigger (KEDA)
    CW->>Src: PullAsync()
    Src-->>CW: Raw rows
    CW->>CW: Normalize → CanonicalAsset
    CW->>CW: DeduplicationEngine.ComputeDedupKey()
    CDB-->>CW: Existing asset (or null)
    CW->>CW: DeduplicationEngine.Resolve()
    CW->>CDB: UpsertAsset (version++)
    CW->>BLOB: UploadPull (Parquet)
    CW->>SQL: InsertConnectorRun (audit)
```

### Scenario 3: Agent Answers a Pentest Query

```mermaid
sequenceDiagram
    participant P as Pentester
    participant API as Recon API
    participant AG as Python Agent
    participant QB as Query Builder
    participant LLM as Claude (Anthropic)
    participant Tools as Tool Stubs

    P->>API: POST /api/agent/query\n{team, engagement_id, question}
    API->>API: Validate team claim (JWT)
    API->>AG: Proxy POST /query
    AG->>QB: build_query_plan(question, sources)
    QB->>LLM: "Which tool handles this?"
    LLM-->>QB: QueryPlan JSON
    QB-->>AG: QueryPlan
    AG->>LLM: Tool-use loop (system prompt + tools)
    loop Until end_turn or max 10 iterations
        LLM-->>AG: tool_use block
        AG->>Tools: dispatch (team+scope injected)
        Tools->>API: REST call with Bearer token
        API-->>Tools: assets / data
        Tools-->>AG: result
        AG->>LLM: tool_result
    end
    LLM-->>AG: Final answer (text)
    AG-->>API: {answer, sources_used, assets}
    API-->>P: 200 + response
```

### Scenario 4: Asset Change Triggers a Skill

```mermaid
sequenceDiagram
    participant CDB as Cosmos DB
    participant CF as Change Feed Worker
    participant SR as Skill Registry
    participant SE as Skill Executor
    participant RO as Retrigger Orchestrator
    participant SB as Service Bus

    CDB->>CF: Change feed event (asset updated)
    CF->>SR: GetSkillsByTriggerType("asset_change")
    SR-->>CF: [scope-validator, ...]
    CF->>SE: ExecuteAsync(skillId, asset)
    SE->>SE: Evaluate trigger condition
    alt Condition matches
        SE->>RO: ScheduleRetriggerAsync
        RO->>SB: Enqueue connector-jobs message
    else No match
        SE-->>CF: Skipped
    end
```

### Scenario 5: Secret Rotation

```mermaid
sequenceDiagram
    participant Admin as Security Admin
    participant KV as Key Vault
    participant API as Recon API
    participant SR as SecretResolver

    Admin->>KV: Rotate secret value in Key Vault
    Admin->>API: POST /api/teams/alpha/secrets/rotate
    API->>API: Enforce team claim (JWT)
    API->>SR: InvalidateCacheForPrefix("alpha")
    Note over SR: TTL cache entries removed
    Note over SR: Next pull resolves fresh\nvalue from Key Vault
    API-->>Admin: 204 No Content
```

---

## Project Structure

```
ReconPlatform.sln
├── src/
│   ├── ReconPlatform.Api/          # ASP.NET Core 8 Web API — all HTTP endpoints
│   │   ├── Controllers/            # Teams, Recon, Engagements, Skills, Agent, Health
│   │   ├── Middleware/             # AuditLoggingMiddleware, SecretScrubMiddleware
│   │   └── Program.cs             # DI registration, auth config, middleware pipeline
│   ├── ReconPlatform.Connectors/   # Connector framework — REST, SQL, ADX, Plugin
│   │   ├── Interfaces/IConnector.cs
│   │   ├── RestApiConnector.cs
│   │   ├── AzureSqlConnector.cs
│   │   ├── AzureAdxConnector.cs
│   │   └── PluginLoader.cs
│   ├── ReconPlatform.Engine/       # Core business logic — dedup, diff, normalizer
│   │   ├── DeduplicationEngine.cs
│   │   ├── DiffEngine.cs
│   │   ├── Normalizer.cs
│   │   ├── RetriggerOrchestrator.cs
│   │   └── StalenessChecker.cs
│   ├── ReconPlatform.Storage/      # Azure storage clients
│   │   ├── BlobStorageClient.cs    # Parquet archive writes
│   │   ├── CosmosDbClient.cs       # Hot asset store
│   │   ├── SqlMetadataClient.cs    # Team configs, audit log
│   │   └── SynapseClient.cs        # Query layer on Parquet
│   ├── ReconPlatform.Workers/      # Background Container App workers
│   │   ├── ConnectorWorker/        # Service Bus consumer (KEDA triggered)
│   │   ├── StalenessTimer/         # Cron job every 6 hours
│   │   └── ChangeFeedWorker/       # Cosmos change feed poller
│   ├── ReconPlatform.Config/       # Config models, validation, secret resolution
│   │   ├── Models/                 # TeamConfig, SourceConfig, DeduplicationConfig
│   │   ├── Validator.cs
│   │   └── SecretResolver.cs
│   ├── ReconPlatform.Skills/       # Skill and sub-agent registry
│   │   ├── SkillRegistry.cs        # Loads YAML, hot-reloads on file change
│   │   ├── SkillExecutor.cs
│   │   └── Models/                 # SkillDefinition, AgentDefinition
│   └── ReconPlatform.Shared/       # Shared models — CanonicalAsset, interfaces
├── agent/                          # Python LLM agent (FastAPI, Claude tool-use)
│   ├── main.py                     # FastAPI app entry point
│   ├── orchestrator.py             # Claude tool-use loop, scope enforcement
│   ├── query_builder.py            # QueryPlan builder (LLM-assisted)
│   ├── tools/                      # Tool implementations (call back to C# API)
│   └── requirements.txt
├── plugins/                        # Drop-in connector plugins (C#)
│   └── ExamplePlugin.cs            # Starter template implementing IConnector
├── skills/                         # Config-driven skill definitions (YAML)
│   ├── agents/scope-validator.yaml # Built-in scope enforcement agent
│   └── asset-enrichment.yaml       # Example enrichment skill
├── infra/                          # Bicep IaC templates for all Azure resources
├── tests/
│   ├── ReconPlatform.UnitTests/    # Fast tests — no Azure services needed
│   └── ReconPlatform.IntegrationTests/
└── docs/                           # Full documentation (see below)
```

---

## Documentation Index

| Document | Description |
|---|---|
| [docs/setup.md](docs/setup.md) | Complete local development setup — prerequisites, emulators, running all services, troubleshooting |
| [docs/deployment.md](docs/deployment.md) | Production deployment — Bicep, CI/CD, secrets, scaling, rollback |
| [docs/configuration.md](docs/configuration.md) | Team config YAML reference — all connector types, dedup config, stale detection |
| [docs/extending-skills.md](docs/extending-skills.md) | Add new skills and agent tools without code changes |
| [docs/source-catalog.md](docs/source-catalog.md) | Source catalog design — named queries for the AI agent |
| [docs/soc2-checklist.md](docs/soc2-checklist.md) | SOC2 Type II readiness checklist |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Architecture decision records — storage choices, why Container Apps vs Functions |

---

## Contributing

**Branch naming:** `<type>/<short-description>` — e.g., `feat/new-adx-connector`, `fix/dedup-race-condition`

**Commit format:** `<type>(<scope>): <description>` — e.g., `feat(engine): add dedup conflict resolution`

Types: `feat`, `fix`, `docs`, `test`, `refactor`, `chore`

**Before opening a PR:**

- [ ] `dotnet build ReconPlatform.sln --configuration Release` passes with zero warnings
- [ ] `dotnet test tests/ReconPlatform.UnitTests` passes
- [ ] New public methods in Engine, Config, and Connectors have unit tests
- [ ] Test naming follows `MethodName_Scenario_ExpectedResult`
- [ ] No `TODO` comments — create a TASKS.md entry instead
- [ ] No secrets or `.env` file committed
- [ ] No `#pragma warning disable` without an explanatory comment

**Dependency security:**

- Pin all NuGet package versions in `*.csproj`
- Pin all Python packages in `requirements.txt`
- Check for known CVEs before adding a new package

---

## Security

This platform is built for SOC2 Type II compliance. Key security properties:

- All API endpoints require Entra ID Bearer tokens (except `GET /api/health`)
- Team claim in JWT is validated against the route team on every request — no cross-team access
- All secrets are stored in Azure Key Vault using `{{secret:KEY_NAME}}` references — never in code or config files
- Structured logging with automatic scrubbing of fields matching `*secret*`, `*password*`, `*key*`, `*token*`, `*connection*`
- Audit log written before every mutating operation (pre-action audit)
- All recon queries are scoped to the engagement — assets outside scope are never returned

See [docs/soc2-checklist.md](docs/soc2-checklist.md) for the full SOC2 readiness checklist.

**Reporting vulnerabilities:** Please report security issues privately to security@your-org.com. Do not open a public GitHub issue for security vulnerabilities.
