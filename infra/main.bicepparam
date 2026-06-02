// Dev environment parameter file for Recon Intelligence Platform
// Usage:
//   az deployment group create \
//     --resource-group rg-recon-dev \
//     --template-file infra/main.bicep \
//     --parameters @infra/main.bicepparam \
//     --parameters sqlAdminPassword=<secret> \
//                  synapseAdminPassword=<secret> \
//                  deployingPrincipalId=$(az ad signed-in-user show --query id -o tsv)
//
// NEVER commit sqlAdminPassword or synapseAdminPassword — always pass via CLI or CI/CD secret variable
using './main.bicep'

param namePrefix = 'recon-dev'
param location = 'eastus2'
param environmentName = 'dev'

// deployingPrincipalId: pass via CLI --parameters deployingPrincipalId=<object-id>
// sqlAdminPassword: pass via CLI --parameters sqlAdminPassword=<value>
// synapseAdminPassword: pass via CLI --parameters synapseAdminPassword=<value>
param deployingPrincipalId = ''
param sqlAdminPassword = ''
param synapseAdminPassword = ''

param useFreeCosmosDb = true
param storageRedundancy = 'LRS'
param logRetentionDays = 30
param enableKvPurgeProtection = false
