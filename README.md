# Publish Test Results to Azure DevOps (staging)

GitHub Action that publishes test results from a GitHub runner to Azure DevOps Test Plans.
It reuses the Azure DevOps PublishTestResults library (Ta) from NuGet, so parsing and upload
match the Azure Pipelines task.

> Staging repo for the POC. The production home will be a `microsoft/` repo after OSS approval.

## Usage

```yaml
permissions:
  id-token: write   # GitHub OIDC -> Entra token for Azure DevOps
  contents: read

steps:
  - uses: <owner>/ado-publish-test-results@v1
    with:
      results-dir: TestResults
      pattern: '*.trx'          # or TEST-*.xml with test-runner: JUnit
      test-runner: VSTest       # VSTest | JUnit | NUnit | XUnit | CTest
      collection-url: https://dev.azure.com/<org>
      project: <project>
      client-id: <entra-app-client-id>
      tenant-id: <entra-tenant-id>
```

## How it works

1. `index.js` gets an Azure DevOps token (GitHub OIDC exchanged with Entra).
2. It runs `dotnet build publish.cs` and then `dotnet run publish.cs`. .NET restores the Ta NuGet package.
3. `publish.cs` parses the files with Ta and publishes a build-less test run.
4. The run is marked with `TestRunSystem=GitHubActions` and a `source=GitHubActions; repo=...; runId=...` comment.
5. The run link is written to the job summary and to the `run-url` output.

## Requirements

- .NET 10 SDK on the runner (preinstalled on GitHub-hosted runners).
- Network access to nuget.org and to Azure DevOps.
- An Entra app with a federated credential for your repo, added to the Azure DevOps org with Test Plans write access.
