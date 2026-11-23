# Builds TextSnag for Windows and packages it for a GitHub release.
#
#   windows\build.ps1             build, then make dist\TextSnag-Windows.zip
#   windows\build.ps1 -Install    also copy it to %LOCALAPPDATA%\Programs\TextSnag, add a Start menu
#                                 shortcut, and launch it
#
# Signing (optional, avoids the SmartScreen "unrecognized app" warning): set SIGN_CERT to a .pfx
# path and SIGN_PASSWORD to its password; signtool.exe from the Windows SDK must be on PATH.
param([switch]$Install, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipBuild) {
    dotnet build windows/TextSnag -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$out = 'windows/TextSnag/bin/Release/net48'

if ($env:SIGN_CERT) {
    signtool sign /f $env:SIGN_CERT /p $env:SIGN_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "$out/TextSnag.exe"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$stage = 'dist/TextSnag-Windows'
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item "$out/TextSnag.exe" $stage
Copy-Item "$out/TextSnag.exe.config" $stage -ErrorAction SilentlyContinue
Copy-Item "$out/*.dll" $stage -Exclude zxing.presentation.dll # (ZXing's WPF add-on, unused)
Copy-Item LICENSE $stage
Remove-Item -Force dist/TextSnag-Windows.zip -ErrorAction SilentlyContinue
Compress-Archive -Path $stage -DestinationPath dist/TextSnag-Windows.zip
Write-Host "Release zip: dist/TextSnag-Windows.zip"

if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA 'Programs/TextSnag'
    Get-Process TextSnag -ErrorAction SilentlyContinue | Stop-Process -Force
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item "$stage/*" $target -Force
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'TextSnag.lnk'))
    $link.TargetPath = Join-Path $target 'TextSnag.exe'
    $link.Save()
    Start-Process (Join-Path $target 'TextSnag.exe')
    Write-Host "Installed to $target"
}
