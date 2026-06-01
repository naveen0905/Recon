# Recon Intelligence Platform — Build Tasks

## How to Use This File

Work through phases in order. Do not start Phase N+1 until Phase N acceptance criteria are met.
Mark tasks `[x]` as completed. Update "Current Phase" in `.claude/context.md` after each phase.

---

## Phase 1 — Core Scaffold (Week 1)

**Goal:** .NET 8 solution structure, config models, secret resolution, storage clients stubbed, de-dup models defined.

- [x] 1.1 Initialize Python project stub (`requirements.txt`, `pyproject.toml`, `.gitignore`) — agent service baseline
- [ ] 1.2 Create .NET 8 solution (`ReconPlatform.sln`) with all project stubs as defined in `ARCHITECTURE.md`
  - Each project has correct `<ProjectReference>` dependencies
  - Strict project boundary: no cross-cutting references outside of `ReconPlatform.Shared`
  - Add `.gitignore` entries for `bin/`, `obj/`, `.vs/`
- [ ] 1.3 Implement `TeamConfig`, `SourceConfig`, `DeduplicationConfig` models (`ReconPlatform.Config/Models/`)
  - All YAML fields from ARCHITECTURE.md config schema as C# record types
  - Support `rest_api`, `azure_sql`, `azure_adx`, `plugin` source types (discriminated union via enum)
  - Support `oauth2`, `api_key`, `bearer`, `managed_identity` auth types
  - `DeduplicationConfig`: `MatchKeys`, `ConflictResolution`, `SourcePriority`, optional `CustomResolver`
  - `stale_after_days` at team level with per-source override
  - YAML deserialization via `YamlDotNet`
- [ ] 1.4 Implement config validator (`ReconPlatform.Config/Validator.cs`)
  - Validate required fields per connector type
  - Validate auth type matches connector type constraints
  - Validate `dedup.match_keys` reference valid fields for asset type
  - Return structured `ValidationResult` with per-field errors
- [ ] 1.5 Implement secret resolver (`ReconPlatform.Config/SecretResolver.cs`)
  - Resolve `{{secret:KEY_NAME}}` patterns from Azure Key Vault (`Azure.Security.KeyVault.Secrets`)
  - Fall back to environment variables for local dev
  - Never log resolved secret values (SOC2)
  - Support secret rotation: re-resolve without restart
- [ ] 1.6 Implement Blob Storage client (`ReconPlatform.Storage/BlobStorageClient.cs`)
  - Write Parquet files to `{team}/{source}/{year}/{month}/{day}/pull_{timestamp}.parquet`
  - List pulls for a given team/source/date range
  - Uses `Azure.Storage.Blobs` SDK
  - Managed identity auth preferred; connection string fallback for local dev
- [ ] 1.7 Implement Cosmos DB client (`ReconPlatform.Storage/CosmosDbClient.cs`)
  - Upsert asset document with version increment
  - Run `DeduplicationEngine` before upsert to resolve conflicts
  - Compute and store diff between current and previous version
  - Query stale assets (`is_stale=true` or `last_pulled < threshold`)
  - Mark asset as `retrigger_scheduled=true`
  - Uses `Microsoft.Azure.Cosmos` SDK
- [ ] 1.8 Implement Azure SQL client (`ReconPlatform.Storage/SqlMetadataClient.cs`)
  - CRUD for `team_configs`, `connector_run_log`, `engagements`, `user_permissions`, `audit_log`
  - Uses `Microsoft.Data.SqlClient` with managed identity
  - All writes to `audit_log` for SOC2
- [ ] 1.9 Implement Synapse Serverless SQL client (`ReconPlatform.Storage/SynapseClient.cs`)
  - Execute T-SQL queries against external Parquet tables in Blob
  - Return results as `IEnumerable<Dictionary<string, object>>`
  - Uses `Microsoft.Data.SqlClient` with Synapse serverless endpoint
- [ ] 1.10 Implement `DeduplicationEngine` (`ReconPlatform.Engine/DeduplicationEngine.cs`)
  - Compute `dedup_key` from configured `match_keys` for a given asset + source config
  - Resolve conflicts between incoming asset and existing Cosmos document per `ConflictResolution` strategy
  - Support `last_write`, `highest_confidence`, `source_priority`
  - Support dynamic loading of `custom_resolver` plugin class
  - Update `contributing_sources` list on successful merge
- [ ] 1.11 Create `CanonicalAsset` shared model (`ReconPlatform.Shared/Models/CanonicalAsset.cs`)
  - All fields from canonical schema in ARCHITECTURE.md
  - `dedup_key`, `contributing_sources`, `confidence_score`, `source_priority`

**Acceptance Criteria:**
- `dotnet build ReconPlatform.sln` passes with zero warnings (treat warnings as errors)
- `dotnet test tests/ReconPlatform.UnitTests` passes
  - `TeamConfigTests`: valid and invalid YAML parses correctly
  - `ValidatorTests`: required field and auth constraint errors returned correctly
  - `DeduplicationEngineTests`: all three conflict strategies produce correct output
  - `SecretResolverTests`: `{{secret:X}}` resolved from mock Key Vault; falls back to env var
- All storage clients instantiate without error (mocked Azure SDK calls)

---

## Phase 2 — Connector Framework + Connectors (Week 2–2.5)

**Goal:** Pluggable connector system. All three connector types working. De-dup applied end-to-end.

- [ ] 2.1 Define `IConnector` interface (`ReconPlatform.Connectors/Interfaces/IConnector.cs`)
  - `Task<IEnumerable<Dictionary<string, object>>> PullAsync(SourceConfig config, CancellationToken ct)`
  - `Task<bool> TestConnectionAsync(SourceConfig config, CancellationToken ct)`
  - Shared retry policy via Polly (3 attempts, exponential backoff)
- [ ] 2.2 Implement `RestApiConnector` (`ReconPlatform.Connectors/RestApiConnector.cs`)
  - OAuth2 client credentials, API key (header/query), Bearer token
  - JSONPath field extraction via `Newtonsoft.Json` or `System.Text.Json` + JSONPath
  - Pagination: cursor and offset patterns
  - Uses `HttpClient` (IHttpClientFactory)
- [ ] 2.3 Implement `AzureSqlConnector` (`ReconPlatform.Connectors/AzureSqlConnector.cs`)
  - Execute configured SQL query; managed identity + connection string auth
  - Return rows as `IEnumerable<Dictionary<string, object>>`
- [ ] 2.4 Implement `AzureAdxConnector` (`ReconPlatform.Connectors/AzureAdxConnector.cs`)
  - Execute KQL query; managed identity auth
  - Uses `Microsoft.Azure.Kusto.Data` SDK
- [ ] 2.5 Implement plugin loader (`ReconPlatform.Connectors/PluginLoader.cs`)
  - Load connector from assembly path or type name in config
  - Validate implements `IConnector`
  - Example plugin at `plugins/ExamplePlugin.cs`
- [ ] 2.6 Implement normalizer (`ReconPlatform.Engine/Normalizer.cs`)
  - Map raw source dict → `CanonicalAsset` using source `mapping` config
  - JSONPath for nested source fields
  - Fill defaults for missing optional fields
- [ ] 2.7 Implement diff engine (`ReconPlatform.Engine/DiffEngine.cs`)
  - Compare current Cosmos document `current` with new pull
  - Produce diff: `added_*`, `removed_*`, `changed_*` per field
  - Only update `last_changed` on meaningful diff
- [ ] 2.8 Write Python agent service scaffold (`agent/`)
  - FastAPI app with `/query` endpoint
  - Placeholder LLM call (configurable: Azure OpenAI or Anthropic via `agent/config.yaml`)
  - Tool stubs: `query_assets`, `get_asset_history`, `trigger_pull`, `get_stale_assets`

**Acceptance Criteria:**
- `dotnet test` passes: `ConnectorTests`, `NormalizerTests`, `DiffEngineTests`
- De-dup correctly merges a host seen in two sources in `DeduplicationEngineTests`
- Plugin loader loads `ExamplePlugin` from config path

---

## Phase 3 — Retrigger Engine + Workers (Week 3)

**Goal:** Staleness detection, Service Bus integration, Container App workers.

- [ ] 3.1 Implement staleness checker (`ReconPlatform.Engine/StalenessChecker.cs`)
- [ ] 3.2 Implement retrigger orchestrator (`ReconPlatform.Engine/RetriggerOrchestrator.cs`)
- [ ] 3.3 Implement Staleness Timer worker (`ReconPlatform.Workers/StalenessTimer/`)
  - Container App scheduled job, cron: `0 */6 * * *`
- [ ] 3.4 Implement Connector Worker (`ReconPlatform.Workers/ConnectorWorker/`)
  - KEDA Service Bus trigger
  - Pull → normalize → dedup → write Parquet → upsert Cosmos
  - Update `connector_run_log` including `assets_deduped` count
- [ ] 3.5 Implement Change Feed Worker (`ReconPlatform.Workers/ChangeFeedWorker/`)
  - Poll Cosmos change feed
  - Evaluate matching skill definitions from `SkillRegistry`
  - Execute triggered skills
- [ ] 3.6 Implement retrigger API endpoint (`POST /api/recon/retrigger`)

**Acceptance Criteria:**
- Staleness timer produces correct Service Bus messages given mock Cosmos state
- Connector Worker processes mock Service Bus message end-to-end
- Cosmos document version increments correctly on second pull
- De-duped count logged in `connector_run_log`

---

## Phase 4 — Skills Registry + API Layer (Week 3.5–4)

**Goal:** Full API, skill/agent registration, scope enforcement.

- [ ] 4.1 Implement `SkillRegistry` and `SkillExecutor` (`ReconPlatform.Skills/`)
  - Load YAML from `skills/` directory at startup
  - Watch for file changes; reload without restart
  - Validate skill YAML against schema on load
- [ ] 4.2 Full ASP.NET Core 8 API setup (`ReconPlatform.Api/Program.cs`)
  - Entra ID authentication (per-team app registration)
  - Global error handling; structured logging
  - `AuditLoggingMiddleware` — every request logged to `audit_log`
  - `SecretScrubMiddleware` — scrub sensitive field names from logs
- [ ] 4.3 Teams router — full CRUD + test connection
- [ ] 4.4 Recon router — pull, retrigger, asset query, diff
- [ ] 4.5 Engagements router — create, list, scoped asset query
- [ ] 4.6 Skills router — register, list, delete skill configs
- [ ] 4.7 Health endpoint — per-component status

**Acceptance Criteria:**
- `dotnet test tests/ReconPlatform.IntegrationTests` passes against test client
- Skill YAML registered via API is picked up by `SkillRegistry` without restart
- Unauthenticated requests return 401; cross-team requests return 403

---

## Phase 5 — Agent Interface (Week 4.5)

**Goal:** Python LLM agent queries recon data conversationally within engagement scope.

- [ ] 5.1 Implement query builder (`agent/query_builder.py`)
- [ ] 5.2 Implement agent orchestrator (`agent/orchestrator.py`)
  - Tool definitions driven by `skills/agents/*.yaml`
  - Scope enforcement: cannot return assets outside engagement scope
- [ ] 5.3 Expose `POST /api/agent/query` from C# API (proxies to Python agent Container App)
- [ ] 5.4 Agent YAML skill definition example (`skills/agents/scope-validator.yaml`)

**Acceptance Criteria:**
- Agent correctly routes 5 test queries to the right tool
- Scope enforcement: agent cannot return assets outside engagement scope
- New agent skill added via YAML without code change

---

## Phase 6 — Infrastructure + Docs (Week 5)

**Goal:** Bicep IaC, READMEs, SOC2 hardening.

- [ ] 6.1 Bicep templates for all Azure resources (`infra/`)
  - Container Apps environment + all app definitions
  - Blob, Cosmos, SQL, Synapse, Service Bus, Key Vault
  - Managed Identity assignments with least-privilege RBAC
  - Log Analytics workspace + Azure Monitor alerts
- [ ] 6.2 `docs/setup.md` — local dev setup, prerequisites, env vars
- [ ] 6.3 `docs/deployment.md` — Bicep deploy steps, CI/CD pipeline outline
- [ ] 6.4 `docs/configuration.md` — team YAML config reference, all fields documented
- [ ] 6.5 `docs/extending-skills.md` — how to add new skill YAML, new connector plugin, new agent tool
- [ ] 6.6 `README.md` — platform overview, architecture diagram, quick start
- [ ] 6.7 SOC2 hardening checklist
  - Dependabot enabled
  - Container image scanning in CI (`trivy` or equivalent)
  - No secrets in logs verified by test
  - Dead-letter queue alert configured
  - Audit log retention ≥ 1 year in Log Analytics

**Acceptance Criteria:**
- `az deployment group create` with `infra/main.bicep` succeeds in test resource group
- Full pull + retrigger cycle works end-to-end in Azure test environment
- No secrets appear in Log Analytics after a full pull run

---

## Dependencies

### C# (.NET 8)
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
Newtonsoft.Json
Microsoft.AspNetCore.Authentication.JwtBearer
Microsoft.Identity.Web
Serilog.AspNetCore
xunit
Moq
FluentAssertions
```

### Python (agent service)
```
fastapi
uvicorn
httpx
anthropic   # or openai — configurable per agent YAML
pydantic>=2.0
jsonpath-ng
pyyaml
pytest
pytest-asyncio
```
