# Recon Intelligence Platform — Build Tasks

## How to Use This File

Work through phases in order. Do not start Phase N+1 until Phase N acceptance criteria are met.
Mark tasks `[x]` as completed. Update "Current Phase" in `.claude/context.md` after each phase.

---

## Phase 1 — Core Scaffold (Week 1)

**Goal:** Repo structure, config models, secret resolution, storage clients stubbed.

- [ ] 1.1 Initialize Python project with `pyproject.toml`, `requirements.txt`, `.gitignore`
- [ ] 1.2 Create full directory structure as defined in `ARCHITECTURE.md`
- [ ] 1.3 Implement `TeamConfig` and `SourceConfig` Pydantic models (`src/config/models.py`)
  - All YAML fields from ARCHITECTURE.md config schema
  - Support `rest_api`, `azure_sql`, `azure_adx`, `plugin` source types
  - Support `oauth2`, `api_key`, `bearer`, `managed_identity` auth types
  - `stale_after_days` at team level with per-source override
- [ ] 1.4 Implement config validator (`src/config/validator.py`)
  - Validate required fields per connector type
  - Validate auth type matches connector type constraints
  - Return structured validation errors
- [ ] 1.5 Implement secret resolver (`src/config/secrets.py`)
  - Resolve `{{secret:KEY_NAME}}` patterns from Azure Key Vault
  - Fall back to environment variables for local dev
- [ ] 1.6 Implement Blob Storage client (`src/storage/blob.py`)
  - Write Parquet files to `{team}/{source}/{year}/{month}/{day}/pull_{timestamp}.parquet`
  - List pulls for a given team/source/date range
  - Uses `azure-storage-blob` SDK
- [ ] 1.7 Implement Cosmos DB client (`src/storage/cosmos.py`)
  - Upsert asset document with version increment
  - Compute and store diff between current and previous version
  - Query stale assets (`is_stale=true` or `last_pulled < threshold`)
  - Mark asset as `retrigger_scheduled=true`
  - Uses `azure-cosmos` SDK
- [ ] 1.8 Implement Azure SQL client (`src/storage/sql.py`)
  - CRUD for `team_configs`, `connector_run_log`, `engagements`, `user_permissions`
  - Uses `pyodbc` + connection string from Key Vault
- [ ] 1.9 Implement Synapse Serverless SQL client (`src/storage/synapse.py`)
  - Execute T-SQL queries against external Parquet tables in Blob
  - Return results as list of dicts
  - Uses `pyodbc` with Synapse serverless endpoint

**Acceptance Criteria:**
- `pytest tests/unit/test_config_models.py` passes
- `pytest tests/unit/test_normalizer.py` passes
- All storage clients instantiate without error (mocked Azure SDK calls)

---

## Phase 2 — Connector Framework + Connectors (Week 2–2.5)

**Goal:** Pluggable connector system. All three connector types working.

- [ ] 2.1 Implement `BaseConnector` abstract class (`src/connectors/base.py`)
  - Abstract method: `pull(source_config: SourceConfig) -> list[dict]`
  - Abstract method: `test_connection(source_config: SourceConfig) -> bool`
  - Shared retry logic (3 attempts, exponential backoff)
  - Shared logging interface
- [ ] 2.2 Implement `RestApiConnector` (`src/connectors/rest_api.py`)
  - Support OAuth2 client credentials, API key (header/query), Bearer token
  - JSONPath mapping (`$.data[*].hostname` style field extraction)
  - Pagination support (cursor and offset patterns)
  - Uses `httpx` (async)
- [ ] 2.3 Implement `AzureSqlConnector` (`src/connectors/azure_sql.py`)
  - Execute configured SQL query
  - Support managed identity + connection string auth
  - Return rows as list of dicts
- [ ] 2.4 Implement `AzureAdxConnector` (`src/connectors/azure_adx.py`)
  - Execute configured KQL query against ADX cluster
  - Support managed identity auth
  - Uses `azure-kusto-data` SDK
- [ ] 2.5 Implement plugin loader (`src/connectors/plugin_loader.py`)
  - Dynamically load connector class from `plugin_class` dotted path
  - Validate implements `BaseConnector`
  - Example plugin at `plugins/example_plugin.py`
- [ ] 2.6 Implement normalizer (`src/engine/normalizer.py`)
  - Map raw source dict → canonical schema using source `mapping` config
  - JSONPath for nested source fields
  - Fill defaults for missing optional fields
  - Return `CanonicalAsset` Pydantic model
- [ ] 2.7 Implement diff engine (`src/engine/diff.py`)
  - Compare current Cosmos document `current` with new pull
  - Produce diff: `added_*`, `removed_*`, `changed_*` per field
  - Determine if `last_changed` should be updated (only on meaningful diff)

**Acceptance Criteria:**
- `pytest tests/unit/test_connectors.py` passes (mocked HTTP + Azure SDK)
- `pytest tests/unit/test_normalizer.py` passes
- `pytest tests/unit/test_diff.py` passes with known before/after fixtures

---

## Phase 3 — Retrigger Engine (Week 3)

**Goal:** Staleness detection, Service Bus integration, Connector Worker function.

- [ ] 3.1 Implement staleness checker (`src/engine/staleness.py`)
  - Compute `is_stale` based on `last_pulled` vs `stale_after_days`
  - Bulk update stale flags in Cosmos
- [ ] 3.2 Implement retrigger orchestrator (`src/engine/retrigger.py`)
  - Accept asset ID or full team name
  - Resolve which sources to pull from
  - Push jobs to Service Bus
- [ ] 3.3 Implement Azure Function: Timer Staleness (`src/functions/timer_staleness/`)
  - Runs every 6 hours (cron: `0 */6 * * *`)
  - Calls staleness checker → pushes stale assets to Service Bus
  - Logs run summary to `connector_run_log`
- [ ] 3.4 Implement Azure Function: Connector Worker (`src/functions/connector_worker/`)
  - Service Bus trigger
  - Resolves team config from Azure SQL
  - Instantiates correct connector
  - Pulls data → normalizes → writes Parquet to Blob → upserts Cosmos
  - Updates `connector_run_log` on success/failure
- [ ] 3.5 Implement Azure Function: Change Feed Handler (`src/functions/change_feed/`)
  - Cosmos DB change feed trigger
  - If new asset: log, optionally trigger cross-source enrichment
  - If severity → `critical`: push retrigger job for all sources on that asset
- [ ] 3.6 Implement retrigger API endpoint (`POST /api/recon/retrigger`)
  - Validate team + target exist
  - Push to Service Bus
  - Return job ID

**Acceptance Criteria:**
- Timer function runs locally with `func start` and produces Service Bus messages
- Connector Worker processes a mock Service Bus message end-to-end
- Cosmos document version increments correctly on second pull

---

## Phase 4 — API Layer (Week 3.5–4)

**Goal:** Full FastAPI application exposing all platform capabilities.

- [ ] 4.1 FastAPI app setup (`src/api/main.py`)
  - Lifespan management (init storage clients on startup)
  - Global error handling
  - Request logging middleware
- [ ] 4.2 Teams router (`src/api/routers/teams.py`)
  - `POST /api/teams/{team}/sources` — register source (validate + store config)
  - `GET /api/teams/{team}/sources` — list sources
  - `PUT /api/teams/{team}/sources/{id}` — update config
  - `DELETE /api/teams/{team}/sources/{id}` — remove source
  - `POST /api/teams/{team}/sources/{id}/test` — test connection
- [ ] 4.3 Recon router (`src/api/routers/recon.py`)
  - `POST /api/recon/pull` — ad-hoc full pull for team
  - `POST /api/recon/retrigger` — retrigger asset or team
  - `GET /api/recon/assets` — query via Synapse (filter by team, type, severity, tags)
  - `GET /api/recon/assets/{id}` — get from Cosmos with full history
  - `GET /api/recon/assets/{id}/diff?v1=2&v2=4` — diff two versions
- [ ] 4.4 Engagements router (`src/api/routers/engagements.py`)
  - `POST /api/engagements` — create with scope
  - `GET /api/engagements` — list active engagements
  - `GET /api/engagements/{id}/assets` — assets in scope (Synapse query scoped to engagement)
- [ ] 4.5 Health endpoint (`GET /api/health`)
  - Check connectivity to Cosmos, Blob, Synapse, SQL, Service Bus
  - Return per-component status

**Acceptance Criteria:**
- `pytest tests/integration/test_api.py` passes against test client
- All endpoints return correct status codes for happy path + error cases

---

## Phase 5 — Agent Interface (Week 4.5)

**Goal:** LLM can query recon data and trigger pulls conversationally.

- [ ] 5.1 Query builder (`src/agent/query_builder.py`)
  - Accept natural language intent → build Synapse T-SQL or Cosmos query
  - "Show all internet-facing assets for team X" → SQL
  - "What changed for server01 in last 7 days" → Cosmos history query
- [ ] 5.2 Agent orchestrator (`src/agent/orchestrator.py`)
  - Tool definitions for: `query_assets`, `get_asset_history`, `trigger_pull`, `get_stale_assets`
  - LLM decides which tool to call based on user intent
  - Returns structured response with citations (which sources)
- [ ] 5.3 Agent API endpoint (`POST /api/agent/query`)
  - Accept free-text query + optional engagement scope
  - Run orchestrator
  - Return answer + supporting asset list

**Acceptance Criteria:**
- Agent correctly routes 5 test queries to the right tool
- Scope enforcement: agent cannot return assets outside engagement scope

---

## Phase 6 — Hardening (Week 5)

**Goal:** Production-ready audit, guardrails, error handling.

- [ ] 6.1 Audit logging — every API call writes to `connector_run_log`
- [ ] 6.2 Scope enforcement — all recon queries validate against engagement scope
- [ ] 6.3 Rate limiting per connector — max pulls per hour per source
- [ ] 6.4 Retry + dead-letter handling for Service Bus messages
- [ ] 6.5 Secret rotation support — re-resolve Key Vault secrets without restart
- [ ] 6.6 Integration tests against real Azure services (using test resource group)
- [ ] 6.7 README.md — setup, local dev, deployment guide

**Acceptance Criteria:**
- No secrets in logs or API responses
- Dead-lettered messages are logged and alertable
- Full pull + retrigger cycle works end-to-end in Azure test environment

---

## Dependencies (requirements.txt baseline)

```
fastapi
uvicorn
pydantic>=2.0
httpx
azure-storage-blob
azure-cosmos
azure-keyvault-secrets
azure-identity
azure-kusto-data
azure-servicebus
pyodbc
pandas
pyarrow
jsonpath-ng
pytest
pytest-asyncio
```
