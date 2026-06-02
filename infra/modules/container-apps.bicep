// Container Apps Environment + all 4 Container Apps (API, Agent, Connector Worker, Staleness Timer)
targetScope = 'resourceGroup'

param namePrefix string
param location string
param tags object
param logAnalyticsWorkspaceId string
param logAnalyticsCustomerId string
param logAnalyticsPrimarySharedKey string
param keyVaultName string
param serviceBusConnectionString string
param connectorJobsQueueName string
param cosmosEndpoint string
param cosmosDatabaseName string
param storageAccountUrl string
param sqlServerFqdn string
param sqlDatabaseName string
param synapseServerlessEndpoint string

// Container Apps Environment — Consumption plan, connected to Log Analytics
resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2023-11-02-preview' = {
  name: '${namePrefix}-cae'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsPrimarySharedKey
      }
    }
    workloadProfiles: []
    zoneRedundant: false
  }
}

// ── Managed Identities ────────────────────────────────────────────────────────

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-api-id'
  location: location
  tags: tags
}

resource agentIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-agent-id'
  location: location
  tags: tags
}

resource connectorWorkerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-connector-id'
  location: location
  tags: tags
}

resource stalenessTimerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-staleness-id'
  location: location
  tags: tags
}

// ── Key Vault Access Policies for Container App Identities ───────────────────
// These are additive access policies on the existing Key Vault

resource kvRef 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource apiKvPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = {
  parent: kvRef
  name: 'add'
  properties: {
    accessPolicies: [
      {
        tenantId: subscription().tenantId
        objectId: apiIdentity.properties.principalId
        permissions: { secrets: ['get', 'list'] }
      }
      {
        tenantId: subscription().tenantId
        objectId: agentIdentity.properties.principalId
        permissions: { secrets: ['get', 'list'] }
      }
      {
        tenantId: subscription().tenantId
        objectId: connectorWorkerIdentity.properties.principalId
        permissions: { secrets: ['get', 'list'] }
      }
      {
        tenantId: subscription().tenantId
        objectId: stalenessTimerIdentity.properties.principalId
        permissions: { secrets: ['get', 'list'] }
      }
    ]
  }
}

// ── Shared environment variables (non-secret) ────────────────────────────────

var commonEnvVars = [
  {
    name: 'AZURE_KEYVAULT_URL'
    value: 'https://${keyVaultName}.vault.azure.net/'
  }
  {
    name: 'COSMOS_ENDPOINT'
    value: cosmosEndpoint
  }
  {
    name: 'COSMOS_DATABASE'
    value: cosmosDatabaseName
  }
  {
    name: 'BLOB_ACCOUNT_URL'
    value: storageAccountUrl
  }
  {
    name: 'SERVICEBUS_NAMESPACE'
    value: '${namePrefix}-sb.servicebus.windows.net'
  }
]

// ── 1. recon-api — ASP.NET Core API, external ingress, port 8080 ─────────────

resource reconApi 'Microsoft.App/containerApps@2023-11-02-preview' = {
  name: '${namePrefix}-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      secrets: [
        {
          name: 'servicebus-connection'
          value: serviceBusConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'recon-api'
          // Placeholder image — replaced by ACR image in CI/CD pipeline
          image: 'mcr.microsoft.com/dotnet/aspnet:8.0'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(commonEnvVars, [
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            {
              name: 'SQL_SERVER_FQDN'
              value: sqlServerFqdn
            }
            {
              name: 'SQL_DATABASE'
              value: sqlDatabaseName
            }
            {
              name: 'SYNAPSE_ENDPOINT'
              value: synapseServerlessEndpoint
            }
            {
              name: 'WORKER_TYPE'
              value: 'api'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: apiIdentity.properties.clientId
            }
          ])
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 5
      }
    }
  }
  dependsOn: [apiKvPolicy]
}

// ── 2. recon-agent — Python FastAPI, internal ingress only, port 8000 ────────

resource reconAgent 'Microsoft.App/containerApps@2023-11-02-preview' = {
  name: '${namePrefix}-agent'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${agentIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      ingress: {
        external: false
        targetPort: 8000
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          name: 'recon-agent'
          // Placeholder image — replaced by ACR image in CI/CD pipeline
          image: 'mcr.microsoft.com/dotnet/aspnet:8.0'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(commonEnvVars, [
            {
              name: 'AGENT_LLM_PROVIDER'
              value: 'anthropic'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: agentIdentity.properties.clientId
            }
            {
              name: 'RECON_API_BASE_URL'
              value: 'http://${namePrefix}-api'
            }
          ])
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 3
      }
    }
  }
  dependsOn: [apiKvPolicy]
}

// ── 3. recon-connector-worker — KEDA Service Bus scaler, no ingress ──────────

resource reconConnectorWorker 'Microsoft.App/containerApps@2023-11-02-preview' = {
  name: '${namePrefix}-connector-worker'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${connectorWorkerIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      // No ingress — queue-driven
      secrets: [
        {
          name: 'servicebus-connection'
          value: serviceBusConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'recon-connector-worker'
          // Placeholder image — replaced by ACR image in CI/CD pipeline
          image: 'mcr.microsoft.com/dotnet/aspnet:8.0'
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: concat(commonEnvVars, [
            {
              name: 'WORKER_TYPE'
              value: 'connector-worker'
            }
            {
              name: 'SERVICEBUS_QUEUE'
              value: connectorJobsQueueName
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: connectorWorkerIdentity.properties.clientId
            }
          ])
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 10
        rules: [
          {
            name: 'servicebus-keda-scaler'
            custom: {
              type: 'azure-servicebus'
              metadata: {
                queueName: connectorJobsQueueName
                namespace: '${namePrefix}-sb'
                messageCount: '10'
              }
              auth: [
                {
                  secretRef: 'servicebus-connection'
                  triggerParameter: 'connection'
                }
              ]
            }
          }
        ]
      }
    }
  }
  dependsOn: [apiKvPolicy]
}

// ── 4. recon-staleness-timer — KEDA cron scaler (every hour), no ingress ─────

resource reconStalenessTimer 'Microsoft.App/containerApps@2023-11-02-preview' = {
  name: '${namePrefix}-staleness-timer'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${stalenessTimerIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      // No ingress — cron-driven
      secrets: [
        {
          name: 'servicebus-connection'
          value: serviceBusConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'recon-staleness-timer'
          // Placeholder image — replaced by ACR image in CI/CD pipeline
          image: 'mcr.microsoft.com/dotnet/aspnet:8.0'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: concat(commonEnvVars, [
            {
              name: 'WORKER_TYPE'
              value: 'staleness-timer'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: stalenessTimerIdentity.properties.clientId
            }
          ])
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'cron-hourly-scaler'
            custom: {
              type: 'cron'
              metadata: {
                // Run every hour at minute 0
                timezone: 'UTC'
                start: '0 * * * *'
                end: '5 * * * *'
                desiredReplicas: '1'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [apiKvPolicy]
}

// ── Outputs ──────────────────────────────────────────────────────────────────

output environmentId string = containerAppsEnvironment.id
output environmentName string = containerAppsEnvironment.name
output apiUrl string = 'https://${reconApi.properties.configuration.ingress.fqdn}'
output agentUrl string = reconAgent.properties.configuration.ingress.fqdn
output apiIdentityPrincipalId string = apiIdentity.properties.principalId
output agentIdentityPrincipalId string = agentIdentity.properties.principalId
output connectorWorkerIdentityPrincipalId string = connectorWorkerIdentity.properties.principalId
output stalenessTimerIdentityPrincipalId string = stalenessTimerIdentity.properties.principalId
