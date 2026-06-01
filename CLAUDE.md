# Recon Intelligence Platform — Agentic Coding Guidelines

This file governs how Claude Code agents work in this repository. Read it every session before writing code.

---

## Language & Framework Rules

- **C# .NET 8** for all platform services (API, connectors, storage, engine, workers)
- **Python 3.11+** only for the agent/LLM layer (`agent/`)
- **ASP.NET Core 8** for the API project
- **FastAPI** for the Python agent service
- Use `record` types for immutable models in C#; `class` only when mutability is required
- Use `IOptions<T>` pattern for configuration injection; never read `Environment.GetEnvironmentVariable` directly in business logic
- Use `IHttpClientFactory` — never `new HttpClient()`
- All async methods must use `CancellationToken`

---

## Project Boundary Rules

The solution has strict dependency rules. Violating these breaks future repo-split:

```
ReconPlatform.Api         → Config, Connectors, Storage, Engine, Skills, Shared
ReconPlatform.Workers     → Config, Connectors, Storage, Engine, Skills, Shared
ReconPlatform.Connectors  → Config, Shared
ReconPlatform.Storage     → Config, Shared
ReconPlatform.Engine      → Config, Storage, Shared
ReconPlatform.Skills      → Config, Shared
ReconPlatform.Config      → Shared only
ReconPlatform.Shared      → no internal dependencies
```

No project may reference `Api` or `Workers`. No circular references.

---

## Security Rules (SOC2 + Zero-Day Prevention)

### Secrets
- Never hardcode secrets, connection strings, or tokens
- Always use `{{secret:KEY_NAME}}` resolved by `SecretResolver` from Azure Key Vault
- Environment variable fallback is only for local dev — guarded by `IsDevelopment()` check
- Never log, return in API responses, or write to Blob/Cosmos any field that resolves to a secret

### Input Validation
- Validate all external inputs at API boundary using data annotations or FluentValidation
- Parameterize all SQL queries — no string interpolation in SQL
- Sanitize all JSONPath expressions from user-supplied configs before evaluation
- Reject YAML configs that reference paths outside the `plugins/` directory for plugin classes

### Authentication & Authorization
- All API endpoints require Entra ID Bearer token except `GET /api/health`
- Validate team claim in token against requested team in route — no cross-team access
- All recon queries must filter by engagement scope — never return assets outside scope
- Log every authorization decision to `audit_log`

### Logging (SOC2)
- Use structured logging (Serilog) — never `Console.WriteLine` in production code
- Scrub fields matching `*secret*`, `*password*`, `*key*`, `*token*`, `*connection*`, `*conn*` before logging
- Every API request: log actor, team, resource, action, timestamp, IP
- Every connector run: log team, source, assets pulled, assets deduped, status
- Audit log must be written before the operation completes (pre-action audit)

### Dependency Security
- Pin all NuGet package versions in `*.csproj` files
- Pin all Python packages in `requirements.txt`
- No packages with known critical CVEs — check before adding

---

## Code Quality Rules

- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in all `.csproj` files
- Enable nullable reference types: `<Nullable>enable</Nullable>`
- No `#pragma warning disable` without an explicit comment explaining why
- No `TODO` comments in committed code — create a TASKS.md entry instead
- Write tests for every public method in Engine, Config, and Connectors projects
- Test naming: `MethodName_Scenario_ExpectedResult`
- Use `FluentAssertions` for C# test assertions
- Mock Azure SDK calls in unit tests — never call real Azure services in unit tests

---

## De-Duplication Rules

- Every upsert to Cosmos must go through `DeduplicationEngine` first
- `dedup_key` must be computed from `match_keys` defined in source config — never hardcoded
- Conflict resolution strategy must be read from config — never assume `last_write` as default
- Log `assets_deduped` count in `connector_run_log` on every run
- Custom dedup resolvers must implement `IDeduplicationResolver` interface

---

## Skills / Plugin Extension Rules

- New connector types: implement `IConnector`, register in `PluginLoader` config — no changes to core
- New skill triggers: add entry to `skills/` YAML directory — no code change required
- New agent tools: add tool definition to agent YAML under `skills/agents/` — no code change required
- Plugin class paths in YAML are restricted to `plugins/` directory (security: prevent path traversal)
- Skill YAML must pass schema validation before `SkillRegistry` accepts it
- Agents may generate and POST new skill YAML to `/api/skills` — this is an intended capability

---

## Git Workflow

- Work on branch: `claude/happy-ramanujan-VFR8X`
- Commit after every completed task with descriptive message
- Push after every commit
- Never push directly to `main`
- Commit message format: `<type>(<scope>): <description>` — e.g., `feat(engine): add dedup conflict resolution`
- Mark tasks `[x]` in TASKS.md and update `.claude/context.md` after each completed task

---

## What NOT to Build

- DuckDB — not permitted in this environment
- Azure Functions — replaced by Container Apps
- ADX as primary store — future upgrade path only
- Azure SQL as recon data store — metadata only
- Multi-region — deferred
- Rate limiting per connector — deferred to v2
- CMK encryption — deferred (noted as upgrade path)
