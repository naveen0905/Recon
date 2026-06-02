// Cosmos DB — hot tier for active engagement assets with Change Feed support
targetScope = 'resourceGroup'

param namePrefix string
param location string
param tags object
param useFreeCosmosDb bool = true

var cosmosAccountName = '${namePrefix}-cosmos'

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-02-15-preview' = {
  name: cosmosAccountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    enableFreeTier: useFreeCosmosDb
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    enableAnalyticalStorage: false
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    capabilities: []
    backupPolicy: {
      type: 'Periodic'
      periodicModeProperties: {
        backupIntervalInMinutes: 240
        backupRetentionIntervalInHours: 8
        backupStorageRedundancy: 'Local'
      }
    }
    networkAclBypass: 'AzureServices'
    publicNetworkAccess: 'Enabled'
  }
}

resource reconDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-02-15-preview' = {
  parent: cosmosAccount
  name: 'recon'
  properties: {
    resource: {
      id: 'recon'
    }
  }
}

// Assets container — hot tier, partitioned by /team
resource assetsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: reconDatabase
  name: 'assets'
  properties: {
    resource: {
      id: 'assets'
      partitionKey: {
        paths: ['/team']
        kind: 'Hash'
        version: 2
      }
      defaultTtl: -1
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          { path: '/*' }
        ]
        excludedPaths: [
          { path: '/"_etag"/?' }
          { path: '/history/*' }
        ]
        compositeIndexes: [
          [
            {
              path: '/team'
              order: 'ascending'
            }
            {
              path: '/sourceId'
              order: 'ascending'
            }
          ]
        ]
      }
    }
    options: {
      autoscaleSettings: {
        maxThroughput: 1000
      }
    }
  }
}

// Change feed leases container — used by Change Feed processor
resource changeFeedLeasesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: reconDatabase
  name: 'change_feed_leases'
  properties: {
    resource: {
      id: 'change_feed_leases'
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
        version: 2
      }
    }
    options: {
      throughput: 400
    }
  }
}

output cosmosAccountId string = cosmosAccount.id
output cosmosAccountName string = cosmosAccount.name
output cosmosEndpoint string = cosmosAccount.properties.documentEndpoint
output cosmosDatabaseName string = reconDatabase.name
