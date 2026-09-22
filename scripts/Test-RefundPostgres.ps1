$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$stateDir = Join-Path $projectRoot '.local/refund-test'
$state = Get-Content -LiteralPath (Join-Path $stateDir 'environment.json') -Raw | ConvertFrom-Json
$env:COOKIESHEET_TEST_DATABASE = "Host=127.0.0.1;Port=$($state.DatabasePort);Database=$($state.Database);Username=cookiesheet_test;Password=$($state.Password)"
$env:COOKIESHEET_TEST_API = $state.ApiUrl
$env:COOKIESHEET_REFUND_SAMPLE = Join-Path $stateDir 'refund-sample.xlsx'
$env:COOKIESHEET_TEST_ORDERS = Join-Path $stateDir 'original-orders.json'
$env:COOKIESHEET_TEST_REPORT = Join-Path $stateDir 'test-result.json'
& dotnet test (Join-Path $projectRoot 'CookieSheetServer/CookieSheet.Refunds.Tests/CookieSheet.Refunds.Tests.csproj') --no-restore --disable-build-servers -m:1 -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Refund PostgreSQL tests failed.' }
Write-Output "Test report: $env:COOKIESHEET_TEST_REPORT"
