# Extending Skills and Agent Tools

This guide is for developers who want to add new automated behaviors to the platform without writing C# code. Most skill and agent extensions are pure YAML configuration changes.

---

## 1. What is a Skill?

A **skill** is a YAML file in the `skills/` directory that tells the platform:

- **When** to fire (trigger type: on asset change, on schedule, or manually)
- **What** to do (action type: send a notification, retrigger data sources)

Skills are completely declarative — no code changes are needed to add a new one. The `SkillRegistry` watches the `skills/` directory for file changes and hot-reloads without restart.

**Relationship to the agent:** The AI agent and skills are complementary. Skills are automated reactions to events (like "whenever a new high-severity asset appears, retrigger the vuln scanner"). The agent is an interactive system that answers questions in natural language. Skills can also be defined as sub-agents (see section 6).

---

## 2. Skill YAML Schema

```yaml
id: <string>             # Unique identifier, used in API calls and logs
name: <string>           # Human-readable name shown in GET /api/skills
version: <int>           # Increment when you make breaking changes to trigger/action
description: <string>    # Optional — explains what this skill does
enabled: <bool>          # Set to false to disable without deleting the file

trigger:
  type: <trigger_type>   # asset_change | manual | scheduled
  filter:                # Optional key-value conditions that must match for trigger to fire
    <field>: <value>

actions:
  - type: <action_type>  # notify | retrigger_sources
    # ... action-specific fields (see section 5)
```

**Field reference:**

| Field | Type | Required | Description |
|---|---|---|---|
| `id` | string | Yes | Must be unique across all skill files. Used as the key in `GET /api/skills/{id}`. |
| `name` | string | Yes | Display name. |
| `version` | int | Yes | Start at `1`. Increment when changing trigger or action semantics. |
| `description` | string | No | Shown in API responses. Helpful for operators debugging why a skill fired. |
| `enabled` | bool | No (default: true) | Set to `false` to temporarily disable without deleting. |
| `trigger.type` | enum | Yes | `asset_change`, `manual`, or `scheduled`. |
| `trigger.filter` | object | No | Conditions that must all match for the trigger to fire. |
| `actions` | list | Yes | At least one action. Actions execute in order. |

---

## 3. Step-by-Step: Add a New Skill

No code changes needed. Follow these steps:

**Step 1:** Create a new YAML file in the `skills/` directory.

```bash
touch skills/my-new-skill.yaml
```

**Step 2:** Write the skill definition (see examples in sections 4 and 5).

**Step 3:** The `SkillRegistry` uses a `FileSystemWatcher` and will pick up the new file automatically within a few seconds. You do NOT need to restart the API or workers.

**Step 4:** Verify the skill is registered:

```bash
curl -H "Authorization: Bearer <your-token>" http://localhost:5000/api/skills
```

You should see your new skill in the response.

**Step 5:** Test it (see section 9).

Alternatively, you can register a skill via the API without creating a file:

```bash
curl -X POST http://localhost:5000/api/skills \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: application/yaml" \
  --data-binary @skills/my-new-skill.yaml
```

Skills registered via API are persisted to the database and survive restarts.

---

## 4. Skill Trigger Types

### `asset_change` — fires when a Cosmos asset is created or updated

```yaml
id: high-severity-alert
name: High Severity Asset Alert
version: 1
trigger:
  type: asset_change
  filter:
    severity: high        # only fire for high-severity assets
    asset_type: host      # only for host assets (not findings or credentials)
actions:
  - type: notify
    channel: audit_log
    message: "High severity host detected: {asset_id}"
enabled: true
```

The `filter` block supports any canonical asset field. The Change Feed Worker evaluates the filter against the incoming asset. All filter conditions must match (AND logic). Leave `filter` empty to fire on every asset change.

### `manual` — fires only when explicitly triggered via API

```yaml
id: full-retrigger
name: Manual Full Team Retrigger
version: 1
description: >
  Retriggers all sources for the team. Used at the start of a new engagement
  to ensure all data is fresh.
trigger:
  type: manual
actions:
  - type: retrigger_sources
    sources: []    # empty = all sources for this team
enabled: true
```

Trigger it:

```bash
curl -X POST http://localhost:5000/api/skills/full-retrigger/execute \
  -H "Authorization: Bearer <your-token>"
```

### `scheduled` — fires on a cron schedule

```yaml
id: weekly-stale-report
name: Weekly Stale Asset Report
version: 1
trigger:
  type: scheduled
  cron: "0 9 * * MON"    # every Monday at 09:00 UTC
actions:
  - type: notify
    channel: audit_log
    message: "Weekly staleness check triggered"
enabled: true
```

---

## 5. Skill Action Types

### `notify` — write a structured message to a channel

```yaml
actions:
  - type: notify
    channel: audit_log       # audit_log | service_bus
    queue: enrichment-alerts  # required when channel = service_bus
    message: "Asset {asset_id} changed: severity escalated to {severity}"
```

| Field | Required | Description |
|---|---|---|
| `channel` | Yes | `audit_log` writes to the SQL audit_log table. `service_bus` publishes to a queue. |
| `queue` | When channel=service_bus | Service Bus queue name. |
| `message` | Yes | Message template. `{field_name}` placeholders are filled from the triggering asset's fields. |

### `retrigger_sources` — enqueue an immediate data pull for specific sources

```yaml
actions:
  - type: retrigger_sources
    sources:
      - vuln-db
      - network-telemetry
    # scope: team    # optional: 'team' (default) or 'asset'
```

| Field | Required | Description |
|---|---|---|
| `sources` | Yes | List of source IDs to retrigger. Empty list (`[]`) means all sources for the team. |
| `scope` | No | `team` (retrigger for all assets) or `asset` (retrigger only for the specific asset that triggered the skill). |

---

## 6. What is an Agent Skill?

Files in `skills/agents/` are **sub-agent definitions** — they define a specialized AI agent with its own system prompt and restricted tool set. Unlike regular skills (which react to events), agent skills are invoked by the orchestrator or manually and operate interactively.

**Regular skill:** YAML in `skills/` → event-driven → runs actions automatically.

**Agent skill:** YAML in `skills/agents/` → defines an LLM-backed agent with specific tools and a system prompt → invoked by the orchestrator or API.

Example (the built-in scope validator):

```yaml
# skills/agents/scope-validator.yaml
id: scope-validator
name: Scope Validator Agent
version: 1
type: llm_agent
description: >
  Validates that all queried assets fall within the active engagement scope.
model: anthropic/claude-sonnet-4-6
system_prompt: |
  You are a scope enforcement agent for a penetration testing platform.
  Check each asset against the engagement scope. Return only in-scope assets.
  Log scope violations to audit_log.
triggers:
  - type: asset_change
    condition: "asset.team != engagement.team"
  - type: manual
actions:
  - type: notify
    channel: audit_log
    message: "Scope violation: asset {asset_id} outside engagement {engagement_id}"
tools:
  - query_assets
  - get_engagement_scope
enabled: true
```

Agent skills differ from regular skills in these ways:

| Aspect | Regular Skill | Agent Skill |
|---|---|---|
| Location | `skills/` | `skills/agents/` |
| Has `type: llm_agent` | No | Yes |
| Has `system_prompt` | No | Yes |
| Has `tools` list | No | Yes |
| Execution | SkillExecutor (deterministic) | Python agent orchestrator (LLM-driven) |

---

## 7. Step-by-Step: Add a New Agent Tool

Agent tools are Python functions in `agent/tools/` that the LLM can call. Adding a new tool requires a small code change in two places.

**Step 1:** Create the tool function in `agent/tools/`.

```python
# agent/tools/get_engagement_summary.py

import httpx
import os
from typing import Any

_API_BASE = os.environ.get("RECON_API_BASE_URL", "http://localhost:5000")


async def get_engagement_summary(team: str, engagement_id: str) -> dict[str, Any]:
    """Return a summary of asset counts and staleness for an engagement."""
    async with httpx.AsyncClient() as client:
        resp = await client.get(
            f"{_API_BASE}/api/engagements/{engagement_id}/summary",
            headers={"X-Team": team},
        )
        resp.raise_for_status()
        return resp.json()
```

**Step 2:** Export the function from `agent/tools/__init__.py`:

```python
# agent/tools/__init__.py  (add this line)
from .get_engagement_summary import get_engagement_summary
```

**Step 3:** Register the tool in `agent/orchestrator.py`.

Import the function at the top:

```python
from tools import (
    describe_sources,
    get_asset_history,
    get_engagement_summary,    # add this
    get_stale_assets,
    query_assets,
    trigger_pull,
)
```

Add the tool schema to `TOOL_DEFINITIONS`:

```python
{
    "name": "get_engagement_summary",
    "description": (
        "Return a summary of the current engagement: asset counts by type, "
        "stale asset count, and last pull timestamps per source."
    ),
    "input_schema": {
        "type": "object",
        "properties": {},    # no additional inputs — team and engagement_id come from request
        "required": [],
    },
},
```

Add the dispatch case to `_dispatch_tool`:

```python
case "get_engagement_summary":
    return await get_engagement_summary(
        team=team,
        engagement_id=engagement_id,
    )
```

**Step 4:** Write a test in `agent/tests/test_orchestrator.py`:

```python
async def test_get_engagement_summary_dispatched():
    # arrange: mock the HTTP call
    # act: call _dispatch_tool("get_engagement_summary", {}, team="alpha", engagement_id="eng-1")
    # assert: the correct API endpoint was called
    ...
```

**Step 5:** Restart the agent — new tool definitions are sent to Claude on every request, so no other changes are needed.

---

## 8. Scope Rules

The agent enforces engagement scope at two levels. Both are required and work together.

**Level 1 — System prompt (LLM instruction):**

The orchestrator builds a system prompt that explicitly tells Claude the team and engagement:

```
You are a recon intelligence assistant for team {team}.
You have access to tools within engagement {engagement_id}.
Never return assets outside the engagement scope.
```

**Level 2 — Tool dispatch injection (code enforcement):**

More importantly, the `_dispatch_tool` function in `agent/orchestrator.py` **always injects `team` and `engagement_id` from the original HTTP request**, ignoring any values Claude might try to pass:

```python
async def _dispatch_tool(tool_name, tool_input, team, engagement_id):
    match tool_name:
        case "query_assets":
            return await query_assets(
                team=team,             # from request — Claude cannot override
                engagement_id=engagement_id,  # from request — Claude cannot override
                source_id=tool_input.get("source_id", ""),
                query_id=tool_input.get("query_id", ""),
                parameters=tool_input.get("parameters"),
            )
        case "get_asset_history":
            asset_id = tool_input.get("asset_id", "")
            # Scope check: asset IDs are prefixed with the team name
            if not asset_id.startswith(f"{team}::"):
                return {"error": "Access denied: asset does not belong to your team."}
            return await get_asset_history(team=team, asset_id=asset_id)
```

This means **even if Claude hallucinates a different team name**, the tool call still uses the original requester's team. This is the critical security boundary.

When adding a new tool, always follow this pattern: accept `team` and `engagement_id` as function parameters injected by `_dispatch_tool`, never read them from `tool_input`.

---

## 9. Testing Your Skill

**Test a skill registered from a file (hot-reload):**

```bash
# Drop the file in the skills directory
cp my-skill.yaml skills/my-skill.yaml

# Wait ~5 seconds for the FileSystemWatcher to pick it up, then verify:
curl -H "Authorization: Bearer dev-token-team-alpha" \
  http://localhost:5000/api/skills | jq '.[] | select(.id == "my-skill")'
```

**Test a skill registered via API:**

```bash
curl -X POST http://localhost:5000/api/skills \
  -H "Authorization: Bearer dev-token-team-alpha" \
  -H "Content-Type: application/yaml" \
  --data-binary @my-skill.yaml

# Verify it is registered
curl -H "Authorization: Bearer dev-token-team-alpha" \
  http://localhost:5000/api/skills/my-skill
```

**Manually execute a skill:**

```bash
curl -X POST http://localhost:5000/api/skills/my-skill/execute \
  -H "Authorization: Bearer dev-token-team-alpha"
```

**Verify the skill fired by checking the audit log:**

```bash
curl -H "Authorization: Bearer dev-token-team-alpha" \
  http://localhost:5000/api/audit?event_type=skill_executed&skill_id=my-skill
```

---

## Skill Lifecycle

```mermaid
flowchart TD
    A[Create skills/my-skill.yaml] --> B{Schema valid?}
    B -->|No| C[SkillRegistry rejects\ncheck API logs for error]
    B -->|Yes| D[SkillRegistry hot-reloads\nFileSystemWatcher fires]
    D --> E[Skill available via\nGET /api/skills]
    E --> F{Trigger type?}
    F -->|asset_change| G[ChangeFeedWorker\ndetects matching change]
    F -->|manual| H[POST /api/skills/id/execute]
    F -->|scheduled| I[Cron timer fires]
    G & H & I --> J[SkillExecutor.ExecuteAsync]
    J --> K{Action type?}
    K -->|retrigger_sources| L[RetriggerOrchestrator\nenqueues Service Bus msg]
    K -->|notify| M[Structured log\n+ audit_log entry]
    L --> N[Connector Worker\npulls fresh data]
```
