// Monitoring — Alert rules + action group for dead-letter and replica alerts
targetScope = 'resourceGroup'

param namePrefix string
param location string
param tags object
param logAnalyticsWorkspaceId string
param serviceBusId string
param containerAppsEnvironmentId string
// Alert email — in production this should be read from Key Vault secret ALERT_EMAIL
// Placeholder: set to a real address via post-deploy script or CI/CD variable
param alertEmail string = 'alerts@example.com'

// Action group — email notifications for all alerts
resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${namePrefix}-ag'
  location: 'Global'
  tags: tags
  properties: {
    groupShortName: 'ReconAlerts'
    enabled: true
    emailReceivers: [
      {
        name: 'PlatformAlerts'
        // Production: replace with value from Key Vault secret {{secret:ALERT_EMAIL}}
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// Alert: Service Bus dead-letter count > 0 on connector-jobs queue
resource deadLetterAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${namePrefix}-dlq-alert'
  location: 'Global'
  tags: tags
  properties: {
    description: 'Alert when connector-jobs dead-letter queue has messages — indicates failed connector runs requiring investigation'
    severity: 2
    enabled: true
    scopes: [
      serviceBusId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'DeadLetterCount'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          metricName: 'DeadletteredMessages'
          dimensions: [
            {
              name: 'EntityName'
              operator: 'Include'
              values: ['connector-jobs']
            }
          ]
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Maximum'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

// Alert: change-events dead-letter queue > 0
resource changeEventsDeadLetterAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${namePrefix}-change-dlq-alert'
  location: 'Global'
  tags: tags
  properties: {
    description: 'Alert when change-events dead-letter queue has messages'
    severity: 3
    enabled: true
    scopes: [
      serviceBusId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'ChangeEventsDeadLetterCount'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          metricName: 'DeadletteredMessages'
          dimensions: [
            {
              name: 'EntityName'
              operator: 'Include'
              values: ['change-events']
            }
          ]
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Maximum'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

// Alert (informational): Container Apps environment — high active replica count
// This fires when total replicas spike, not when they reach zero (scale-to-zero is expected)
resource highReplicaAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${namePrefix}-replica-spike-alert'
  location: 'Global'
  tags: tags
  properties: {
    description: 'Informational: connector-worker replica count is high — may indicate large job backlog'
    severity: 4 // Informational
    enabled: true
    scopes: [
      containerAppsEnvironmentId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'ReplicaCount'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.App/managedEnvironments'
          metricName: 'UsageNanoCores'
          operator: 'GreaterThan'
          threshold: 8000000000 // ~8 vCores — proxy for high replica count
          timeAggregation: 'Average'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

output actionGroupId string = actionGroup.id
output deadLetterAlertId string = deadLetterAlert.id
