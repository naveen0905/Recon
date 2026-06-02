// Key Vault — secrets store for all platform secrets
targetScope = 'resourceGroup'

param namePrefix string
param location string
param tags object
param enablePurgeProtection bool = false
param deployingPrincipalId string

// Managed identity object IDs for Container Apps — passed in after container-apps deployment
// Access policies are set here for the deploying principal; Container App identities are added via separate module outputs
param containerAppPrincipalIds array = []

// Shorten name to fit 24-char Key Vault limit; replace hyphens
var kvName = take(replace('${namePrefix}-kv', '-', ''), 24)

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: enablePurgeProtection ? true : null
    enableRbacAuthorization: false
    accessPolicies: concat(
      [
        // Deploying principal gets full secret management
        {
          tenantId: subscription().tenantId
          objectId: deployingPrincipalId
          permissions: {
            secrets: [
              'get'
              'list'
              'set'
              'delete'
              'backup'
              'restore'
              'recover'
              'purge'
            ]
          }
        }
      ],
      [for principalId in containerAppPrincipalIds: {
        tenantId: subscription().tenantId
        objectId: principalId
        permissions: {
          secrets: [
            'get'
            'list'
          ]
        }
      }]
    )
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// Placeholder secret — real secrets are set post-deploy via CI/CD
resource alertEmailSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ALERT-EMAIL'
  properties: {
    // ALERT_EMAIL placeholder — replace with actual email via CI/CD or az keyvault secret set
    value: 'alerts@example.com'
    attributes: {
      enabled: true
    }
  }
}

output keyVaultId string = keyVault.id
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
