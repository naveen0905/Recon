# Claude Code — Session Instructions

## READ THESE FILES BEFORE WRITING ANY CODE

1. `ARCHITECTURE.md` — All design decisions, schemas, and what NOT to build
2. `TASKS.md` — Phased build plan with acceptance criteria
3. `CLAUDE.md` — Coding standards, security rules, agentic guidelines

## Current State

- **Current Phase:** 1
- **Last Completed Task:** 1.1 — Python agent stub (pyproject.toml, requirements.txt, .gitignore)
- **Next Task:** 1.2 — .NET 8 solution scaffold with agent-friendly repo practices

## Confirmed Architectural Decisions

| Decision | Choice |
|---|---|
| Primary language | C# .NET 8 |
| Agent / LLM layer | Python 3.11+ (separate Container App) |
| Solution structure | Single `.sln`, strict project boundaries |
| Hosting | Azure Container Apps (Consumption, scale-to-zero) |
| IaC | Bicep |
| Region | Single (East US 2) initially |
| Auth | Azure Entra ID — app registration per team |
| Compliance | SOC2 Type II |
| De-duplication | Configurable match keys per source, team-expandable |
| Plugin / Skills | Configuration-driven YAML |

## Hard Rules

- Never use DuckDB — not permitted
- Never use ADX as primary store — future upgrade path only
- Never store recon data in Azure SQL — operational metadata only
- Never hardcode secrets — always `{{secret:KEY_NAME}}` from Key Vault
- Never log resolved secret values — scrub `*secret*`, `*password*`, `*key*`, `*token*`, `*conn*`
- Never skip writing tests — every phase has acceptance criteria tests
- Never use Azure Functions — replaced by Container Apps
- Treat compiler warnings as errors
- All API endpoints require Entra ID auth except `GET /api/health`
- Scope enforcement: all recon queries validate against engagement scope
- Ask before any architectural decision not explicitly in ARCHITECTURE.md

## How to Start Each Session

1. Read ARCHITECTURE.md, TASKS.md, CLAUDE.md
2. Find first unchecked `[ ]` task in TASKS.md
3. Confirm current task with user before starting
4. Implement → test → mark `[x]` → update this file

## Repository

- GitHub: `naveen0905/Recon`
- Primary language: C# .NET 8 (`ReconPlatform.sln`)
- Agent language: Python 3.11+ (`agent/`)
- Branch for new work: create feature branch from `main`

## Local Dev Quick Start

```bash
# .NET
make build
make test

# Python agent
make agent-install
make agent-test

# Run API
dotnet run --project src/ReconPlatform.Api
```

## Azure Resources Required (dev)

Blob Storage, Cosmos DB (free tier), Azure SQL Serverless, Synapse Workspace,
Service Bus (standard), Key Vault, Container Apps environment, Log Analytics.

All secrets in Key Vault. Local dev uses `.env` (see `.env.example`).
