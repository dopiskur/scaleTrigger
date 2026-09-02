# Deploying ScaleTrigger to Azure

Three ways to run ScaleTrigger, roughly ordered from "just try it" to "how you'd actually operate it."

## 1. Quick deploy — "Deploy to Azure" button

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fdopiskur%2FscaleTrigger%2Fmaster%2Fdeploy%2Fazure%2Fmain.json)

Every field already has a default, so you can literally just click "Review + create" and deploy — nothing is required. This is a **subscription-scope** template: there's no resource group to pick beforehand, it creates one itself (named `ScaleTrigger` by default, via the `resourceGroupName` parameter) and deploys into that. This provisions:

- The **resource group** itself (`resourceGroupName`, default `ScaleTrigger`) — created if it doesn't exist yet, reused as-is if it does.
- A **Linux App Service Plan** on the `S1` (Standard) tier by default — deliberately not Basic/Free, since autoscale rules (the entire point of this tool) require Standard tier or higher.
- The **App Service** itself (`appServiceName`, default `ScaleTrigger` — must be globally unique across Azure since it becomes `<name>.azurewebsites.net`, so change it if that default is already taken), with GitHub source control pointed at this repo's `master` branch (`isManualIntegration: true` — works against a public repo with no token or publish profile). App Service's own Oryx build picks up `ScaleTrigger.sln` and builds it on first push, so the site is live shortly after the deployment finishes, no separate CI step needed.
- Every **Application Setting** `appsettings.json.example` seeds the app from (`DatabaseProvider`, `ConnectionStrings__*`, `Auth__Enabled`, `Load__*`, `NodeBenchmark__*`, `Jwt__*`, `AdminUser__*`, ...), using the same `:` → `__` nesting convention as any other Azure App Service deployment of this app.

Everything past `resourceGroupName`/`appServiceName` has a sensible default too (matching `appsettings.json.example`) and is optional in the portal's parameter form. Notably: `databaseProvider` defaults to `Sqlite`, which needs no external database and is enough to confirm the deploy actually works — but SQLite's file lives on the App Service's ephemeral storage (wiped on restart/scale), so switch `databaseProvider` and the matching `connectionString*` parameter to a real database (MSSQL/MySQL/PostgreSQL) before running anything you'd mind losing.

The button always deploys **the current `main.json` on `master`**, kept in sync automatically — see "Keeping main.json in sync" below.

This scenario provisions infrastructure and gets a first build running; it's not meant to be re-run on every code change (see scenario 2 for that).

### Deploying by hand instead of the button

Subscription-scope, so this targets a region directly instead of an existing resource group — `resourceGroupName` (default `ScaleTrigger`) controls which resource group gets created/reused:

```bash
az deployment sub create \
  --location <azure-region> \
  --template-file deploy/azure/main.bicep
```

## 2. Repeatable deploys — GitHub Actions

For every push after the first deploy, use the existing workflow instead of re-running the template: [`.github/workflows/deploy-api.yml`](../../.github/workflows/deploy-api.yml). It builds and publishes `ScaleTrigger` via `azure/webapps-deploy`, triggered on any push to `master` that touches `ScaleTrigger/**`.

One-time setup (see the main [README's "Deploying via GitHub Actions" section](../../README.md) for the full walkthrough):

1. Set `AZURE_WEBAPP_NAME` in `deploy-api.yml` to the App Service name you deployed in scenario 1 (or created any other way).
2. Download that App Service's *Publish Profile* (Azure Portal → App Service → "Get publish profile") and store it as the `AZURE_WEBAPP_PUBLISH_PROFILE_API` repository secret.

This workflow only pushes code — it doesn't touch Application Settings. Change those in the portal, via `az webapp config appsettings set`, or by re-running the Bicep template (scenario 1) with updated parameter values.

## 3. Local, no Azure at all — docker-compose

For development or testing the app itself, without any of the above:

```bash
docker compose up --build
```

Brings up ScaleTrigger and a local PostgreSQL container together, schema created automatically, no cloud account needed. See the main [README's Quickstart](../../README.md#quickstart) for the full walkthrough (benchmarking, sizing load, generating traffic).

## Keeping main.json in sync

[`.github/workflows/build-bicep.yml`](../../.github/workflows/build-bicep.yml) runs `az bicep build` against `main.bicep` on every push that touches it and commits the regenerated `main.json` back to the branch if it changed. This means the Deploy-to-Azure button above always points at a `main.json` that matches the current `main.bicep` — you never need to run `az bicep build` by hand after editing the template (though `az bicep build --file deploy/azure/main.bicep` locally is the fastest way to check your edits before pushing).
