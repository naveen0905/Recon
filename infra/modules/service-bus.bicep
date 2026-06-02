// Service Bus — durable job queue for connector triggers and change events
targetScope = 'resourceGroup'

param namePrefix string
param location string
param tags object

var namespaceName = '${namePrefix}-sb'

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

// connector-jobs queue — KEDA Service Bus scaler drives recon-connector-worker
resource connectorJobsQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'connector-jobs'
  properties: {
    maxDeliveryCount: 3
    lockDuration: 'PT5M' // 5 minutes
    deadLetteringOnMessageExpiration: true
    enablePartitioning: false
    enableExpress: false
    defaultMessageTimeToLive: 'P7D' // 7 days
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: false
    requiresSession: false
  }
}

// change-events queue — reactive enrichment from change feed worker
resource changeEventsQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'change-events'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT1M' // 1 minute
    deadLetteringOnMessageExpiration: true
    enablePartitioning: false
    enableExpress: false
    defaultMessageTimeToLive: 'P7D' // 7 days
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: false
    requiresSession: false
  }
}

// Shared access policy for applications (listen + send)
resource appSendListenPolicy 'Microsoft.ServiceBus/namespaces/authorizationRules@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'AppSendListen'
  properties: {
    rights: [
      'Listen'
      'Send'
    ]
  }
}

output serviceBusId string = serviceBusNamespace.id
output serviceBusName string = serviceBusNamespace.name
output serviceBusEndpoint string = serviceBusNamespace.properties.serviceBusEndpoint
output connectorJobsQueueName string = connectorJobsQueue.name
output changeEventsQueueName string = changeEventsQueue.name
output serviceBusConnectionString string = appSendListenPolicy.listKeys().primaryConnectionString
