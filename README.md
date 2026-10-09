# Publish Test Results to Azure DevOps (staging)

GitHub Action that publishes test results from a GitHub runner to Azure DevOps Test Plans.
It reuses the Azure DevOps PublishTestResults library (Ta) from NuGet, so parsing and upload
match the Azure Pipelines task.

> Staging repo for the POC. The production home will be a `microsoft/` repo after OSS approval.

## Latest-package POC

This POC defaults to `Microsoft.TeamFoundation.PublishTestResults` `20.278.1-preview`, the latest
publicly installable version on nuget.org when this POC was published. The newer listed
`20.279.0-preview` cannot currently restore because its pinned
`Microsoft.VisualStudio.Services.BlobStore.Client` dependency is not publicly available.

The default is pinned so action runs remain reproducible. Consumers can test a newer concrete
version with the `ta-package-version` input. The resolved assembly version is printed in the log
and exposed as the `ta-package-version` action output. Update the action's default only after the
new version passes Windows, Linux, and macOS compatibility runs.

## Usage

```yaml
permissions:
  id-token: write   # GitHub OIDC -> Entra token for Azure DevOps
  contents: read

steps:
  - uses: haddanki-msft/ado-publish-test-results@v0.1.0
    with:
      results-dir: TestResults
      pattern: '*.trx'          # or TEST-*.xml with test-runner: JUnit
      test-runner: VSTest       # VSTest | JUnit | NUnit | XUnit | CTest
      collection-url: https://dev.azure.com/<org>
      project: <project>
      client-id: <entra-app-client-id>
      tenant-id: <entra-tenant-id>
      ta-package-version: 20.278.1-preview # optional; defaults to latest validated version
```

## How it works

1. The composite `action.yml` restores the Ta NuGet package and compiles `publish.cs`.
2. `publish.cs` exchanges the GitHub OIDC assertion with Entra for an Azure DevOps token.
3. `publish.cs` parses the files with Ta and publishes a build-less test run.
4. The run is marked with `TestRunSystem=GitHubActions` and a `source=GitHubActions; repo=...; runId=...` comment.
5. The run link is written to the job summary and to the `run-url` output.

## Requirements

- .NET 10 SDK on the runner (preinstalled on GitHub-hosted runners).
- Network access to nuget.org and to Azure DevOps.
- An Entra app with a federated credential for your repo, added to the Azure DevOps org with Test Plans write access.
