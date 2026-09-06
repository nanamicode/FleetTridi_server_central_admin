$root=Split-Path -Parent $PSScriptRoot
$folder="$root/dist/FleetTridi-Windows"
Start-Process "$folder/FleetTridi.Server.exe"
Start-Sleep -Seconds 2
Start-Process "$folder/FleetTridi.Admin.exe"
