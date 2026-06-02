# SOC2 Type II Readiness Checklist

This checklist maps platform controls to SOC2 Trust Service Criteria. Review and check off each item before requesting a SOC2 audit. Items marked with a code reference have automated test coverage.

---

## 1. Access Control (CC6)

**CC6.1 — Logical access is restricted to authorized users**

- [ ] All API endpoints require Entra ID Bearer token except `GET /api/health`
  - Code: `src/ReconPlatform.Api/Program.cs` — `AddAuthentication().AddMicrosoftIdentityWebApi()`
  - Test: `AuthorizationTests.UnauthenticatedRequest_Returns401`
- [ ] Team claim validated on every request — extracted from JWT and compared to route `{team}`
  - Code: `src/ReconPlatform.Api/Controllers/TeamsController.cs`
  - Test: `ScopeEnforcementTests.CrossTeamRequest_Returns403`
- [ ] Engagement scope enforced for all asset queries — no assets returned outside scope
  - Code: `agent/orchestrator.py` `_dispatch_tool` — team injected from request, not from LLM
  - Test: `ScopeEnforcementTests.OutOfScopeAsset_NotReturned`
- [ ] Cross-team asset access returns 403 (verified by automated test)
  - Test: `ScopeEnforcementTests.CrossTeamAssetQuery_Returns403`
- [ ] `audit_log` table written before every mutating operation (pre-action audit)
  - Code: `src/ReconPlatform.Api/Middleware/AuditLoggingMiddleware.cs`
  - Test: `AuditLoggingTests.MutatingRequest_WritesAuditLogBeforeOperation`
- [ ] User permissions stored in `user_permissions` table in Azure SQL
  - Schema: `ARCHITECTURE.md` → Azure SQL Schema → `user_permissions` table
- [ ] Entra ID app registration per team — RBAC on all Azure resources
  - IaC: `infra/keyvault.bicep`, `infra/cosmos.bicep`

**CC6.2 — Prior to issuing credentials, registration is authorized**

- [ ] New team registration requires admin claim in JWT
- [ ] Engagement creation restricted to users with `admin` or `write` permission in `user_permissions`

**CC6.3 — Access is removed promptly when no longer needed**

- [ ] Revoking an Entra ID app registration immediately blocks API access (no session tokens stored)
- [ ] `user_permissions` records can be deleted via admin API

---

## 2. Encryption (CC6.7)

**At-rest encryption**

- [ ] All secrets stored in Azure Key Vault — never in code, config files, environment variables (except local dev)
  - Code: `src/ReconPlatform.Config/SecretResolver.cs`
  - Test: `SecretResolverTests.SecretPattern_ResolvedFromKeyVault`
- [ ] `{{secret:KEY_NAME}}` pattern used everywhere secrets appear in YAML config — never hardcoded
  - Validated by: `src/ReconPlatform.Config/Validator.cs` — rejects plaintext connection strings
- [ ] Blob Storage encrypted at rest using Azure-managed keys
  - IaC: `infra/blob.bicep` — `encryption: { services: { blob: { enabled: true } } }`
  - Upgrade path: Customer Managed Keys (CMK) via Key Vault — deferred to v2
- [ ] Cosmos DB encrypted at rest using Azure-managed keys
  - IaC: `infra/cosmos.bicep`
  - Upgrade path: CMK deferred to v2
- [ ] Azure SQL encrypted at rest using Transparent Data Encryption (TDE) — enabled by default
  - IaC: `infra/sql.bicep`

**In-transit encryption**

- [ ] TLS enforced on all Container App ingress — HTTP disabled, HTTPS only
  - IaC: `infra/containerapp.bicep` — `ingress: { allowInsecure: false }`
- [ ] SQL Server TLS 1.2+ enforced via connection string
  - Code: `SQL_CONNECTION_STRING` in `.env.example` — `Encrypt=True;TrustServerCertificate=False`
- [ ] All Key Vault SDK calls use HTTPS (enforced by Azure SDK)
- [ ] Service Bus connections use AMQP over TLS (enforced by Azure SDK)

---

## 3. Audit Logging (CC7.2)

**CC7.2 — System monitoring**

- [ ] `AuditLoggingMiddleware` logs every non-health API request with: actor (user_id from JWT), HTTP method, path, team, IP address, timestamp
  - Code: `src/ReconPlatform.Api/Middleware/AuditLoggingMiddleware.cs`
  - Test: `AuditLoggingTests.EveryNonHealthRequest_LogsAuditEntry`
- [ ] `audit_log` table in Azure SQL — append-only, no DELETE/UPDATE permitted on this table
  - Schema: `ARCHITECTURE.md` → Azure SQL Schema → `audit_log` table
  - Code: `src/ReconPlatform.Storage/SqlMetadataClient.cs` — `InsertAuditLog` (no update/delete method exists)
- [ ] `connector_run_log` records every connector pull: team, source_id, assets_pulled, assets_deduped, status, caller_identity
  - Code: `src/ReconPlatform.Storage/SqlMetadataClient.cs` — `InsertConnectorRunLog`
  - Test: `ConnectorWorkerTests.SuccessfulPull_LogsRunWithAssetCounts`
- [ ] Dead-lettered Service Bus messages logged to `audit_log` with failure reason (no message body — SOC2)
  - Code: `src/ReconPlatform.Workers/ConnectorWorker/DeadLetterMonitor.cs`
- [ ] Secret values never appear in logs — `SecretScrubbingPolicy` applied to all Serilog sinks
  - Code: `src/ReconPlatform.Api/Program.cs` — Serilog enricher scrubs `*secret*`, `*password*`, `*key*`, `*token*`, `*connection*`, `*conn*`
  - Test: `SecretScrubbingTests.SecretFieldsAreNotLogged` (verified by test)
- [ ] Log Analytics workspace retention: 30 days (dev), 90 days (prod)
  - IaC: `infra/main.bicep` — `retentionInDays` parameter
- [ ] Every authorization decision logged (allow or deny) with actor, team, resource, action
  - Code: `AuditLoggingMiddleware` — logs 401/403 responses with reason

---

## 4. Availability (A1)

**A1.1 — Current processing capacity is sufficient**

- [ ] Scale-to-zero Container Apps (Consumption plan) — zero idle cost
- [ ] KEDA auto-scale for Connector Worker based on Service Bus queue depth
  - IaC: `infra/containerapp.bicep` — scale rules with `messageCount: 5`
- [ ] API auto-scales based on HTTP concurrency

**A1.2 — Environmental threats are identified**

- [ ] Service Bus dead-letter queue monitored by `DeadLetterMonitor` worker
- [ ] Azure Monitor alert on dead-letter count > 0
  - IaC: `infra/main.bicep` — metric alert rule
- [ ] `GET /api/health` returns per-component status: cosmos, sql, blob, serviceBus
  - Code: `src/ReconPlatform.Api/Controllers/HealthController.cs`

**A1.3 — Recovery point and time objectives**

- [ ] Cosmos DB: zone-redundant option noted as prod upgrade path (deferred from free tier)
- [ ] Blob Storage: locally redundant (LRS) by default; zone-redundant (ZRS) upgrade path in `infra/blob.bicep`
- [ ] Rollback procedure documented in `docs/deployment.md` — activate previous Container App revision (seconds)
- [ ] All raw data in Blob Parquet is the source of truth — Cosmos can be rebuilt from Parquet if needed

---

## 5. Change Management (CC8)

**CC8.1 — Infrastructure changes are authorized and tested**

- [ ] All infrastructure defined as Bicep IaC — no manual Azure portal changes permitted
  - IaC: `infra/` directory
- [ ] CI workflow runs on every PR — build + unit tests + container image Trivy scan
  - Config: `.github/workflows/ci.yml`
- [ ] `TreatWarningsAsErrors` enforced — zero-warning policy
  - Config: `Directory.Build.props`
- [ ] Nullable reference types enabled — no implicit null dereferences
  - Config: `Directory.Build.props` — `<Nullable>enable</Nullable>`

**CC8.2 — Secrets management**

- [ ] Secrets never committed — `.gitignore` covers `.env`, `*.pfx`, `*.key`
- [ ] `{{secret:KEY}}` pattern required — `Validator.cs` rejects plaintext secrets
  - Test: `ValidatorTests.PlaintextConnectionString_FailsValidation`

**CC8.3 — Input validation**

- [ ] All external inputs validated at API boundary using data annotations or FluentValidation
- [ ] All SQL queries parameterized — no string interpolation
  - Code: `src/ReconPlatform.Connectors/AzureSqlConnector.cs`
  - Test: `SqlConnectorTests.Query_IsParameterized`
- [ ] JSONPath expressions from user-supplied configs sanitized before evaluation
  - Code: `src/ReconPlatform.Config/ExpressionValidator.cs`
- [ ] Plugin class paths restricted to `plugins/` directory — path traversal prevented
  - Code: `src/ReconPlatform.Connectors/PluginLoader.cs`
  - Test: `PluginLoaderTests.PathTraversalAttempt_IsRejected`
- [ ] Skill YAML schema validated before `SkillRegistry` accepts it
  - Code: `src/ReconPlatform.Skills/SkillRegistry.cs`
  - Test: `SkillRegistryTests.InvalidSkillYaml_IsRejected`

---

## 6. Incident Response

**Detection**

- [ ] Azure Monitor alert on Service Bus dead-letter count > 0 — fires within 5 minutes
  - IaC: `infra/main.bicep` — metric alert rule targeting `DeadLetteredMessageCount`
- [ ] `DeadLetterMonitor` logs failure reason and source to `audit_log` (no message body — prevents data leakage in logs)
  - Code: `src/ReconPlatform.Workers/ConnectorWorker/DeadLetterMonitor.cs`
- [ ] Log Analytics alerts on error rate spike (configurable threshold)

**Containment**

- [ ] Rollback procedure documented — activate previous Container App revision in seconds (see `docs/deployment.md`)
- [ ] Secret rotation documented — `POST /api/teams/{team}/secrets/rotate` invalidates cached secrets; next pull fetches fresh values from Key Vault

**Recovery**

- [ ] All raw data in Blob Parquet — Cosmos can be rebuilt from Blob without data loss
- [ ] `connector_run_log` provides a complete audit trail of every pull for forensic analysis

---

## Audit Evidence References

| Control | Evidence Location |
|---|---|
| Authentication enforced | `tests/ReconPlatform.UnitTests/AuthorizationTests.cs` |
| Secret scrubbing | `tests/ReconPlatform.UnitTests/SecretScrubbingTests.cs` |
| Scope enforcement | `tests/ReconPlatform.UnitTests/ScopeEnforcementTests.cs` |
| Audit log written pre-action | `tests/ReconPlatform.IntegrationTests/AuditLoggingTests.cs` |
| SQL injection prevention | `tests/ReconPlatform.UnitTests/SqlConnectorTests.cs` |
| Path traversal prevention | `tests/ReconPlatform.UnitTests/PluginLoaderTests.cs` |
| Infrastructure as code | `infra/` directory — all Bicep templates |
| CI pipeline | `.github/workflows/ci.yml` |
| Secret pattern enforcement | `tests/ReconPlatform.UnitTests/ValidatorTests.cs` |
