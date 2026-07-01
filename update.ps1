# Pulls the latest code and rebuilds the downloadable .exe. Run this from PowerShell inside the
# repo root (the "outplay" folder you cloned) whenever you want to update to the latest version.
#
# Usage:
#   .\update.ps1
#
# If PowerShell blocks running local scripts, run this once first (in an admin PowerShell):
#   Set-ExecutionPolicy -Scope CurrentUser RemoteSigned

$ErrorActionPreference = "Stop"

Write-Host "Pulling latest changes..." -ForegroundColor Cyan
git pull

Write-Host "Publishing OutplayOverlay.exe (this can take a minute)..." -ForegroundColor Cyan
Push-Location "app/OutplayOverlay"
try {
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
}
finally {
    Pop-Location
}

$exePath = Join-Path (Get-Location) "app/OutplayOverlay/publish/OutplayOverlay.exe"
if (Test-Path $exePath) {
    Write-Host ""
    Write-Host "Done. Updated exe is at:" -ForegroundColor Green
    Write-Host "  $exePath"
}
else {
    Write-Host ""
    Write-Host "Publish finished, but OutplayOverlay.exe wasn't found where expected - check the output above for errors." -ForegroundColor Yellow
}
