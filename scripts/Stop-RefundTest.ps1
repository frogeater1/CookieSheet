$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$stateDir = Join-Path $projectRoot '.local/refund-test'
$statePath = Join-Path $stateDir 'environment.json'
if (-not (Test-Path -LiteralPath $statePath)) { throw 'No temporary test environment was created.' }
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$expectedDll = Join-Path $stateDir 'server/CookieSheetServer.dll'
if ($state.ServerPid) {
    $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$($state.ServerPid)"
    if ($processInfo -and $processInfo.Name -eq 'dotnet.exe' -and $processInfo.CommandLine.Contains($expectedDll)) {
        Stop-Process -Id $state.ServerPid
    } elseif ($processInfo) { throw 'Stored PID now belongs to another process; it was not stopped.' }
}
$state.ServerPid = 0
$state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
$dockerHost = (& docker context inspect --format '{{.Endpoints.docker.Host}}').Trim()
if ($LASTEXITCODE -ne 0 -or $dockerHost -notlike 'npipe:*') { throw 'Only the local Windows Docker engine is supported.' }
$env:COOKIESHEET_TEST_DB_PASSWORD = $state.Password
& docker compose -f (Join-Path $projectRoot 'CookieSheetServer/compose.refund-test.yaml') stop
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the temporary database.' }
Write-Output 'Temporary API and database stopped. Test data is retained for the next start.'
