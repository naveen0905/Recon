// Recon Intelligence Platform — Root Bicep Deployment
// Targets a single resource group; provision with:
//   az deployment group create \
//     --resource-group <rg-name> \
//     --template-file infra/main.bicep \
//     --parameters @infra/main.bicepparam \
//     --parameters sqlAdminPassword=<secret> synapseAdminPassword=<secret>
targetScope = 'resourceGroup'

// ── Parameters ────────────────────────────────────────────────────────────────

@description('Prefix for all resource names — keep short (e.g. recon-dev, recon-prod)')
param namePrefix string = 'recon-dev'

@description('Azure region for all resources')
param location string = 'eastus2'

@description('Environment name used in tags and config (dev | staging | prod)')
param environmentName string = 'dev'

@description('Object ID of the principal (user/SP) running this deployment — gets full Key Vault secret management')
param deployingPrincipalId string

@description('SQL Server administrator password — provide via CLI, never in param file')
@secure()
param sqlAdminPassword string

@description('Synapse workspace SQL administrator password — provide via CLI, never in param file')
@secure()
param synapseAdminPassword string

@description('Use Cosmos DB free tier (true for dev, false for prod when free tier is already used)')
param useFreeCosmosDb bool = true

@description('Storage redundancy: LRS for dev, GRS for prod')
@allowed(['LRS', 'GRS', 'ZRS', 'GZRS'])
param storageRedundancy string = 'LRS'

@description('Log Analytics retention in days (30 for dev, 90+ for prod SOC2)')
param logRetentionDays int = 30

@description('Enable Key Vault purge protection (false for dev, true for prod)')
param enableKvPurgeProtection bool = false

// ── Tags — applied to all resources ──────────────────────────────────────────

var tags = {
  environment: environmentName
  project: 'ReconPlatform'
  managedBy: 'bicep'
}

// ── Module: Log Analytics ─────────────────────────────────────────────────────

module logAnalytics 'modules/log-analytics.bicep' = {
  name: 'logAnalytics'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    retentionDays: logRetentionDays
  }
}

// ── Module: Key Vault ─────────────────────────────────────────────────────────

module keyVault 'modules/key-vault.bicep' = {
  name: 'keyVault'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    enablePurgeProtection: enableKvPurgeProtection
    deployingPrincipalId: deployingPrincipalId
    // Container App managed identity IDs are added post-deploy via container-apps module
    // which calls Key Vault additive access policy
    containerAppPrincipalIds: []
  }
}

// ── Module: Storage Account ───────────────────────────────────────────────────

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    redundancy: storageRedundancy
  }
}

// ── Module: Cosmos DB ─────────────────────────────────────────────────────────

module cosmos 'modules/cosmos.bicep' = {
  name: 'cosmos'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    useFreeCosmosDb: useFreeCosmosDb
  }
}

// ── Module: Azure SQL Serverless ──────────────────────────────────────────────

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    sqlAdminPassword: sqlAdminPassword
  }
}

// ── Module: Service Bus ───────────────────────────────────────────────────────

module serviceBus 'modules/service-bus.bicep' = {
  name: 'serviceBus'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
  }
}

// ── Module: Synapse (Serverless SQL Pool) ─────────────────────────────────────

module synapse 'modules/synapse.bicep' = {
  name: 'synapse'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    synapseAdminPassword: synapseAdminPassword
    storageAccountName: storage.outputs.storageAccountName
    storageAccountId: storage.outputs.storageAccountId
  }
}

// ── Module: Container Apps ────────────────────────────────────────────────────

module containerApps 'modules/container-apps.bicep' = {
  name: 'containerApps'
  dependsOn: [
    keyVault
    storage
    cosmos
    sql
    serviceBus
  ]
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    logAnalyticsWorkspaceId: logAnalytics.outputs.workspaceId
    logAnalyticsCustomerId: logAnalytics.outputs.customerId
    logAnalyticsPrimarySharedKey: logAnalytics.outputs.primarySharedKey
    keyVaultName: keyVault.outputs.keyVaultName
    serviceBusConnectionString: serviceBus.outputs.serviceBusConnectionString
    connectorJobsQueueName: serviceBus.outputs.connectorJobsQueueName
    cosmosEndpoint: cosmos.outputs.cosmosEndpoint
    cosmosDatabaseName: cosmos.outputs.cosmosDatabaseName
    storageAccountUrl: storage.outputs.storageAccountUrl
    sqlServerFqdn: sql.outputs.sqlServerFqdn
    sqlDatabaseName: sql.outputs.sqlDatabaseName
    synapseServerlessEndpoint: synapse.outputs.synapseServerlessEndpoint
  }
}

// ── Module: Monitoring ────────────────────────────────────────────────────────

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    logAnalyticsWorkspaceId: logAnalytics.outputs.workspaceId
    serviceBusId: serviceBus.outputs.serviceBusId
    containerAppsEnvironmentId: containerApps.outputs.environmentId
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────

@description('Public URL of the Recon API (ASP.NET Core Container App)')
output apiUrl string = containerApps.outputs.apiUrl

@description('Internal URL of the Agent service (Python FastAPI Container App)')
output agentUrl string = containerApps.outputs.agentUrl

@description('Key Vault name — use to fetch secrets post-deploy')
output keyVaultName string = keyVault.outputs.keyVaultName

@description('Key Vault URI')
output keyVaultUri string = keyVault.outputs.keyVaultUri

@description('Cosmos DB endpoint')
output cosmosEndpoint string = cosmos.outputs.cosmosEndpoint

@description('Storage account blob endpoint')
output storageAccountUrl string = storage.outputs.storageAccountUrl

@description('SQL Server FQDN')
output sqlServerFqdn string = sql.outputs.sqlServerFqdn

@description('Synapse serverless SQL endpoint')
output synapseServerlessEndpoint string = synapse.outputs.synapseServerlessEndpoint

@description('Service Bus namespace endpoint')
output serviceBusEndpoint string = serviceBus.outputs.serviceBusEndpoint

@description('Log Analytics workspace ID')
output logAnalyticsWorkspaceId string = logAnalytics.outputs.workspaceId
