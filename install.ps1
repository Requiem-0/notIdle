#requires -Version 5.1
<#
    install.ps1 - build NotIdle into a real, standalone Windows app.

    After running this once you can close VS Code entirely: NotIdle runs from
    %LOCALAPPDATA%\NotIdle as its own process, unrelated to any terminal.

        .\install.ps1            # build + install + launch
        .\install.ps1 -NoLaunch  # build + install, don't start it now
        .\install.ps1 -Uninstall # stop it and remove the installed copy

    The app self-contains the .NET runtime, so the target PC needs nothing
    installed. Auto-start is toggled from inside the app ("Start with Windows").
#>
param(
    [switch]$NoLaunch,
    [switch]$Uninstall,
    [string]$DisguiseAs = "SystemAudioHost"
)

$ErrorActionPreference = 'Stop'
$AppName   = if ($DisguiseAs) { $DisguiseAs } else { 'SystemAudioHost' }
$Root      = $PSScriptRoot
$Project   = Join-Path $Root 'src\NotIdle.App\NotIdle.App.csproj'
$InstallDir = Join-Path $env:LOCALAPPDATA $AppName
$InstalledExe = Join-Path $InstallDir "$AppName.exe"
$StartMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$Shortcut  = Join-Path $StartMenu "$AppName.lnk"

function Stop-Running {
    @('NotIdle', 'SystemAudioHost', $AppName) | Select-Object -Unique | ForEach-Object {
        Get-Process $_ -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "Stopping running $($_.ProcessName) (PID $($_.Id))..."
            $_ | Stop-Process -Force
        }
    }
    Start-Sleep -Milliseconds 400
}

if ($Uninstall) {
    Stop-Running
    if (Test-Path $Shortcut)   { Remove-Item $Shortcut -Force }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    Write-Host "Uninstalled." -ForegroundColor Green
    return
}

Write-Host "Publishing self-contained single-file exe (this can take a minute)..." -ForegroundColor Cyan
$publishDir = Join-Path $Root 'artifacts\publish'
$publishLog = dotnet publish $Project -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:PublishReadyToRun=true `
    /p:EnableCompressionInSingleFile=true /p:DebugType=none `
    -o $publishDir 2>&1
if ($LASTEXITCODE -ne 0) {
    # Do not swallow the diagnostics: a bare "publish failed" is unactionable.
    $publishLog | ForEach-Object { Write-Host $_ }
    Write-Host ""
    Write-Host "Publish failed (exit $LASTEXITCODE)." -ForegroundColor Red
    Write-Host "If another build or 'dotnet test' is running against this repo, wait for it" -ForegroundColor Yellow
    Write-Host "to finish and re-run: concurrent MSBuild processes lock the same output." -ForegroundColor Yellow
    throw "publish failed"
}

Stop-Running  # release the installed exe if a previous copy is running

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $publishDir "NotIdle.exe") $InstalledExe -Force
Write-Host "Installed to $InstalledExe" -ForegroundColor Green

# Start Menu shortcut so it shows up in search like any installed app.
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($Shortcut)
$lnk.TargetPath = $InstalledExe
$lnk.WorkingDirectory = $InstallDir
$lnk.Description = if ($AppName -ne 'NotIdle') { "$AppName Service Host" } else { 'NotIdle - idle keep-awake utility' }
$lnk.Hotkey = "CTRL+ALT+D"
$lnk.Save()
Write-Host "Start Menu shortcut created (Shortcut key: Ctrl+Alt+D)."

if (-not $NoLaunch) {
    Start-Process $InstalledExe
    Write-Host "Launched. Look for the tray icon (near the clock)." -ForegroundColor Green
    Write-Host "You can now close VS Code; NotIdle keeps running on its own."
}
