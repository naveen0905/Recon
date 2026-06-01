# Claude Code — Session Instructions

## READ THESE FILES BEFORE WRITING ANY CODE

1. `ARCHITECTURE.md` — All design decisions, schemas, and what NOT to build
2. `TASKS.md` — Phased build plan with acceptance criteria
3. `CLAUDE.md` — Coding standards, security rules, agentic guidelines

## Current State

- **Current Phase:** 1
- **Last Completed Task:** 1.1 — Python project scaffold (pyproject.toml, requirements.txt, .gitignore)
- **Next Task:** 1.2 — Create .NET 8 solution with all project stubs

## Confirmed Architectural Decisions

| Decision | Choice |
|---|---|
| Primary language | C# .NET 8 |
| Agent / LLM layer | Python 3.11+ (separate Container App) |
| Solution structure | Single `.sln`, strict project boundaries |
| Hosting | Azure Container Apps (Consumption plan, scale-to-zero) |
| IaC | Bicep |
| Region | Single (East US 2) initially |
| Auth | Azure Entra ID — app registration per team |
| Compliance | SOC2 Type II |
| De-duplication | Configurable match keys per source, team-expandable |
| Plugin / Skills | Configuration-driven YAML |

## Hard Rules

- Never use DuckDB — not permitted
- Never use ADX as primary store — future upgrade path only
- Never store recon data in Azure SQL — SQL is for operational metadata only
- Never hardcode secrets — always `{{secret:KEY_NAME}}` resolved from Azure Key Vault
- Never log resolved secret values — scrub fields matching `*secret*`, `*password*`, `*key*`, `*token*`, `*conn*`
- Never skip writing tests — every phase has acceptance criteria that require tests
- Never use Azure Functions — replaced by Container Apps
- Treat compiler warnings as errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`)
- All API endpoints require Entra ID auth — no anonymous endpoints except `/api/health`
- Scope enforcement: all recon queries must validate against engagement scope
- Ask before any architectural decision not explicitly covered in ARCHITECTURE.md

## How to Start Each Session

1. Read ARCHITECTURE.md, TASKS.md, CLAUDE.md
2. Find first unchecked `[ ]` task in TASKS.md
3. Confirm current task with user before starting
4. Implement → test → mark `[x]` → update context.md

## Repository

- GitHub repo: `naveen0905/Recon`
- Working branch: `claude/happy-ramanujan-VFR8X`
- Primary language: C# .NET 8
- Agent language: Python 3.11+
- Framework: ASP.NET Core 8 (API), FastAPI (agent)

## Local Dev Setup

```bash
# .NET API
dotnet restore
dotnet build ReconPlatform.sln
dotnet run --project src/ReconPlatform.Api

# Python agent
pip install -e ".[dev]"
uvicorn agent.main:app --reload

# Tests
dotnet test tests/ReconPlatform.UnitTests
pytest tests/
```

## Azure Resources Required (dev environment)

- Azure Blob Storage account
- Azure Cosmos DB (free tier)
- Azure SQL Serverless
- Azure Synapse Workspace (serverless pool)
- Azure Service Bus (standard tier)
- Azure Key Vault
- Azure Container Apps environment
- Log Analytics Workspace (SOC2 audit logs)

All connection strings and secrets in Key Vault. Local dev uses environment variables as fallback.
