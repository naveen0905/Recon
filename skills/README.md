# Skills

This directory contains configuration-driven skill and sub-agent definitions.
No code deployment is needed to add a new skill — drop a YAML file here and
the `SkillRegistry` picks it up at startup (and on file change without restart).

## Skill types

| Type | Description | Directory |
|---|---|---|
| `event_skill` | Triggered by Cosmos change feed events | `skills/*.yaml` |
| `llm_agent` | LLM sub-agent with tool definitions | `skills/agents/*.yaml` |

## Adding a new skill

1. Copy one of the example YAML files below
2. Edit `id`, `name`, `trigger`, and `actions`
3. Restart is not required — `SkillRegistry` watches this directory
4. Validate via `POST /api/skills` (the API also accepts skill YAML for dynamic registration)

See `docs/extending-skills.md` for full field reference.
