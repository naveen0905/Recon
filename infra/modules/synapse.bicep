// Synapse Analytics — serverless SQL pool for T-SQL queries on Parquet blobs
targetScope = 'resourceGroup'

param namePrefix string
param location string
param tags object

@secure()
param synapseAdminPassword string
param synapseAdminLogin string = 'synapseadmin'
param storageAccountName string
param storageAccountId string

// Synapse workspace name: alphanumeric + hyphens, max 45 chars, globally unique
var workspaceName = '${namePrefix}-synapse'
// Data lake storage — reuse existing storage account
var storageAccountUrl = 'https://${storageAccountName}.dfs.core.windows.net'
var fileSystemName = 'recon-raw'

resource synapseWorkspace 'Microsoft.Synapse/workspaces@2021-06-01' = {
  name: workspaceName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    defaultDataLakeStorage: {
      accountUrl: storageAccountUrl
      filesystem: fileSystemName
      resourceId: storageAccountId
      createManagedPrivateEndpoint: false
    }
    sqlAdministratorLogin: synapseAdminLogin
    sqlAdministratorLoginPassword: synapseAdminPassword
    managedVirtualNetwork: 'default'
    managedVirtualNetworkSettings: {
      preventDataExfiltration: false
    }
    publicNetworkAccess: 'Enabled'
  }
}

// Allow Azure services to connect to Synapse serverless SQL
resource synapseFirewallAllowAzure 'Microsoft.Synapse/workspaces/firewallRules@2021-06-01' = {
  parent: synapseWorkspace
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Synapse uses the built-in serverless SQL pool (always present, no dedicated pool)
// No Microsoft.Synapse/workspaces/sqlPools resource needed for serverless

output synapseWorkspaceId string = synapseWorkspace.id
output synapseWorkspaceName string = synapseWorkspace.name
output synapseServerlessEndpoint string = synapseWorkspace.properties.connectivityEndpoints.sqlOnDemand
output synapseIdentityPrincipalId string = synapseWorkspace.identity.principalId
