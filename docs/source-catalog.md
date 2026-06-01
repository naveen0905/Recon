# Source Catalog — Design Document

## Problem

The Recon agent operates over a set of team-configured sources (ADX clusters, SQL databases, REST APIs, plugins). Before the agent can query a source it needs to understand:

- What questions the source can answer
- What parameters each query accepts and what types they must be
- What fields the source returns and how they map to canonical asset fields

Without this, the agent must either guess query shapes or fall back to a human asking the right question. Both paths are unreliable at scale. The Source Catalog solves this by letting source owners declare, inside the existing team YAML config, exactly which named queries their source supports.

---

## Design

Each source in a team YAML config may include an optional `catalog` block. Sources without a `catalog` block continue to work as before — the catalog is purely additive.

```
sources:
  - id: my-source
    type: azure_adx
    ...
    catalog:
      description: "Corporate asset inventory in ADX"
      queries:
        - id: hosts_by_subnet
          ...
```

The agent, when planning a recon task, calls `describe_sources` to enumerate all available named queries across the team's sources. It then selects the best-matching query and calls `query_assets` supplying only the named parameters defined in that query — it never constructs free-form query text.

---

## CatalogQuery Structure

Each entry in `queries` is a `CatalogQuery` with the following fields:

| Field | Type | Required | Description |
|---|---|---|---|
| `id` | string | yes | Stable machine-readable identifier, unique within the source |
| `description` | string | yes | Human/agent-readable explanation of what this query returns |
| `template` | string | yes | Query text with `{param_name}` placeholders for each named parameter |
| `parameters` | list of `QueryParameter` | no | Typed, named parameters that fill the template placeholders |
| `output_fields` | list of `OutputFieldDescriptor` | no | Description of returned fields and their canonical mappings |

### QueryParameter

| Field | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Must match a `{name}` placeholder in `template` |
| `type` | string | yes | One of: `string`, `int`, `double`, `kql_expression`, `sql_expression` |
| `default` | string | no | Default value used when the agent omits the parameter |
| `description` | string | yes | What the parameter controls; used by the agent to choose values |

### OutputFieldDescriptor

| Field | Type | Required | Description |
|---|---|---|---|
| `name` | string | yes | Field name as returned by this source |
| `type` | string | yes | Data type: `string`, `int`, `double`, `bool`, `datetime` |
| `maps_to` | string | no | Canonical asset field name (e.g. `asset.hostname`, `asset.ip`, `asset.owner`) |

---

## Supported Parameter Types

| Type | Meaning | Validation |
|---|---|---|
| `string` | Arbitrary text value | Length and character allowlist enforced; no SQL/KQL metacharacters |
| `int` | 64-bit signed integer | Must parse as integer; range checked at runtime |
| `double` | IEEE 754 double | Must parse as floating point |
| `kql_expression` | A KQL expression fragment injected into an ADX template | Restricted to a safe subset: comparisons, `and`/`or`, `in`, `contains`, `startswith`; no `externaldata`, `union`, `invoke`, `evaluate`, pipe to external plugins |
| `sql_expression` | A SQL expression fragment injected into a SQL template | Restricted to comparison operators and `AND`/`OR`; no subqueries, `EXEC`, `xp_*`, or DDL |

The `kql_expression` and `sql_expression` types exist to let the agent build flexible filter predicates without exposing raw query construction. The allowlist for both is enforced by `ExpressionValidator` in `ReconPlatform.Config` before any query is dispatched.

---

## Safety Model

The agent is constrained to operate as follows:

1. The agent may **only** execute queries that appear by `id` in a source's catalog.
2. The agent may **only** supply values for parameters declared in that query's `parameters` list.
3. Parameter values are validated against their declared `type` before the template is rendered.
4. The rendered query string is never shown to the agent — the agent sees only the parameter schema and output field descriptions.
5. `kql_expression` and `sql_expression` parameters pass through `ExpressionValidator` which rejects anything outside the safe subset.
6. Every query execution is written to `audit_log` before dispatch, including: actor identity, team, source id, query id, supplied parameter values (with secrets scrubbed), and timestamp.

This design means the agent cannot construct arbitrary queries. It can only fill typed scalar slots in pre-approved templates. The surface area for injection is bounded and auditable.

---

## Example YAML

### azure_adx

```yaml
- id: adx-asset-inventory
  type: azure_adx
  cluster: "{{secret:ADX_CLUSTER_URI}}"
  database: AssetDB
  catalog:
    description: "Corporate host inventory stored in Azure Data Explorer"
    queries:
      - id: hosts_by_subnet
        description: "Return all hosts whose IP falls within a given CIDR subnet"
        template: |
          Hosts
          | where subnet == '{subnet}'
          | where {extra_filter}
          | project hostname, ip, owner, last_seen
        parameters:
          - name: subnet
            type: string
            description: "CIDR notation subnet, e.g. 10.0.0.0/24"
          - name: extra_filter
            type: kql_expression
            default: "1 == 1"
            description: "Optional KQL filter expression applied after subnet match"
        output_fields:
          - name: hostname
            type: string
            maps_to: asset.hostname
          - name: ip
            type: string
            maps_to: asset.ip
          - name: owner
            type: string
            maps_to: asset.owner
          - name: last_seen
            type: datetime
            maps_to: asset.last_seen
```

### azure_sql

```yaml
- id: sql-vuln-findings
  type: azure_sql
  connection_string: "{{secret:SQL_CONN_VULN}}"
  catalog:
    description: "Vulnerability findings from the internal scanner stored in Azure SQL"
    queries:
      - id: open_findings_by_severity
        description: "Return open vulnerability findings filtered by minimum severity score"
        template: |
          SELECT asset_id, cve_id, severity, title, discovered_at
          FROM findings
          WHERE severity >= @min_severity
            AND status = 'open'
            AND ({scope_filter})
        parameters:
          - name: min_severity
            type: double
            default: "7.0"
            description: "Minimum CVSS score (0.0–10.0) to include in results"
          - name: scope_filter
            type: sql_expression
            default: "1=1"
            description: "Optional SQL filter expression limiting asset scope"
        output_fields:
          - name: asset_id
            type: string
            maps_to: asset.id
          - name: cve_id
            type: string
            maps_to: finding.cve_id
          - name: severity
            type: double
            maps_to: finding.severity
          - name: title
            type: string
          - name: discovered_at
            type: datetime
            maps_to: finding.discovered_at
```

### rest_api

```yaml
- id: crowdstrike-hosts
  type: rest_api
  base_url: "https://api.crowdstrike.com"
  auth:
    type: oauth2_client_credentials
    token_url: "https://api.crowdstrike.com/oauth2/token"
    client_id: "{{secret:CS_CLIENT_ID}}"
    client_secret: "{{secret:CS_CLIENT_SECRET}}"
  catalog:
    description: "CrowdStrike Falcon host inventory via REST API"
    queries:
      - id: hosts_by_tag
        description: "Return hosts that have a given FalconGroupingTag assigned"
        template: "/devices/queries/devices/v1?filter=tags:'{tag}'&limit={limit}"
        parameters:
          - name: tag
            type: string
            description: "FalconGroupingTag value to filter by"
          - name: limit
            type: int
            default: "100"
            description: "Maximum number of device IDs to return per page"
        output_fields:
          - name: device_id
            type: string
            maps_to: asset.id
          - name: hostname
            type: string
            maps_to: asset.hostname
          - name: external_ip
            type: string
            maps_to: asset.ip
          - name: tags
            type: string
```

### plugin

```yaml
- id: custom-cmdb-plugin
  type: plugin
  plugin_class: "plugins/CmdbConnector"
  config:
    endpoint: "https://cmdb.internal/api"
  catalog:
    description: "Internal CMDB accessed via custom connector plugin"
    queries:
      - id: assets_by_business_unit
        description: "Return all CMDB assets belonging to a given business unit"
        template: '{"business_unit": "{business_unit}", "include_decommissioned": {include_decommissioned}}'
        parameters:
          - name: business_unit
            type: string
            description: "Business unit short code, e.g. CORP, ENG, FIN"
          - name: include_decommissioned
            type: string
            default: "false"
            description: "Set to 'true' to include decommissioned assets"
        output_fields:
          - name: asset_name
            type: string
            maps_to: asset.hostname
          - name: asset_owner
            type: string
            maps_to: asset.owner
          - name: business_unit
            type: string
          - name: status
            type: string
            maps_to: asset.status
```

---

## Future Extension — Phase A and Phase B

### Phase A — Static Catalog (Current Design)

The catalog is authored by hand inside the team YAML config. The agent uses it as-is. This is the design described in this document. It is simple, auditable, and requires no runtime discovery.

Planned work:
- `SchemaRegistry` service aggregates catalogs from all team sources on load
- `describe_sources` agent tool returns aggregated catalog entries
- `query_assets` agent tool validates parameters, renders templates, dispatches to the appropriate connector

### Phase B — Runtime Discovery (Future)

Phase B adds automatic catalog construction by introspecting live sources. Human-authored catalog entries in YAML take precedence and are merged with discovered entries; the `id` field is used as the merge key.

| Source Type | Discovery Mechanism |
|---|---|
| `azure_adx` | `.show tables` and `.show table T schema` — emits table names and column types |
| `azure_sql` | `INFORMATION_SCHEMA.COLUMNS` query — emits table/view names and column types |
| `rest_api` | OpenAPI spec URL configured in source (`openapi_spec_url` field) — parsed to extract operations and parameter schemas |
| `plugin` | Plugin declares a `GetCatalog()` method returning `SourceCatalog`; `PluginLoader` calls it at startup |

Discovered queries are marked with `"source": "discovered"` in the runtime catalog to distinguish them from human-authored ones. The agent preference ordering is: human-authored > plugin-declared > runtime-discovered.

Phase B is deferred until the core connector layer is stable.

---

## SchemaRegistry Service (Planned)

`SchemaRegistry` is a planned service in `ReconPlatform.Engine` that:

1. Loads all team configs via `ITeamConfigProvider`
2. For each source, reads `SourceConfig.Catalog` (if present)
3. Builds an in-memory index keyed by `(team_id, source_id, query_id)`
4. Exposes `ISchemaRegistry.GetCatalog(teamId)` returning the aggregated `IReadOnlyList<SourceCatalogEntry>` for that team
5. In Phase B, additionally invokes runtime discovery and merges results

`SchemaRegistry` is re-loaded when team config changes are detected (watch on the config Blob container).

---

## Agent Tool Integration

### `describe_sources`

```
describe_sources(team_id: string) -> list[SourceCatalogEntry]
```

Returns all named queries available to the team across all configured sources. Each entry includes `source_id`, `query_id`, `description`, parameter schemas, and output field descriptors. The agent uses this to decide which query to run.

### `query_assets`

```
query_assets(team_id: string, source_id: string, query_id: string, parameters: dict[str, str]) -> list[Asset]
```

Executes a named query by:
1. Looking up `(team_id, source_id, query_id)` in `SchemaRegistry` — returns 404 if not found
2. Validating each supplied parameter value against its declared type
3. Writing a pre-action audit log entry
4. Rendering the template with validated parameter values
5. Dispatching the rendered query to the appropriate connector
6. Mapping output fields to canonical asset fields using `maps_to`
7. Passing results through `DeduplicationEngine` before returning

---

## Security Constraints Summary

| Constraint | Enforcement Point |
|---|---|
| Agent can only execute catalog-registered queries | `query_assets` tool — rejects unknown `query_id` |
| Parameter values must match declared type | `ParameterValidator` in `ReconPlatform.Config` |
| `kql_expression` parameters pass allowlist check | `ExpressionValidator` — ADX path |
| `sql_expression` parameters pass allowlist check | `ExpressionValidator` — SQL path |
| No raw query text exposed to agent | Template rendered server-side only |
| All executions pre-audited | `IAuditLogger.LogQueryExecution` called before dispatch |
| Secrets scrubbed from audit log | `SecretScrubber` applied to parameter values before logging |
| Cross-team access blocked | `query_assets` validates `team_id` against caller's Entra ID token claim |
| Results scoped to engagement | Connector applies engagement scope filter before returning assets |
