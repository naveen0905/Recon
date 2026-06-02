// Storage Account — Parquet raw archive + skill YAML hot-reload
targetScope = 'resourceGroup'

param namePrefix string
param location string
param tags object
param redundancy string = 'LRS' // LRS for dev, GRS for prod

// Storage account name: alphanumeric only, max 24 chars
var storageAccountName = take(toLower(replace(replace('${namePrefix}stor', '-', ''), '_', '')), 24)

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_${redundancy}'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    encryption: {
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
        file: {
          enabled: true
          keyType: 'Account'
        }
      }
      keySource: 'Microsoft.Storage'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

// Raw Parquet archive — partitioned by {team}/{source-id}/{year}/{month}/{day}/
resource reconRawContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'recon-raw'
  properties: {
    publicAccess: 'None'
  }
}

// Skill YAML hot-reload — SkillRegistry watches this container
resource skillsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'skills'
  properties: {
    publicAccess: 'None'
  }
}

output storageAccountId string = storageAccount.id
output storageAccountName string = storageAccount.name
output storageAccountUrl string = storageAccount.properties.primaryEndpoints.blob
output storageConnectionStringSecretRef string = 'storage-connection-string'
