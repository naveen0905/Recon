# Recon Intelligence Platform — Build Tasks

## How to Use This File

Work through phases in order. Do not start Phase N+1 until Phase N acceptance criteria are met.
Mark tasks `[x]` as completed. Update "Current Phase" in `.claude/context.md` after each phase.

---

## Phase 1 — Core Scaffold (Week 1)

**Goal:** .NET 8 solution structure, config models, secret resolution, storage clients stubbed, de-dup models defined.

- [x] 1.1 Initialize Python agent stub (`requirements.txt`, `pyproject.toml`, `.gitignore`)
- [ ] 1.2 Create .NET 8 solution (`ReconPlatform.sln`) with all project stubs as defined in `ARCHITECTURE.md`
  - Each project has correct `<ProjectReference>` dependencies
  - Strict project boundary: no cross-cutting references outside `ReconPlatform.Shared`
  - `Directory.Build.props` — `TreatWarningsAsErrors`, `Nullable enable`, `LangVersion 12`
  - `Directory.Packages.props` — centralized NuGet version management
  - `global.json` pinning .NET 8 SDK
  - `.editorconfig`, `Makefile`, `.env.example`
  - CI workflow (`.github/workflows/ci.yml`)
  - Agent-friendly: `.claude/commands/` slash commands, `skills/` YAML stubs, `plugins/` stub
- [ ] 1.3 Implement `TeamConfig`, `SourceConfig`, `DeduplicationConfig` models (`ReconPlatform.Config/Models/`)
  - All YAML fields from ARCHITECTURE.md as C# record types
  - `rest_api`, `azure_sql`, `azure_adx`, `plugin` source types (enum discriminator)
  - `oauth2`, `api_key`, `bearer`, `managed_identity` auth types
  - `DeduplicationConfig`: `MatchKeys`, `ConflictResolution`, `SourcePriority`, optional `CustomResolver`
  - `stale_after_days` team-level with per-source override
  - YAML deserialization via `YamlDotNet`
- [ ] 1.4 Implement config validator (`ReconPlatform.Config/Validator.cs`)
  - Validate required fields per connector type
  - Validate auth type matches connector type constraints
  - Validate `dedup.match_keys` reference valid fields for the asset type
  - Return structured `ValidationResult` with per-field errors
- [ ] 1.5 Implement secret resolver (`ReconPlatform.Config/SecretResolver.cs`)
  - Resolve `{{secret:KEY_NAME}}` patterns from Azure Key Vault
  - Fall back to environment variables for local dev (guarded by `IsDevelopment()`)
  - Never log resolved secret values (SOC2)
  - Support secret rotation: re-resolve without restart
- [ ] 1.6 Implement Blob Storage client (`ReconPlatform.Storage/BlobStorageClient.cs`)
  - Write Parquet to `{team}/{source}/{year}/{month}/{day}/pull_{timestamp}.parquet`
  - List pulls for a given team/source/date range
  - `Azure.Storage.Blobs` SDK; managed identity preferred
- [ ] 1.7 Implement Cosmos DB client (`ReconPlatform.Storage/CosmosDbClient.cs`)
  - Upsert asset document with version increment
  - Run `DeduplicationEngine` before upsert to resolve conflicts
  - Compute and store diff between current and previous version
  - Query stale assets; mark `retrigger_scheduled=true`
  - `Microsoft.Azure.Cosmos` SDK
- [ ] 1.8 Implement Azure SQL client (`ReconPlatform.Storage/SqlMetadataClient.cs`)
  - CRUD for `team_configs`, `connector_run_log`, `engagements`, `user_permissions`, `audit_log`
  - `Microsoft.Data.SqlClient` with managed identity
  - All writes append to `audit_log` (SOC2)
- [ ] 1.9 Implement Synapse Serverless SQL client (`ReconPlatform.Storage/SynapseClient.cs`)
  - Execute T-SQL against external Parquet tables in Blob
  - Return `IEnumerable<Dictionary<string, object>>`
  - `Microsoft.Data.SqlClient` with Synapse serverless endpoint
- [ ] 1.10 Implement `DeduplicationEngine` (`ReconPlatform.Engine/DeduplicationEngine.cs`)
  - Compute `dedup_key` from configured `match_keys`
  - Resolve conflicts: `last_write`, `highest_confidence`, `source_priority`
  - Support dynamic `custom_resolver` plugin via `IDeduplicationResolver`
  - Update `contributing_sources` on successful merge
- [ ] 1.11 Implement `CanonicalAsset` shared model (`ReconPlatform.Shared/Models/CanonicalAsset.cs`)
  - All fields from canonical schema in ARCHITECTURE.md
  - `dedup_key`, `contributing_sources`, `confidence_score`, `source_priority`

**Acceptance Criteria:**
- `dotnet build ReconPlatform.sln` passes with zero warnings
- `dotnet test tests/ReconPlatform.UnitTests` passes:
  - `TeamConfigTests` — valid and invalid YAML parses correctly
  - `ValidatorTests` — required field and auth constraint errors returned
  - `DeduplicationEngineTests` — all three conflict strategies produce correct output
  - `SecretResolverTests` — `{{secret:X}}` resolved from mock Key Vault; falls back to env var
- All storage clients instantiate without error (mocked Azure SDK)

---

## Phase 2 — Connector Framework + Connectors (Week 2–2.5)

**Goal:** Pluggable connector system. All three connector types working. De-dup applied end-to-end.

- [ ] 2.1 Define `IConnector` interface (`ReconPlatform.Connectors/Interfaces/IConnector.cs`)
  - `Task<IEnumerable<Dictionary<string, object>>> PullAsync(SourceConfig config, CancellationToken ct)`
  - `Task<bool> TestConnectionAsync(SourceConfig config, CancellationToken ct)`
  - Shared retry policy via Polly (3 attempts, exponential backoff)
- [ ] 2.2 Implement `RestApiConnector` (`ReconPlatform.Connectors/RestApiConnector.cs`)
  - OAuth2 client credentials, API key (header/query), Bearer token
  - JSONPath field extraction
  - Pagination: cursor and offset patterns
  - `IHttpClientFactory` (never `new HttpClient()`)
- [ ] 2.3 Implement `AzureSqlConnector` (`ReconPlatform.Connectors/AzureSqlConnector.cs`)
  - Execute configured SQL query; managed identity + connection string auth
  - Parameterized queries only — no string interpolation
- [ ] 2.4 Implement `AzureAdxConnector` (`ReconPlatform.Connectors/AzureAdxConnector.cs`)
  - Execute KQL query; managed identity auth
  - `Microsoft.Azure.Kusto.Data` SDK
- [ ] 2.5 Implement plugin loader (`ReconPlatform.Connectors/PluginLoader.cs`)
  - Load connector from assembly/type name in config
  - Restrict plugin paths to `plugins/` directory (prevent path traversal)
  - Example plugin at `plugins/ExamplePlugin.cs`
- [ ] 2.6 Implement normalizer (`ReconPlatform.Engine/Normalizer.cs`)
  - Map raw source dict → `CanonicalAsset` using source `mapping` config
  - JSONPath for nested fields; fill defaults for missing optional fields
- [ ] 2.7 Implement diff engine (`ReconPlatform.Engine/DiffEngine.cs`)
  - Compare Cosmos `current` with new pull
  - Produce `added_*`, `removed_*`, `changed_*` per field
  - Only update `last_changed` on meaningful diff
- [ ] 2.8 Python agent scaffold (`agent/`)
  - FastAPI `/query` endpoint stub
  - Configurable LLM provider via `agent/config.yaml` (anthropic or azure_openai)
  - Tool stubs: `query_assets`, `get_asset_history`, `trigger_pull`, `get_stale_assets`

**Acceptance Criteria:**
- `dotnet test` passes: `ConnectorTests`, `NormalizerTests`, `DiffEngineTests`
- De-dup correctly merges a host seen in two sources in `DeduplicationEngineTests`
- Plugin loader loads `ExamplePlugin` from config path without code changes

---

## Phase 3 — Retrigger Engine + Workers (Week 3)

**Goal:** Staleness detection, Service Bus integration, Container App workers.

- [ ] 3.1 Implement staleness checker (`ReconPlatform.Engine/StalenessChecker.cs`)
  - Compute `is_stale` based on `last_pulled` vs `stale_after_days`
  - Bulk update stale flags in Cosmos
- [ ] 3.2 Implement retrigger orchestrator (`ReconPlatform.Engine/RetriggerOrchestrator.cs`)
  - Accept asset ID or full team name
  - Resolve which sources to pull from
  - Push jobs to Service Bus
- [ ] 3.3 Implement Staleness Timer worker (`ReconPlatform.Workers/StalenessTimer/`)
  - Scheduled job, cron: `0 */6 * * *`
  - Calls staleness checker → pushes stale assets to Service Bus
  - Logs run summary to `connector_run_log`
- [ ] 3.4 Implement Connector Worker (`ReconPlatform.Workers/ConnectorWorker/`)
  - KEDA Service Bus trigger
  - Pull → normalize → dedup → write Parquet → upsert Cosmos
  - Update `connector_run_log` including `assets_deduped` count
- [ ] 3.5 Implement Change Feed Worker (`ReconPlatform.Workers/ChangeFeedWorker/`)
  - Poll Cosmos change feed
  - Evaluate matching skill definitions from `SkillRegistry`
  - Execute triggered skills
- [ ] 3.6 Retrigger API endpoint (`POST /api/recon/retrigger`)
  - Validate team + target exist
  - Push to Service Bus; return job ID

**Acceptance Criteria:**
- Staleness timer produces correct Service Bus messages given mock Cosmos state
- Connector Worker processes mock Service Bus message end-to-end
- Cosmos document version increments correctly on second pull
- De-duped count logged in `connector_run_log`

---

## Phase 4 — Skills Registry + API Layer (Week 3.5–4)

**Goal:** Full API, skill/agent registration, scope enforcement.

- [ ] 4.1 Implement `SkillRegistry` and `SkillExecutor` (`ReconPlatform.Skills/`)
  - Load YAML from `skills/` at startup; watch for file changes
  - Validate skill YAML against schema on load
- [ ] 4.2 Full ASP.NET Core 8 API (`ReconPlatform.Api/Program.cs`)
  - Entra ID auth (per-team app registration)
  - `AuditLoggingMiddleware` — every request logged to `audit_log` before operation
  - `SecretScrubMiddleware` — scrub `*secret*`, `*password*`, `*key*`, `*token*`, `*conn*` from logs
  - Structured logging via Serilog
- [ ] 4.3 Teams router — full CRUD + test connection endpoint
- [ ] 4.4 Recon router — pull, retrigger, asset query (Synapse), asset detail + diff (Cosmos)
- [ ] 4.5 Engagements router — create, list, scoped asset query
- [ ] 4.6 Skills router — register (`POST /api/skills`), list, delete
- [ ] 4.7 Health endpoint — per-component status (Cosmos, Blob, SQL, Synapse, Service Bus)

**Acceptance Criteria:**
- `dotnet test tests/ReconPlatform.IntegrationTests` passes against test client
- Skill YAML registered via API picked up by `SkillRegistry` without restart
- Unauthenticated requests → 401; cross-team requests → 403

---

## Phase 5 — Agent Interface (Week 4.5)

**Goal:** Python LLM agent queries recon data conversationally within engagement scope.

- [ ] 5.1 Implement query builder (`agent/query_builder.py`)
  - Natural language intent → Synapse T-SQL or Cosmos query
- [ ] 5.2 Implement agent orchestrator (`agent/orchestrator.py`)
  - Tool definitions driven by `skills/agents/*.yaml`
  - Scope enforcement: cannot return assets outside engagement scope
- [ ] 5.3 `POST /api/agent/query` in C# API (proxies to Python agent Container App)
- [ ] 5.4 Agent skill YAML example (`skills/agents/scope-validator.yaml`)

**Acceptance Criteria:**
- Agent correctly routes 5 test queries to the right tool
- Agent cannot return assets outside engagement scope
- New agent skill added via YAML without code change

---

## Phase 6 — Hardening (Week 5)

**Goal:** Production-ready audit, guardrails, error handling.

- [ ] 6.1 Audit logging — every API call and connector run writes structured event to `audit_log` table; verify no sensitive fields leak
- [ ] 6.2 Scope enforcement — all recon queries (`GET /api/recon/assets`, Synapse, Cosmos) validate caller's team claim against engagement scope; add unit tests for out-of-scope rejection
- [ ] 6.3 Rate limiting per connector — max pulls per hour per source enforced in `RetriggerOrchestrator`; excess requests rejected with 429 + logged
- [ ] 6.4 Retry + dead-letter handling for Service Bus — failed messages retried 3× with exponential backoff; dead-lettered messages logged to `connector_run_log` with `status=dead_lettered`; Azure Monitor alert wired
- [ ] 6.5 Secret rotation support — `SecretResolver` re-resolves Key Vault secrets on each use (with per-secret TTL cache); verify rotation takes effect without restart
- [ ] 6.6 Integration tests against real Azure services — test resource group in East US 2; full pull + retrigger cycle end-to-end; Cosmos version increment verified; Blob Parquet verified
- [ ] 6.7 Secret scrubbing verified by test — assert no field matching scrub pattern appears in Serilog output after a full pull run

**Acceptance Criteria:**
- No secrets in logs or API responses (verified by test)
- Dead-lettered messages are logged and produce an Azure Monitor alert
- Full pull + retrigger cycle works end-to-end in Azure test environment
- All recon endpoints reject out-of-scope requests with 403

---

## Phase 7 — Infrastructure + Docs (Week 6)

**Goal:** Bicep IaC for all Azure resources, full documentation.

- [ ] 7.1 Bicep templates (`infra/`)
  - Container Apps environment + all app/job definitions (API, 3 workers)
  - Blob, Cosmos, SQL Serverless, Synapse Serverless, Service Bus, Key Vault
  - Managed Identity assignments with least-privilege RBAC
  - Log Analytics workspace + Azure Monitor alerts (dead-letter, anomalous pull volume)
  - `WORKER_TYPE` env var per Container App deployment
- [ ] 7.2 `docs/setup.md` — local dev prerequisites, env vars, first run
- [ ] 7.3 `docs/deployment.md` — Bicep deploy steps, CI/CD pipeline, environment promotion
- [ ] 7.4 `docs/configuration.md` — full team YAML config reference, all fields with types and defaults
- [ ] 7.5 `docs/extending-skills.md` — adding new skill YAML, new connector plugin, new agent tool
- [ ] 7.6 `README.md` — platform overview, architecture diagram, quick start
- [ ] 7.7 SOC2 checklist
  - Dependabot enabled
  - Container image scanning in CI (`trivy`)
  - Audit log retention ≥ 1 year verified in Log Analytics
  - CODEOWNERS file present

**Acceptance Criteria:**
- `az deployment group create` with `infra/main.bicep` succeeds in test resource group
- All docs reviewed and accurate against implemented code
- Trivy scan shows zero critical CVEs in container images

---

## Dependencies

### C# (.NET 8) — see `Directory.Packages.props` for pinned versions
```
Microsoft.Azure.Cosmos
Azure.Storage.Blobs
Azure.Identity
Azure.Security.KeyVault.Secrets
Azure.Messaging.ServiceBus
Microsoft.Data.SqlClient
Microsoft.Azure.Kusto.Data
YamlDotNet
Polly
Microsoft.Extensions.Http.Resilience
Newtonsoft.Json
Microsoft.AspNetCore.Authentication.JwtBearer
Microsoft.Identity.Web
Serilog.AspNetCore
xunit + Moq + FluentAssertions
```

### Python (agent service) — see `requirements.txt`
```
fastapi
uvicorn
httpx
anthropic
pydantic>=2.0
pyyaml
pytest
```
