$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'release\Shitie.exe'
if (-not (Test-Path -LiteralPath $package)) { throw '请先运行 build.ps1 生成 release\Shitie.exe。' }
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\Shitie'
$executable = Join-Path $installRoot 'Shitie.exe'
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
if (Test-Path -LiteralPath $executable) {
    Start-Process -FilePath $executable -ArgumentList '--quit' -WindowStyle Hidden -Wait
}
for ($attempt = 0; $attempt -lt 15; $attempt++) {
    try { Copy-Item -LiteralPath $package -Destination $executable -Force; break }
    catch { if ($attempt -eq 14) { throw }; Start-Sleep -Milliseconds 200 }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $installRoot '使用说明.md') -Force
$shortcutShell = New-Object -ComObject WScript.Shell
$destinations = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))
foreach ($destination in $destinations) {
    $shortcut = $shortcutShell.CreateShortcut((Join-Path $destination '拾贴剪切板.lnk'))
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.Description = '拾贴 · 本地剪切板历史与复制成功音效'
    $shortcut.IconLocation = "$executable,0"
    $shortcut.Save()
}
Start-Process -FilePath $executable
Write-Output "安装完成：$executable"
Write-Output '桌面已创建“拾贴剪切板”快捷方式。'
