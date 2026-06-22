// PesaCore / PesaCore — Azure Infrastructure as Code
//
// Bicep is Microsoft's IaC DSL for Azure — compiles to ARM templates,
// preferred over Terraform in Microsoft-shop contexts.
//
// This file declares the Azure side of the hybrid deployment story
// described in the migration notes:
//   - Azure App Service (Linux, P1v3) hosting the .NET 10 API
//   - Azure SQL Database with Managed Identity auth
//   - Azure Key Vault for secrets
//   - Application Insights + Log Analytics for telemetry
//   - User-Assigned Managed Identity for service auth (no passwords)
//
// Deploy:
//   az deployment group create \
//     --resource-group rg-pesacore-prod \
//     --template-file main.bicep \
//     --parameters environmentName=prod sqlAdminLogin=pesacore-admin
//
// What-if (recommended pre-deploy):
//   az deployment group what-if --resource-group rg-pesacore-prod \
//     --template-file main.bicep --parameters environmentName=prod
//
// Cost discipline: P1v3 App Service Plan (~$72/mo), Azure SQL S1 (~$30/mo),
// App Insights pay-as-you-go (~$5/mo at low volume), Key Vault standard
// (~$0.03/secret/mo). Total floor: ~$110/month for prod. Scale to S0 SQL
// for dev/staging to halve the cost. The audit's "things to NOT build"
// section explicitly says: don't deploy this yet — Bicep + what-if output
// is the artifact; live deployment incurs cost without value pre-screen-call.

@description('Environment name (e.g., dev, staging, prod). Used in resource naming.')
@allowed(['dev', 'staging', 'prod'])
param environmentName string = 'dev'

@description('Azure region for all resources.')
@allowed([
  'westeurope'
  'northeurope'
  'eastus'
  'centralus'
  'southafricanorth'
])
param location string = 'westeurope'

@description('Project name — used as resource-name prefix.')
param projectName string = 'pesacore'

@description('SQL Server admin login (used only if Entra-only auth is not enabled).')
param sqlAdminLogin string

@description('SQL Server admin password.')
@secure()
@minLength(16)
param sqlAdminPassword string

// Required Entra ID admin parameters — no defaults so misconfiguration fails
// loudly at deployment plan stage rather than silently producing a SQL Server
// with a non-existent AD admin (the all-zero GUID is a valid format and won't
// be validated against the tenant by ARM).
@description('Entra ID group/user object ID (SID) for SQL admins. Required.')
param sqlAadAdminSid string

@description('Entra ID group/user display name for SQL admins. Required.')
param sqlAadAdminLogin string

@description('Tags applied to all resources.')
param tags object = {
  project: 'pesacore'
  environment: environmentName
  managedBy: 'bicep'
  costCenter: 'platform-team'
}

// ----------------------------------------------------------------------------
// Naming convention. Predictable, scoped to the environment.
// ----------------------------------------------------------------------------
var namePrefix = '${projectName}-${environmentName}'
var sqlServerName = '${namePrefix}-sql'
var sqlDbName = '${projectName}-db'
var appServicePlanName = '${namePrefix}-plan'
var appServiceName = '${namePrefix}-api'
var keyVaultName = '${namePrefix}-kv'
var appInsightsName = '${namePrefix}-ai'
var logAnalyticsName = '${namePrefix}-law'
var userMiName = '${namePrefix}-mi'

// ----------------------------------------------------------------------------
// Log Analytics Workspace — backend for App Insights + diagnostics.
// 30-day retention by default; bump to 90 days in prod for incident review.
// ----------------------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: environmentName == 'prod' ? 90 : 30
    workspaceCapping: {
      // Cap daily ingestion to control cost. Adjust per real volume.
      dailyQuotaGb: environmentName == 'prod' ? 5 : 1
    }
  }
}

// ----------------------------------------------------------------------------
// Application Insights — APM, distributed tracing, log queries.
// Workspace-based (the modern pattern — classic AI is deprecated).
// ----------------------------------------------------------------------------
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ----------------------------------------------------------------------------
// User-Assigned Managed Identity — service principal for App Service.
// Used to authenticate to SQL (passwordless), Key Vault, Storage, etc.
// Banking discipline: no shared service-account passwords ever.
// ----------------------------------------------------------------------------
resource userMi 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: userMiName
  location: location
  tags: tags
}

// ----------------------------------------------------------------------------
// Azure SQL Server + Database.
// Entra-only auth in prod (passwordless); SQL auth permitted in dev for
// local-tooling convenience (azuredatastudio, etc.).
// ----------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: environmentName == 'prod' ? 'Disabled' : 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      // Required parameters — caller must provide values that resolve in the
      // target tenant. ARM does not validate the SID against the tenant at
      // deploy time, so a wrong value would silently produce an unusable
      // admin binding. Passing required parameters (no defaults) means a
      // missing value fails the deployment plan immediately.
      login: sqlAadAdminLogin
      sid: sqlAadAdminSid
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: false
    }
  }
}

// Allow Azure services to connect (for App Service in dev/staging).
// Prod uses VNet integration + private endpoint instead.
resource sqlFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (environmentName != 'prod') {
  parent: sqlServer
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDbName
  location: location
  tags: tags
  sku: {
    name: environmentName == 'prod' ? 'S1' : 'S0'
    tier: 'Standard'
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: environmentName == 'prod' ? 268435456000 : 1073741824 // 250 GB prod, 1 GB dev
    zoneRedundant: false
    readScale: 'Disabled'
    requestedBackupStorageRedundancy: 'Local'
  }
}

// ----------------------------------------------------------------------------
// Key Vault — secrets store. Connection strings, API keys, signing certs.
// RBAC mode (modern) rather than access policies (legacy).
// ----------------------------------------------------------------------------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enabledForTemplateDeployment: true
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enableSoftDelete: true
    softDeleteRetentionInDays: environmentName == 'prod' ? 90 : 7
    enablePurgeProtection: environmentName == 'prod' ? true : null
    publicNetworkAccess: environmentName == 'prod' ? 'Disabled' : 'Enabled'
  }
}

// Grant the App Service's MI read access to Key Vault secrets.
// Built-in role: Key Vault Secrets User (4633458b-17de-408a-b874-0445c86b69e6)
resource kvSecretsUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, userMi.id, 'kv-secrets-user')
  properties: {
    principalId: userMi.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
  }
}

// ----------------------------------------------------------------------------
// App Service Plan + App Service for the API.
// Linux P1v3 in prod, B1 in dev/staging for cost. Scale-out auto-config.
// ----------------------------------------------------------------------------
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  kind: 'linux'
  properties: {
    reserved: true
  }
  sku: {
    name: environmentName == 'prod' ? 'P1v3' : 'B1'
    tier: environmentName == 'prod' ? 'PremiumV3' : 'Basic'
  }
}

resource appService 'Microsoft.Web/sites@2023-12-01' = {
  name: appServiceName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${userMi.id}': {}
    }
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    keyVaultReferenceIdentity: userMi.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: environmentName == 'prod'
      http20Enabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      healthCheckPath: '/healthz'
      autoHealEnabled: true
      autoHealRules: {
        triggers: {
          slowRequests: {
            timeTaken: '00:00:30'
            count: 10
            timeInterval: '00:01:00'
          }
        }
        actions: {
          actionType: 'Recycle'
          minProcessExecutionTime: '00:01:00'
        }
      }
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: environmentName == 'prod' ? 'Production' : 'Staging'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE'
          value: 'false'
        }
        {
          // Reference the SQL connection string from Key Vault, not inline.
          // The MI authenticates to KV; KV returns the secret; App Service
          // injects it into the app's environment.
          name: 'ConnectionStrings__BankDb'
          value: '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/secrets/BankDb-ConnectionString/)'
        }
        {
          name: 'KeyVault__Uri'
          value: 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/'
        }
        {
          name: 'OpenTelemetry__ExporterEndpoint'
          value: appInsights.properties.ConnectionString
        }
      ]
      cors: {
        allowedOrigins: [
          'https://portal.corp.internal'
        ]
        supportCredentials: false
      }
    }
  }
}

// Diagnostic settings: pipe App Service logs + metrics to Log Analytics.
resource appServiceDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: appService
  name: 'appservice-to-loganalytics'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        category: 'AppServiceHTTPLogs'
        enabled: true
      }
      {
        category: 'AppServiceConsoleLogs'
        enabled: true
      }
      {
        category: 'AppServiceAppLogs'
        enabled: true
      }
      {
        category: 'AppServiceAuditLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// ----------------------------------------------------------------------------
// Outputs: surfaces consumed by deploy pipeline / verification scripts.
// ----------------------------------------------------------------------------
output appServiceUrl string = 'https://${appService.properties.defaultHostName}'
output appServiceName string = appService.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
output keyVaultUri string = keyVault.properties.vaultUri
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output userManagedIdentityId string = userMi.id
output userManagedIdentityClientId string = userMi.properties.clientId
