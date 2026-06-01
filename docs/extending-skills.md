# Extending Skills and Connectors

> Full content added in Task 7.5. Placeholder to establish doc structure.

## Adding a new skill (no code required)

1. Create a YAML file in `skills/` following the schema in `ARCHITECTURE.md`
2. The `SkillRegistry` hot-reloads — no restart needed
3. Optionally POST to `POST /api/skills` for dynamic registration

## Adding a new connector type

Run `/new-connector <Name> <type>` in Claude Code for a guided scaffold.
