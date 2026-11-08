param([switch]$Clipboard, [switch]$Scroll)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Shitie\Shitie.csproj'
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
$reportRoot = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $reportRoot | Out-Null
$report = Join-Path $reportRoot 'test-results.txt'
$testArguments = @('--self-test', '--report', ('"' + $report + '"'))
if ($Clipboard) { $testArguments += '--clipboard' }
if ($Scroll) { $testArguments += '--scroll' }
$testRun = Start-Process -FilePath (Join-Path $PSScriptRoot 'Shitie\bin\Release\net8.0-windows\Shitie.exe') -ArgumentList $testArguments -WindowStyle Hidden -PassThru -Wait
Get-Content -LiteralPath $report
if ($testRun.ExitCode -ne 0) { throw '存在未通过的测试，请查看 artifacts\test-results.txt。' }
