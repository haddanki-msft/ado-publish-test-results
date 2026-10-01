// POC: .NET 10 file-based app. The Ta NuGet package is restored from nuget.org when the
// action runs; no prebuilt zip is shipped.
#:package Microsoft.TeamFoundation.PublishTestResults@20.278.1-preview
#:property PublishAot=false
#:property RollForward=Major
#:property SatelliteResourceLanguages=en

using System.Diagnostics;
using System.Text.Json;
using Microsoft.TeamFoundation.TestClient.PublishTestResults;
using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

string Env(string name, string fallback = "") =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

var resultsDir = Env("PTR_RESULTS_DIR", ".");
var pattern = Env("PTR_PATTERN", "*.trx");
var runner = Env("PTR_TEST_RUNNER", "VSTest");
var dryRun = Env("PTR_DRY_RUN", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
var runName = Env("PTR_RUN_TITLE", $"GitHub {Env("GITHUB_REPOSITORY", "local")} run {Env("GITHUB_RUN_ID", "0")}");
var summaryFile = Env("PTR_SUMMARY_FILE");
var token = Env("ADO_ACCESS_TOKEN");

var trace = new ConsoleTraceListener(useErrorStream: true);

var files = Directory.Exists(resultsDir)
    ? Directory.EnumerateFiles(resultsDir, pattern, SearchOption.AllDirectories).Select(Path.GetFullPath).OrderBy(f => f, StringComparer.Ordinal).ToList()
    : new List<string>();
if (files.Count == 0)
{
    Console.Error.WriteLine($"error: no files matched '{pattern}' under '{resultsDir}'.");
    return 1;
}
Console.Error.WriteLine($"[ptr] {files.Count} {runner} file(s) found.");

// GitHub has no ADO build: buildId 0 keeps the run build-less. Platform and configuration must stay
// empty, because ADO rejects them without a build ID. ADO stores TestRunSystem in 16 characters.
const string TestRunSystem = "GitHubActions";
var context = new TestRunContext(null, null, null, 0, null, null, null, runName, TestRunSystem);

ITestResultParser parser = runner switch
{
    "VSTest" => new TrxResultParser(trace),
    "JUnit" => new JUnitResultParser(trace),
    "NUnit" => new NUnitResultParser(trace),
    "XUnit" => new XUnitResultParser(trace),
    "CTest" => new CTestResultParser(trace),
    _ => throw new ArgumentException($"Unsupported test runner '{runner}'."),
};

var runData = parser.ParseTestResultFiles(context, files)?.GetTestRunData();
if (runData is null || runData.Count == 0)
{
    Console.Error.WriteLine("error: parser returned no test run data.");
    return 1;
}
var parsed = runData.Sum(r => r.TestResults?.Count ?? 0);
Console.Error.WriteLine($"[ptr] parsed {parsed} result(s).");

// Tags need a build or release, so the GitHub identity goes into the run comment in a
// fixed key=value format that tools can parse. TestRunSystem marks the source.
var server = Env("GITHUB_SERVER_URL", "https://github.com");
var repo = Env("GITHUB_REPOSITORY", "local");
var ghRunId = Env("GITHUB_RUN_ID", "0");
var origin = string.Join("; ",
    "source=GitHubActions",
    $"repo={repo}",
    $"workflow={Env("GITHUB_WORKFLOW")}",
    $"job={Env("GITHUB_JOB")}",
    $"runId={ghRunId}",
    $"attempt={Env("GITHUB_RUN_ATTEMPT", "1")}",
    $"ref={Env("GITHUB_REF")}",
    $"sha={Env("GITHUB_SHA")}",
    $"event={Env("GITHUB_EVENT_NAME")}",
    $"runner={Env("RUNNER_ENVIRONMENT")}/{Env("RUNNER_OS")}-{Env("RUNNER_ARCH")}",
    $"url={server}/{repo}/actions/runs/{ghRunId}");

var summary = new Dictionary<string, object?> { ["runName"] = runName, ["runner"] = runner, ["parsedResults"] = parsed, ["dryRun"] = dryRun };
var exitCode = 0;

if (!dryRun)
{
    if (string.IsNullOrEmpty(token))
    {
        Console.Error.WriteLine("error: ADO_ACCESS_TOKEN is not set.");
        return 1;
    }
    var collectionUrl = new Uri(Env("PTR_COLLECTION_URL"));
    var project = Env("PTR_PROJECT");
    var connection = new VssConnection(collectionUrl, new VssOAuthAccessTokenCredential(token));
    using var publisher = new TestRunPublisher(connection, trace);
    var sw = Stopwatch.StartNew();
    var runs = await publisher.PublishTestRunDataAsync(context, project, runData, new PublishOptions { IsAddTestRunAttachments = true }, CancellationToken.None);
    Console.Error.WriteLine($"[ptr] published {runs?.Count ?? 0} run(s) in {sw.Elapsed.TotalSeconds:F1}s.");
    if (runs is null || runs.Count == 0)
    {
        Console.Error.WriteLine("error: no test run was published.");
        return 1;
    }

    var client = connection.GetClient<TestManagementHttpClient>();
    var list = new List<object>();
    foreach (var run in runs)
    {
        // RunCreateModel.Comment is read-only in Ta, so stamp the GitHub identity right after creation.
        await client.UpdateTestRunAsync(new RunUpdateModel(comment: origin), project, run.Id);
        var r = await client.GetTestRunByIdAsync(project, run.Id);
        var url = $"{collectionUrl.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_testManagement/runs?runId={r.Id}&_a=runCharts";
        list.Add(new { r.Id, r.State, r.TotalTests, r.PassedTests, r.NotApplicableTests, r.Comment, url });
        if (r.TotalTests != r.PassedTests + r.NotApplicableTests) exitCode = 2;
    }
    summary["runs"] = list;
    summary["hasFailures"] = exitCode == 2;
}

var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (summaryFile.Length > 0) File.WriteAllText(summaryFile, json);
return exitCode;
