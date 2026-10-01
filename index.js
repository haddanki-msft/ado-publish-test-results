// POC JavaScript action with no npm dependencies. It gets an Azure DevOps token through GitHub
// OIDC, then runs publish.cs with `dotnet run`, which restores the Ta NuGet package on the runner.
'use strict';
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const ADO_RESOURCE = '499b84ac-1321-427f-aa17-267ca6975798';

function input(name) {
  return (process.env[`INPUT_${name.toUpperCase()}`] || '').trim();
}

function appendFile(envName, text) {
  const file = process.env[envName];
  if (file) fs.appendFileSync(file, text + os.EOL);
}

function fail(message) {
  console.log(`::error::${message}`);
  process.exit(1);
}

async function getAdoToken(clientId, tenantId) {
  const requestUrl = process.env.ACTIONS_ID_TOKEN_REQUEST_URL;
  const requestToken = process.env.ACTIONS_ID_TOKEN_REQUEST_TOKEN;
  if (!requestUrl || !requestToken) fail('OIDC is unavailable. Add "permissions: id-token: write" to the job.');

  const idResponse = await fetch(`${requestUrl}&audience=api://AzureADTokenExchange`, {
    headers: { Authorization: `Bearer ${requestToken}` },
  });
  if (!idResponse.ok) fail(`GitHub OIDC token request failed: ${idResponse.status}`);
  const { value: assertion } = await idResponse.json();

  const body = new URLSearchParams({
    client_id: clientId,
    scope: `${ADO_RESOURCE}/.default`,
    grant_type: 'client_credentials',
    client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer',
    client_assertion: assertion,
  });
  const tokenResponse = await fetch(`https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/token`, { method: 'POST', body });
  const json = await tokenResponse.json();
  if (!tokenResponse.ok) fail(`Entra token exchange failed: ${json.error_description || tokenResponse.status}`);
  return json.access_token;
}

function ensureDotnetSdk() {
  const result = spawnSync('dotnet', ['--list-sdks'], { encoding: 'utf8' });
  if (result.error || result.status !== 0) fail('dotnet was not found. This POC needs the .NET 10 SDK (preinstalled on GitHub-hosted runners).');
  const sdks = result.stdout.split(/\r?\n/).filter(Boolean);
  if (!sdks.some((line) => Number(line.split('.')[0]) >= 10)) fail(`The .NET 10 SDK is required. Found: ${sdks.join('; ') || 'none'}`);
  console.log(`[ptr] .NET SDKs: ${sdks.map((s) => s.split(' ')[0]).join(', ')}`);
}

async function main() {
  const dryRun = input('dry-run').toLowerCase() === 'true';
  const clientId = input('client-id');
  const tenantId = input('tenant-id');

  ensureDotnetSdk();

  let token = process.env.ADO_ACCESS_TOKEN || '';
  if (!dryRun && !token) {
    if (!clientId || !tenantId) fail('Set client-id and tenant-id, or dry-run: true.');
    token = await getAdoToken(clientId, tenantId);
  }
  if (token) console.log(`::add-mask::${token}`);

  const summaryFile = path.join(process.env.RUNNER_TEMP || os.tmpdir(), `ptr-summary-${process.pid}.json`);
  const env = {
    ...process.env,
    PTR_RESULTS_DIR: path.resolve(input('results-dir') || '.'),
    PTR_PATTERN: input('pattern') || '*.trx',
    PTR_TEST_RUNNER: input('test-runner') || 'VSTest',
    PTR_COLLECTION_URL: input('collection-url'),
    PTR_PROJECT: input('project'),
    PTR_RUN_TITLE: input('run-title'),
    PTR_DRY_RUN: String(dryRun),
    PTR_SUMMARY_FILE: summaryFile,
    ADO_ACCESS_TOKEN: token,
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_NOLOGO: '1',
  };

  const script = path.join(__dirname, 'publish.cs');

  // Build separately so restore + compile time is measured apart from publishing.
  let started = Date.now();
  const build = spawnSync('dotnet', ['build', script], { env, stdio: 'inherit' });
  const setupSeconds = ((Date.now() - started) / 1000).toFixed(1);
  console.log(`[ptr] NuGet restore + compile took ${setupSeconds}s`);
  appendFile('GITHUB_OUTPUT', `setup-seconds=${setupSeconds}`);
  if (build.status !== 0) fail(`dotnet build failed with exit code ${build.status}.`);

  started = Date.now();
  const run = spawnSync('dotnet', ['run', '--no-build', script], { env, stdio: 'inherit' });
  const runSeconds = ((Date.now() - started) / 1000).toFixed(1);
  console.log(`[ptr] parse${dryRun ? '' : ' + publish'} took ${runSeconds}s`);

  if (fs.existsSync(summaryFile)) {
    const summary = JSON.parse(fs.readFileSync(summaryFile, 'utf8'));
    fs.rmSync(summaryFile, { force: true });
    const lines = [
      '### PTR NuGet dummy action (POC)',
      `- Runner: ${process.env.RUNNER_OS || os.platform()}-${process.env.RUNNER_ARCH || os.arch()}`,
      `- Parsed results: ${summary.parsedResults} (${summary.runner})`,
      `- NuGet restore + compile: ${setupSeconds}s; ${dryRun ? 'parse' : 'parse + publish'}: ${runSeconds}s`,
    ];
    for (const r of summary.runs || []) {
      lines.push(`- ADO run [${r.Id}](${r.url}): ${r.State}, ${r.PassedTests}/${r.TotalTests} passed`);
    }
    appendFile('GITHUB_STEP_SUMMARY', lines.join(os.EOL));
    if (summary.runs && summary.runs.length > 0) appendFile('GITHUB_OUTPUT', `run-url=${summary.runs[0].url}`);
  }

  if (run.status === 2) console.log('::warning::Published run contains failed tests.');
  else if (run.status !== 0) fail(`publish.cs exited with code ${run.status}.`);
}

main().catch((error) => fail(error.stack || String(error)));
