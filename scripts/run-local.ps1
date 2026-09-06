$ErrorActionPreference="Stop"
$root=Split-Path -Parent $PSScriptRoot
$folder="$root/dist/FleetTridi-Windows"

$env:FLEETTRIDI_DEV_MODE="1"
$env:FLEETTRIDI_URLS="http://0.0.0.0:8787"

Start-Process "$folder/FleetTridi.Server.exe" -WorkingDirectory $folder
Start-Sleep -Seconds 2
Start-Process "$folder/FleetTridi.Admin.exe" -WorkingDirectory $folder

Write-Host "FleetTridi local iniciado."
Write-Host "Admin: http://localhost:8787"
Write-Host "Usuario: nanamicode"
Write-Host "Senha local: fleettridi-local"
Write-Host "O servidor esta ouvindo na LAN para o primeiro teste com o totem."
