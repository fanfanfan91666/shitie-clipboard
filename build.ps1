$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$project = Join-Path $taskRoot 'Shitie\Shitie.csproj'
$releaseRoot = Join-Path $taskRoot 'release'
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $releaseRoot --nologo
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $releaseRoot '使用说明.md') -Force
$packageRoot = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
$zipPath = Join-Path $packageRoot '拾贴剪切板-v1.0.2-win-x64.zip'
Compress-Archive -LiteralPath @((Join-Path $releaseRoot 'Shitie.exe'), (Join-Path $releaseRoot '使用说明.md')) -DestinationPath $zipPath -Force
Write-Output "已生成：$releaseRoot\Shitie.exe"
Write-Output "已打包：$zipPath"
