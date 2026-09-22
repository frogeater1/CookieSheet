param([switch]$OpenClient)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$stateDir = Join-Path $projectRoot '.local/refund-test'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
$statePath = Join-Path $stateDir 'environment.json'
$dockerHost = (& docker context inspect --format '{{.Endpoints.docker.Host}}').Trim()
if ($LASTEXITCODE -ne 0 -or $dockerHost -notlike 'npipe:*') { throw 'Test database requires the local Windows Docker engine.' }
if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
} else {
    $state = [pscustomobject]@{
        Password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        ApiUrl = 'http://127.0.0.1:51873'
        Database = 'cookiesheet_refund_test'
        DatabasePort = 55439
        ServerPid = 0
    }
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
}
$env:COOKIESHEET_TEST_DB_PASSWORD = $state.Password
& docker compose -f (Join-Path $projectRoot 'CookieSheetServer/compose.refund-test.yaml') up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'Could not start the temporary database.' }
$serverProject = Join-Path $projectRoot 'CookieSheetServer/CookieSheetServer/CookieSheetServer.csproj'
$serverDir = Join-Path $stateDir 'server'
$serverDll = Join-Path $serverDir 'CookieSheetServer.dll'
$serverProcess = if ($state.ServerPid) { Get-Process -Id $state.ServerPid -ErrorAction SilentlyContinue } else { $null }
$ready = $false
if ($serverProcess -and $serverProcess.ProcessName -eq 'dotnet') {
    try { $null = Invoke-RestMethod "$($state.ApiUrl)/api/refunds" -TimeoutSec 2; $ready = $true } catch { }
}
if (-not $ready) {
    & dotnet publish $serverProject --no-restore --disable-build-servers -m:1 -c Debug -o $serverDir -v:quiet -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
    $env:DATABASE = "Host=127.0.0.1;Port=$($state.DatabasePort);Database=$($state.Database);Username=cookiesheet_test;Password=$($state.Password)"
    $env:ASPNETCORE_URLS = $state.ApiUrl
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $p = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @($serverDll) -WorkingDirectory (Split-Path $serverProject) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $stateDir 'server.log') -RedirectStandardError (Join-Path $stateDir 'server-error.log')
    $state.ServerPid = $p.Id
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($p.HasExited) { throw "Test server exited. See $stateDir/server.log and server-error.log." }
        try { $null = Invoke-RestMethod "$($state.ApiUrl)/api/refunds" -TimeoutSec 2; $ready = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'Temporary API did not become ready.' }
}
Write-Output "Temporary PostgreSQL: 127.0.0.1:$($state.DatabasePort) / $($state.Database)"
Write-Output "Temporary API: $($state.ApiUrl)"
Write-Output "Local state and credentials: $statePath"
if ($OpenClient) {
    $clientPath = Join-Path $projectRoot 'CookieSheet/CookieSheet/bin/refunds-preview/CookieSheet.exe'
    if (-not (Test-Path -LiteralPath $clientPath)) { throw 'Build the refunds-preview client first.' }
    $env:COOKIESHEET_API_URL = $state.ApiUrl
    Start-Process -FilePath $clientPath -WorkingDirectory (Split-Path $clientPath) -WindowStyle Hidden | Out-Null
    Write-Output 'Started client connected only to the temporary API.'
}
