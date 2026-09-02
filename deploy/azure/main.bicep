// ScaleTrigger - single App Service deployment (GitHub source control, Oryx build).
// Subscription-scope so the "Deploy to Azure" button needs no pre-existing resource group -
// it creates (or reuses) one named resourceGroupName and deploys modules/app-service.bicep
// into it. See deploy/azure/README.md; compiled to main.json via `az bicep build`.

targetScope = 'subscription'

@description('Resource group to deploy into - created automatically if it does not already exist, reused as-is if it does.')
param resourceGroupName string = 'ScaleTrigger'

@description('Name of the App Service (must be globally unique across all of Azure - becomes <name>.azurewebsites.net). Defaults to "scaletrigger-<random>" instead of a bare "scaletrigger", since that alone is almost certainly already taken by someone else\'s App Service.')
@minLength(2)
@maxLength(60)
param appServiceName string = 'scaletrigger-${uniqueString(subscription().subscriptionId, resourceGroupName)}'

@description('Azure region for all resources.')
param location string = deployment().location

@description('App Service Plan SKU. Autoscale rules (the whole point of this tool) need Standard tier or higher - Basic/Free plans cannot be autoscaled.')
@allowed([
  'S1'
  'S2'
  'S3'
  'P0v3'
  'P1v3'
  'P2v3'
  'P3v3'
])
param skuName string = 'S1'

@description('Public GitHub repository to build and deploy from (Oryx builds ScaleTrigger.sln automatically - no publish profile or GitHub token needed for a public repo).')
param repositoryUrl string = 'https://github.com/dopiskur/scaleTrigger'

@description('Branch to deploy from.')
param branch string = 'master'

@description('Selects the repository implementation - matches appsettings.json.example. Sqlite needs no external database but its file lives on ephemeral App Service storage (wiped on restart/scale) - fine to try the deploy, not for anything you need to keep.')
@allowed([
  'MsSql'
  'MySql'
  'PostgreSql'
  'Sqlite'
])
param databaseProvider string = 'Sqlite'

@description('true = MSSQL only, authenticate via the App Service system-assigned managed identity instead of a connection-string password (the identity still needs to be granted access on the SQL side separately - this template does not do that).')
param useManagedIdentity bool = false

@secure()
@description('MSSQL connection string. Leave empty unless DatabaseProvider is MsSql.')
param connectionStringMsSql string = ''

@secure()
@description('MySQL connection string. Leave empty unless DatabaseProvider is MySql.')
param connectionStringMySql string = ''

@secure()
@description('PostgreSQL connection string. Leave empty unless DatabaseProvider is PostgreSql.')
param connectionStringPostgreSql string = ''

@secure()
@description('SQLite connection string (a relative file path is fine - see DatabaseProvider above for the ephemeral-storage caveat).')
param connectionStringSqlite string = 'Data Source=scaletrigger.db'

@description('true = POST /api/vote/add and other admin actions require a JWT from POST /api/auth/login.')
param authEnabled bool = false

@description('true = refuse to start if the database is unreachable at startup; false = log a critical error and keep running (dashboard shows "Database unavailable" and keeps retrying).')
param failFastOnDbCheck bool = false

param cacheSlidingExpirationMinutes int = 5

param loadConfigRefreshMinSeconds int = 1
param loadConfigRefreshMaxSeconds int = 1

@description('true = per-vote load and the Vote/Payload write run normally; false = POST /api/vote/add is a fast no-op. Live via LoadConfig, shared by every node.')
param loadEnabled bool = true

@description('true = cache GET /api/vote/report in memory; false = every read goes straight to the database. Live via LoadConfig, shared by every node - not a one-time Application Setting like the other params above.')
param loadCacheEnabled bool = true
param loadCpuIterationsPerVoteMin int = 5000
param loadCpuIterationsPerVoteMax int = 20000
param loadMemoryKilobytesPerVoteMin int = 3072
param loadMemoryKilobytesPerVoteMax int = 8192
param loadDiskWriteKilobytesPerVoteMin int = 32
param loadDiskWriteKilobytesPerVoteMax int = 96
param loadNetworkLatencyMillisecondsPerVoteMin int = 10
param loadNetworkLatencyMillisecondsPerVoteMax int = 30
param loadPayloadBytesPerVoteMin int = 0
param loadPayloadBytesPerVoteMax int = 0
param loadDbCpuIterationsPerVoteMin int = 0
param loadDbCpuIterationsPerVoteMax int = 0

param nodeBenchmarkCpuDurationSeconds int = 20
param nodeBenchmarkMemoryBlockMegabytes int = 64
param nodeBenchmarkMemoryRepetitions int = 5
param nodeBenchmarkDiskSizeMegabytes int = 20
param nodeBenchmarkDiskRepetitions int = 5

@secure()
@description('JWT signing key, at least 32 characters. The appsettings.json.example placeholder is reused as the default since this is a stress-test tool with no real secrets to protect by default - replace it if that ever stops being true.')
param jwtKey string = 'abcdefghijklmnopqrstuvwxyz012345'

param jwtIssuer string = 'ScaleTrigger'
param jwtAudience string = 'ScaleTrigger.Clients'
param jwtExpirationMinutes int = 60

@description('POST /api/auth/login username.')
param adminUsername string = 'admin'

@secure()
@description('POST /api/auth/login password. Default matches appsettings.json.example - change it if Auth:Enabled is ever true outside a throwaway environment.')
param adminPassword string = 'admin'

param loggingLevelDefault string = 'Information'
param loggingLevelAspNetCore string = 'Warning'
param allowedHosts string = '*'

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module appService 'modules/app-service.bicep' = {
  name: 'deploy-app-service'
  scope: rg
  params: {
    appServiceName: appServiceName
    location: location
    skuName: skuName
    repositoryUrl: repositoryUrl
    branch: branch
    databaseProvider: databaseProvider
    useManagedIdentity: useManagedIdentity
    connectionStringMsSql: connectionStringMsSql
    connectionStringMySql: connectionStringMySql
    connectionStringPostgreSql: connectionStringPostgreSql
    connectionStringSqlite: connectionStringSqlite
    authEnabled: authEnabled
    failFastOnDbCheck: failFastOnDbCheck
    cacheSlidingExpirationMinutes: cacheSlidingExpirationMinutes
    loadConfigRefreshMinSeconds: loadConfigRefreshMinSeconds
    loadConfigRefreshMaxSeconds: loadConfigRefreshMaxSeconds
    loadEnabled: loadEnabled
    loadCacheEnabled: loadCacheEnabled
    loadCpuIterationsPerVoteMin: loadCpuIterationsPerVoteMin
    loadCpuIterationsPerVoteMax: loadCpuIterationsPerVoteMax
    loadMemoryKilobytesPerVoteMin: loadMemoryKilobytesPerVoteMin
    loadMemoryKilobytesPerVoteMax: loadMemoryKilobytesPerVoteMax
    loadDiskWriteKilobytesPerVoteMin: loadDiskWriteKilobytesPerVoteMin
    loadDiskWriteKilobytesPerVoteMax: loadDiskWriteKilobytesPerVoteMax
    loadNetworkLatencyMillisecondsPerVoteMin: loadNetworkLatencyMillisecondsPerVoteMin
    loadNetworkLatencyMillisecondsPerVoteMax: loadNetworkLatencyMillisecondsPerVoteMax
    loadPayloadBytesPerVoteMin: loadPayloadBytesPerVoteMin
    loadPayloadBytesPerVoteMax: loadPayloadBytesPerVoteMax
    loadDbCpuIterationsPerVoteMin: loadDbCpuIterationsPerVoteMin
    loadDbCpuIterationsPerVoteMax: loadDbCpuIterationsPerVoteMax
    nodeBenchmarkCpuDurationSeconds: nodeBenchmarkCpuDurationSeconds
    nodeBenchmarkMemoryBlockMegabytes: nodeBenchmarkMemoryBlockMegabytes
    nodeBenchmarkMemoryRepetitions: nodeBenchmarkMemoryRepetitions
    nodeBenchmarkDiskSizeMegabytes: nodeBenchmarkDiskSizeMegabytes
    nodeBenchmarkDiskRepetitions: nodeBenchmarkDiskRepetitions
    jwtKey: jwtKey
    jwtIssuer: jwtIssuer
    jwtAudience: jwtAudience
    jwtExpirationMinutes: jwtExpirationMinutes
    adminUsername: adminUsername
    adminPassword: adminPassword
    loggingLevelDefault: loggingLevelDefault
    loggingLevelAspNetCore: loggingLevelAspNetCore
    allowedHosts: allowedHosts
  }
}

output appServiceUrl string = appService.outputs.appServiceUrl
output appServiceName string = appService.outputs.appServiceName
