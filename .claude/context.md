# Claude Code — Session Instructions

## READ THESE FILES BEFORE WRITING ANY CODE

1. `ARCHITECTURE.md` — All design decisions, storage choices, schemas, and what NOT to build
2. `TASKS.md` — Phased build plan with acceptance criteria

## Current State

- **Current Phase:** 1
- **Last Completed Task:** None — fresh start
- **Next Task:** 1.1 — Initialize Python project

## Rules

- Never propose or implement DuckDB — not permitted in this environment
- Never use ADX as primary store — it is a future upgrade path only (see ARCHITECTURE.md)
- Never store recon data in Azure SQL — SQL is for operational metadata only
- Never hardcode secrets — always use `{{secret:KEY_NAME}}` resolved from Azure Key Vault
- Never skip writing tests — every phase has acceptance criteria that require tests
- Ask before making any architectural decision not explicitly covered in ARCHITECTURE.md

## How to Start Each Session

1. Read ARCHITECTURE.md
2. Read TASKS.md — find the first unchecked `[ ]` task
3. Confirm current task with user before starting
4. Implement, test, mark task `[x]` when done
5. Update "Last Completed Task" and "Next Task" in this file

## Repository

- GitHub repo name: `recon-intelligence-platform`
- Language: Python 3.11+
- Framework: FastAPI
- Package manager: pip + pyproject.toml

## Local Dev Setup (target)

```bash
# Clone and install
git clone https://github.com/{org}/recon-intelligence-platform
cd recon-intelligence-platform
pip install -e ".[dev]"

# Run API locally
uvicorn src.api.main:app --reload

# Run Azure Functions locally
cd src/functions
func start

# Run tests
pytest tests/
```

## Azure Resources Required (dev environment)

- Azure Blob Storage account
- Azure Cosmos DB (free tier)
- Azure SQL Serverless
- Azure Synapse Workspace (serverless pool)
- Azure Service Bus (standard tier)
- Azure Key Vault
- Azure Functions App (consumption plan)

All connection strings and secrets go into Key Vault.
Local dev uses environment variables as fallback (see `src/config/secrets.py`).
