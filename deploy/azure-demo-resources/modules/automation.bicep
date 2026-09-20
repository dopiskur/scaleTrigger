param location string = resourceGroup().location
param resourcePrefix string = 'ScaleTrigger'
param singleVmResourceGroup string
param servicePlanResourceGroup string
param scaleSetResourceGroup string
param sqlResourceGroupName string
param logsResourceGroupName string
param approvalNotificationUpn string = 'dummy@somemail.com'
param vmCpuThreshold int = 80
param planCpuThreshold int = 80
param autoShutdownEnabled bool = true
param logAnalyticsWorkspaceId string

@description('UTC hour (0-23) the daily VMSS shutdown schedule fires at. Kept in UTC (rather than a named time zone) so the schedule start time can be computed declaratively without a timezone/DST-aware function.')
param autoShutdownHour int = 5

@description('Raw GitHub URLs the three runbooks are published from - override if deploying from a fork/branch.')
param teardownRunbookUri string = 'https://raw.githubusercontent.com/dopiskur/scaleTrigger/master/deploy/azure-demo-resources/scripts/teardown-runbook.ps1'
param stopVmssRunbookUri string = 'https://raw.githubusercontent.com/dopiskur/scaleTrigger/master/deploy/azure-demo-resources/scripts/stop-vmss-runbook.ps1'
param loadRunbookUri string = 'https://raw.githubusercontent.com/dopiskur/scaleTrigger/master/deploy/azure-demo-resources/scripts/scaleTriggerLoad-runbook.py'

@description('Used only to compute a schedule start time in the future - not a meaningful user-facing parameter.')
param deploymentTime string = utcNow('yyyy-MM-ddTHH:mm:ssZ')

var armEndpoint = environment().resourceManager
var vmName = '${resourcePrefix}-vm'
var vmssName = '${resourcePrefix}-vmss'
var vmResourceId = resourceId(subscription().subscriptionId, singleVmResourceGroup, 'Microsoft.Compute/virtualMachines', vmName)
var servicePlanResourceGroupId = '/subscriptions/${subscription().subscriptionId}/resourceGroups/${servicePlanResourceGroup}'
var webAppName = toLower('${resourcePrefix}-webapp-${uniqueString(servicePlanResourceGroupId)}')
var servicePlanName = '${webAppName}-plan'
var planResourceId = resourceId(subscription().subscriptionId, servicePlanResourceGroup, 'Microsoft.Web/serverfarms', servicePlanName)

// Next autoShutdownHour:00 UTC after deploymentTime - today if not yet past, otherwise tomorrow.
var todayDate = substring(deploymentTime, 0, 10)
var paddedShutdownHour = padLeft(string(autoShutdownHour), 2, '0')
var todayShutdownTime = '${todayDate}T${paddedShutdownHour}:00:00Z'
var scheduleStartTime = dateTimeToEpoch(todayShutdownTime) > dateTimeToEpoch(deploymentTime) ? todayShutdownTime : dateTimeAdd(todayShutdownTime, 'P1D')

resource logicAppVm 'Microsoft.Logic/workflows@2019-05-01' = {
  name: '${resourcePrefix}-la-vm-resize'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    state: 'Enabled'
    definition: {
      '$schema': 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
      contentVersion: '1.0.0.0'
      triggers: {
        manual: {
          type: 'Request'
          kind: 'Http'
          inputs: { schema: {} }
        }
      }
      actions: {
        CheckAlertState: {
          type: 'If'
          expression: {
            equals: [
              '@triggerBody()?[\'data\']?[\'essentials\']?[\'monitorCondition\']'
              'Fired'
            ]
          }
          actions: {
            ScaleUp: {
              type: 'Http'
              inputs: {
                method: 'PATCH'
                uri: uri(armEndpoint, '${vmResourceId}?api-version=2024-07-01')
                authentication: { type: 'ManagedServiceIdentity', audience: armEndpoint }
                headers: { 'Content-Type': 'application/json' }
                body: { properties: { hardwareProfile: { vmSize: 'Standard_B1ms' } } }
              }
            }
          }
          else: {
            actions: {
              ScaleDown: {
                type: 'Http'
                inputs: {
                  method: 'PATCH'
                  uri: uri(armEndpoint, '${vmResourceId}?api-version=2024-07-01')
                  authentication: { type: 'ManagedServiceIdentity', audience: armEndpoint }
                  headers: { 'Content-Type': 'application/json' }
                  body: { properties: { hardwareProfile: { vmSize: 'Standard_B1s' } } }
                }
              }
            }
          }
        }
      }
    }
  }
}

resource logicAppVmDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: logicAppVm
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      { category: 'WorkflowRuntime', enabled: true }
    ]
  }
}

module vmRoleGrant 'grant-role-vm.bicep' = {
  name: 'grant-role-vm'
  scope: resourceGroup(singleVmResourceGroup)
  params: {
    vmName: vmName
    principalId: logicAppVm.identity.principalId
  }
}

resource actionGroupVm 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${resourcePrefix}-ag-vm'
  location: 'global'
  properties: {
    groupShortName: 'stVmScale'
    enabled: true
    logicAppReceivers: [
      {
        name: 'scaleTriggerLogicAppVm'
        resourceId: logicAppVm.id
        callbackUrl: listCallbackUrl('${logicAppVm.id}/triggers/manual', '2019-05-01').value
        useCommonAlertSchema: true
      }
    ]
  }
}

resource alertVm 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${resourcePrefix}-alert-vm-cpu-high'
  location: 'global'
  properties: {
    severity: 2
    enabled: true
    scopes: [vmResourceId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT1M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'HighCpu'
          metricNamespace: 'Microsoft.Compute/virtualMachines'
          metricName: 'Percentage CPU'
          operator: 'GreaterThan'
          threshold: vmCpuThreshold
          timeAggregation: 'Average'
        }
      ]
    }
    actions: [ { actionGroupId: actionGroupVm.id } ]
    autoMitigate: true
  }
}

resource logicAppPlan 'Microsoft.Logic/workflows@2019-05-01' = {
  name: '${resourcePrefix}-la-plan-resize-approval'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    state: 'Enabled'
    definition: {
      '$schema': 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
      contentVersion: '1.0.0.0'
      triggers: {
        manual: {
          type: 'Request'
          kind: 'Http'
          inputs: { schema: {} }
        }
      }
      actions: {
        ScaleUpPlan: {
          type: 'Http'
          inputs: {
            method: 'PATCH'
            uri: uri(armEndpoint, '${planResourceId}?api-version=2023-12-01')
            authentication: { type: 'ManagedServiceIdentity', audience: armEndpoint }
            headers: { 'Content-Type': 'application/json' }
            body: { sku: { name: 'P1v3', tier: 'PremiumV3' } }
          }
        }
      }
    }
  }
}

resource logicAppPlanDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: logicAppPlan
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      { category: 'WorkflowRuntime', enabled: true }
    ]
  }
}

module planRoleGrant 'grant-role-plan.bicep' = {
  name: 'grant-role-plan'
  scope: resourceGroup(servicePlanResourceGroup)
  params: {
    planName: servicePlanName
    principalId: logicAppPlan.identity.principalId
  }
}

resource actionGroupPlan 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${resourcePrefix}-ag-plan'
  location: 'global'
  properties: {
    groupShortName: 'stPlanEsc'
    enabled: true
    azureAppPushReceivers: [
      {
        name: 'approvalPush'
        emailAddress: approvalNotificationUpn
      }
    ]
  }
}

resource alertPlan 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${resourcePrefix}-alert-plan-cpu-high-escalation'
  location: 'global'
  properties: {
    severity: 1
    enabled: true
    scopes: [planResourceId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT1M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'SustainedHighCpu'
          metricNamespace: 'Microsoft.Web/serverfarms'
          metricName: 'CpuPercentage'
          operator: 'GreaterThan'
          threshold: planCpuThreshold
          timeAggregation: 'Average'
        }
      ]
    }
    actions: [ { actionGroupId: actionGroupPlan.id } ]
    autoMitigate: true
  }
}

resource automationAccount 'Microsoft.Automation/automationAccounts@2023-11-01' = {
  name: '${resourcePrefix}-automation'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    sku: { name: 'Free' }
  }
}

resource teardownRunbook 'Microsoft.Automation/automationAccounts/runbooks@2023-11-01' = {
  parent: automationAccount
  name: 'Remove-DemoResources'
  location: location
  properties: {
    runbookType: 'PowerShell'
    logProgress: false
    logVerbose: false
    publishContentLink: {
      uri: teardownRunbookUri
    }
  }
}

resource stopVmssRunbook 'Microsoft.Automation/automationAccounts/runbooks@2023-11-01' = if (autoShutdownEnabled) {
  parent: automationAccount
  name: 'Stop-ScaleSetInstances'
  location: location
  properties: {
    runbookType: 'PowerShell'
    logProgress: false
    logVerbose: false
    publishContentLink: {
      uri: stopVmssRunbookUri
    }
  }
}

// Load generator for the ScaleTrigger API (scaleTriggerLoad-runbook.py) - stdlib-only Python
// counterpart to scripts/scaleTriggerLoad.py, run as a runbook job instead of a local python.exe
// process. Takes no RBAC role grant: it only makes outbound HTTP calls to the app URL.
resource loadRunbook 'Microsoft.Automation/automationAccounts/runbooks@2023-11-01' = {
  parent: automationAccount
  name: 'Invoke-ScaleTriggerLoad'
  location: location
  properties: {
    runbookType: 'Python3'
    logProgress: false
    logVerbose: false
    publishContentLink: {
      uri: loadRunbookUri
    }
  }
}

resource dailyShutdownSchedule 'Microsoft.Automation/automationAccounts/schedules@2023-11-01' = if (autoShutdownEnabled) {
  parent: automationAccount
  name: 'daily-vmss-shutdown'
  properties: {
    startTime: scheduleStartTime
    frequency: 'Day'
    interval: 1
    timeZone: 'UTC'
  }
}

// jobSchedules isn't idempotent (Conflict on redeploy), so this registers via script instead of a declarative resource.
var automationContributorRoleId = 'f353d9bd-d4a6-484e-a77a-8050b599b867'

resource jobScheduleScriptIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (autoShutdownEnabled) {
  name: '${resourcePrefix}-jobschedule-script-identity'
  location: location
}

resource jobScheduleScriptRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (autoShutdownEnabled) {
  scope: automationAccount
  name: guid(automationAccount.id, jobScheduleScriptIdentity.id, automationContributorRoleId)
  properties: {
    #disable-next-line BCP318
    principalId: jobScheduleScriptIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', automationContributorRoleId)
  }
}

resource registerStopVmssJobSchedule 'Microsoft.Resources/deploymentScripts@2023-08-01' = if (autoShutdownEnabled) {
  name: 'register-stop-vmss-job-schedule'
  location: location
  kind: 'AzurePowerShell'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${jobScheduleScriptIdentity.id}': {}
    }
  }
  properties: {
    azPowerShellVersion: '14.0'
    retentionInterval: 'PT1H'
    cleanupPreference: 'OnSuccess'
    timeout: 'PT10M'
    arguments: '-resourceGroupName ${resourceGroup().name} -automationAccountName ${automationAccount.name} -runbookName ${stopVmssRunbook.name} -scheduleName ${dailyShutdownSchedule.name} -vmssResourceGroup ${scaleSetResourceGroup} -vmssName ${vmssName}'
    scriptContent: '''
      param(
        [string] $resourceGroupName,
        [string] $automationAccountName,
        [string] $runbookName,
        [string] $scheduleName,
        [string] $vmssResourceGroup,
        [string] $vmssName
      )
      $maxAttempts = 5
      for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
          Register-AzAutomationScheduledRunbook `
            -ResourceGroupName $resourceGroupName `
            -AutomationAccountName $automationAccountName `
            -RunbookName $runbookName `
            -ScheduleName $scheduleName `
            -Parameters @{ VmssResourceGroup = $vmssResourceGroup; VmssName = $vmssName } `
            -ErrorAction Stop | Out-Null
          Write-Host "Job schedule registered for $runbookName / $scheduleName."
          break
        }
        catch {
          if ($_.Exception.Message -like '*already*') {
            Write-Host "Job schedule already registered for $runbookName / $scheduleName - nothing to do."
            break
          }
          if ($attempt -eq $maxAttempts) {
            throw
          }
          Write-Host "Attempt $attempt failed ($($_.Exception.Message)), retrying in 15s - likely the role assignment above hasn't propagated yet."
          Start-Sleep -Seconds 15
        }
      }
    '''
  }
  dependsOn: [
    jobScheduleScriptRoleAssignment
  ]
}

// Scoped to the 6 resource groups teardown-runbook.ps1 deletes, not the whole subscription.
var teardownResourceGroups = [
  singleVmResourceGroup
  servicePlanResourceGroup
  scaleSetResourceGroup
  sqlResourceGroupName
  logsResourceGroupName
  resourceGroup().name
]

module automationAccountRoleGrant 'grant-role-resourcegroup.bicep' = [for rg in teardownResourceGroups: {
  name: 'grant-role-automation-${rg}'
  scope: resourceGroup(rg)
  params: {
    principalId: automationAccount.identity.principalId
  }
}]

output logicAppVmName string = logicAppVm.name
output logicAppPlanName string = logicAppPlan.name
output automationAccountName string = automationAccount.name
output teardownRunbookName string = teardownRunbook.name
output stopVmssRunbookNameOut string = autoShutdownEnabled ? 'Stop-ScaleSetInstances' : ''
output loadRunbookName string = loadRunbook.name
#disable-next-line outputs-should-not-contain-secrets
output logicAppPlanTriggerUrl string = listCallbackUrl('${logicAppPlan.id}/triggers/manual', '2019-05-01').value
