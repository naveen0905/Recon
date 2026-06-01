# Deployment Guide

> Full content added in Task 7.3. Placeholder to establish doc structure.

## Overview

All services deploy as Azure Container Apps using Bicep templates in `infra/`.

```bash
az deployment group create \
  --resource-group recon-dev \
  --template-file infra/main.bicep \
  --parameters @infra/parameters.dev.json
```
