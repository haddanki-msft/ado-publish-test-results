// Latest publicly installable Ta prerelease. A newer listed package is not usable because
// its pinned BlobStore dependency has not been published.
#:package Microsoft.TeamFoundation.PublishTestResults@20.278.1-preview
#:property PublishAot=false
#:property RollForward=Major
#:property SatelliteResourceLanguages=en

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.TeamFoundation.TestClient.PublishTestResults;
using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

try
{
    return await RunAsync();
}
catch (Exception exception)
{
    WriteWorkflowCommand("error", exception.Message);
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

async Task<int> RunAsync()
{
    var resultsDir = Path.GetFullPath(Env("PTR_RESULTS_DIR", "."));
    var pattern = Env("PTR_PATTERN", "*.trx");
    var runner = NormalizeRunner(Env("PTR_TEST_RUNNER", "VSTest"));
    if (!bool.TryParse(Env("PTR_DRY_RUN", "false"), out var dryRun))
    {
        throw new ArgumentException("dry-run must be 'true' or 'false'.");
    }

    var runName = Env("PTR_RUN_TITLE", $"GitHub {Env("GITHUB_REPOSITORY", "local")} run {Env("GITHUB_RUN_ID", "0")}");
    var setupSeconds = Env("PTR_SETUP_SECONDS", "0.0");
    var token = Env("ADO_ACCESS_TOKEN");
    Uri? collectionUrl = null;
    var project = Env("PTR_PROJECT");

    if (!dryRun)
    {
        collectionUrl = ValidateCollectionUrl(Env("PTR_COLLECTION_URL"));
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new ArgumentException("project is required unless dry-run is true.");
        }

        if (string.IsNullOrEmpty(token))
        {
            token = await GetAdoTokenAsync(Env("PTR_CLIENT_ID"), Env("PTR_TENANT_ID"));
        }
        WriteWorkflowCommand("add-mask", token);
    }

    var processingStopwatch = Stopwatch.StartNew();
    var taAssembly = typeof(TestRunPublisher).Assembly;
    var taPackageVersion =
        taAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? taAssembly.GetName().Version?.ToString()
        ?? "unknown";
    Console.Error.WriteLine($"[ptr] Ta package version: {taPackageVersion}");

    var trace = new ConsoleTraceListener(useErrorStream: true);
    var files = Directory.Exists(resultsDir)
        ? Directory.EnumerateFiles(resultsDir, pattern, SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList()
        : [];
    if (files.Count == 0)
    {
        throw new FileNotFoundException($"No files matched '{pattern}' under '{resultsDir}'.");
    }
    Console.Error.WriteLine($"[ptr] {files.Count} {runner} file(s) found.");

    // GitHub has no ADO build: buildId 0 keeps the run build-less. Platform and configuration must
    // stay empty because ADO rejects them without a build ID.
    const string testRunSystem = "GitHubActions";
    var context = new TestRunContext(null, null, null, 0, null, null, null, runName, testRunSystem);
    ITestResultParser parser = runner switch
    {
        "VSTest" => new TrxResultParser(trace),
        "JUnit" => new JUnitResultParser(trace),
        "NUnit" => new NUnitResultParser(trace),
        "XUnit" => new XUnitResultParser(trace),
        "CTest" => new CTestResultParser(trace),
        _ => throw new UnreachableException(),
    };

    var runData = parser.ParseTestResultFiles(context, files)?.GetTestRunData();
    if (runData is null || runData.Count == 0)
    {
        throw new InvalidOperationException("The parser returned no test run data.");
    }
    var parsed = runData.Sum(data => data.TestResults?.Count ?? 0);
    Console.Error.WriteLine($"[ptr] parsed {parsed} result(s).");

    var publishedRuns = new List<PublishedRun>();
    var exitCode = 0;
    if (!dryRun)
    {
        var connection = new VssConnection(collectionUrl!, new VssOAuthAccessTokenCredential(token));
        using var publisher = new TestRunPublisher(connection, trace);
        var publishStopwatch = Stopwatch.StartNew();
        var runs = await publisher.PublishTestRunDataAsync(
            context,
            project,
            runData,
            new PublishOptions { IsAddTestRunAttachments = true },
            CancellationToken.None);
        Console.Error.WriteLine($"[ptr] published {runs?.Count ?? 0} run(s) in {publishStopwatch.Elapsed.TotalSeconds:F1}s.");
        if (runs is null || runs.Count == 0)
        {
            throw new InvalidOperationException("No test run was published.");
        }

        var client = connection.GetClient<TestManagementHttpClient>();
        var origin = BuildOrigin();
        foreach (var run in runs)
        {
            // RunCreateModel.Comment is read-only in Ta, so stamp the GitHub identity after creation.
            await client.UpdateTestRunAsync(new RunUpdateModel(comment: origin), project, run.Id);
            var published = await client.GetTestRunByIdAsync(project, run.Id);
            var url = $"{collectionUrl!.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_testManagement/runs?runId={published.Id}&_a=runCharts";
            publishedRuns.Add(new PublishedRun(
                published.Id,
                published.State,
                published.TotalTests,
                published.PassedTests,
                published.NotApplicableTests,
                published.Comment,
                url));
            if (published.TotalTests != published.PassedTests + published.NotApplicableTests)
            {
                exitCode = 2;
            }
        }
    }

    processingStopwatch.Stop();
    var processingLabel = dryRun ? "parse" : "parse + publish";
    var processingSeconds = processingStopwatch.Elapsed.TotalSeconds;
    Console.Error.WriteLine($"[ptr] {processingLabel} took {processingSeconds:F1}s");

    var summary = new
    {
        runName,
        runner,
        taPackageVersion,
        parsedResults = parsed,
        dryRun,
        runs = publishedRuns,
        hasFailures = exitCode == 2,
    };
    Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

    WriteOutput("setup-seconds", setupSeconds);
    WriteOutput("ta-package-version", taPackageVersion);
    if (publishedRuns.Count > 0)
    {
        WriteOutput("run-url", publishedRuns[0].Url);
    }
    WriteSummary(runner, parsed, taPackageVersion, setupSeconds, processingLabel, processingSeconds, publishedRuns);

    if (exitCode == 2)
    {
        WriteWorkflowCommand("warning", "Published run contains failed tests.");
    }
    return exitCode;
}

async Task<string> GetAdoTokenAsync(string clientId, string tenantId)
{
    if (!Guid.TryParse(clientId, out _))
    {
        throw new ArgumentException("client-id must be a valid Entra application ID.");
    }
    if (!Guid.TryParse(tenantId, out _))
    {
        throw new ArgumentException("tenant-id must be a valid Entra tenant ID.");
    }

    var requestUrl = Env("ACTIONS_ID_TOKEN_REQUEST_URL");
    var requestToken = Env("ACTIONS_ID_TOKEN_REQUEST_TOKEN");
    if (string.IsNullOrEmpty(requestUrl) || string.IsNullOrEmpty(requestToken))
    {
        throw new InvalidOperationException("OIDC is unavailable. Add 'permissions: id-token: write' to the job.");
    }

    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var separator = requestUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
    using var oidcRequest = new HttpRequestMessage(
        HttpMethod.Get,
        $"{requestUrl}{separator}audience={Uri.EscapeDataString("api://AzureADTokenExchange")}");
    oidcRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
    using var oidcResponse = await httpClient.SendAsync(oidcRequest);
    if (!oidcResponse.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"GitHub OIDC token request failed with HTTP {(int)oidcResponse.StatusCode}.");
    }

    using var oidcJson = JsonDocument.Parse(await oidcResponse.Content.ReadAsStreamAsync());
    if (!oidcJson.RootElement.TryGetProperty("value", out var assertionElement)
        || string.IsNullOrWhiteSpace(assertionElement.GetString()))
    {
        throw new InvalidOperationException("GitHub OIDC token response did not contain an assertion.");
    }

    const string adoResource = "499b84ac-1321-427f-aa17-267ca6975798";
    using var tokenRequest = new HttpRequestMessage(
        HttpMethod.Post,
        $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token")
    {
        Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = $"{adoResource}/.default",
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertionElement.GetString()!,
        }),
    };
    using var tokenResponse = await httpClient.SendAsync(tokenRequest);
    using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStreamAsync());
    if (!tokenResponse.IsSuccessStatusCode)
    {
        var detail = tokenJson.RootElement.TryGetProperty("error_description", out var error)
            ? OneLine(error.GetString() ?? string.Empty)
            : $"HTTP {(int)tokenResponse.StatusCode}";
        throw new InvalidOperationException($"Entra token exchange failed: {detail}");
    }
    if (!tokenJson.RootElement.TryGetProperty("access_token", out var tokenElement)
        || string.IsNullOrWhiteSpace(tokenElement.GetString()))
    {
        throw new InvalidOperationException("Entra token response did not contain an access token.");
    }
    return tokenElement.GetString()!;
}

string NormalizeRunner(string value) =>
    value.ToUpperInvariant() switch
    {
        "VSTEST" => "VSTest",
        "JUNIT" => "JUnit",
        "NUNIT" => "NUnit",
        "XUNIT" => "XUnit",
        "CTEST" => "CTest",
        _ => throw new ArgumentException($"Unsupported test runner '{value}'."),
    };

Uri ValidateCollectionUrl(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || uri.Scheme != Uri.UriSchemeHttps
        || !string.IsNullOrEmpty(uri.UserInfo)
        || !uri.IsDefaultPort)
    {
        throw new ArgumentException("collection-url must be an Azure DevOps Services HTTPS URL without user information or a custom port.");
    }

    var host = uri.DnsSafeHost;
    const string legacySuffix = ".visualstudio.com";
    var isDevAzure = host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase);
    var isLegacyAzureDevOps =
        host.EndsWith(legacySuffix, StringComparison.OrdinalIgnoreCase)
        && host.Length > legacySuffix.Length
        && !host[..^legacySuffix.Length].Contains('.', StringComparison.Ordinal);
    if (!isDevAzure && !isLegacyAzureDevOps)
    {
        throw new ArgumentException("collection-url host must be dev.azure.com or <organization>.visualstudio.com.");
    }
    return uri;
}

string BuildOrigin()
{
    var server = Env("GITHUB_SERVER_URL", "https://github.com");
    var repo = Env("GITHUB_REPOSITORY", "local");
    var runId = Env("GITHUB_RUN_ID", "0");
    return string.Join("; ",
        "source=GitHubActions",
        $"repo={repo}",
        $"workflow={Env("GITHUB_WORKFLOW")}",
        $"job={Env("GITHUB_JOB")}",
        $"runId={runId}",
        $"attempt={Env("GITHUB_RUN_ATTEMPT", "1")}",
        $"ref={Env("GITHUB_REF")}",
        $"sha={Env("GITHUB_SHA")}",
        $"event={Env("GITHUB_EVENT_NAME")}",
        $"runner={Env("RUNNER_ENVIRONMENT")}/{Env("RUNNER_OS")}-{Env("RUNNER_ARCH")}",
        $"url={server}/{repo}/actions/runs/{runId}");
}

void WriteSummary(
    string runner,
    int parsed,
    string packageVersion,
    string setupSeconds,
    string processingLabel,
    double processingSeconds,
    IReadOnlyList<PublishedRun> runs)
{
    var summaryPath = Env("GITHUB_STEP_SUMMARY");
    if (string.IsNullOrEmpty(summaryPath))
    {
        return;
    }

    var lines = new List<string>
    {
        "### Publish Test Results to Azure DevOps",
        $"- Runner: {OneLine(Env("RUNNER_OS", Environment.OSVersion.Platform.ToString()))}-{OneLine(Env("RUNNER_ARCH", RuntimeInformation.ProcessArchitecture.ToString()))}",
        $"- Ta NuGet package: {OneLine(packageVersion)}",
        $"- Parsed results: {parsed} ({OneLine(runner)})",
        $"- NuGet restore + compile: {OneLine(setupSeconds)}s; {processingLabel}: {processingSeconds:F1}s",
    };
    lines.AddRange(runs.Select(run =>
        $"- ADO run [{run.Id}]({run.Url}): {OneLine(run.State)}, {run.PassedTests}/{run.TotalTests} passed"));
    AppendFile(summaryPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
}

void WriteOutput(string name, string value)
{
    var outputPath = Env("GITHUB_OUTPUT");
    if (string.IsNullOrEmpty(outputPath))
    {
        return;
    }

    var delimiter = $"ptr_{Guid.NewGuid():N}";
    AppendFile(
        outputPath,
        $"{name}<<{delimiter}{Environment.NewLine}{value}{Environment.NewLine}{delimiter}{Environment.NewLine}");
}

void WriteWorkflowCommand(string command, string message) =>
    Console.WriteLine($"::{command}::{EscapeWorkflowData(message)}");

string EscapeWorkflowData(string value) =>
    value.Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);

string OneLine(string value) =>
    value.Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

void AppendFile(string path, string content) =>
    File.AppendAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

string Env(string name, string fallback = "") =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

sealed record PublishedRun(
    int Id,
    string State,
    int TotalTests,
    int PassedTests,
    int NotApplicableTests,
    string Comment,
    string Url);
