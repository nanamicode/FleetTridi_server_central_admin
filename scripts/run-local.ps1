$root=Split-Path -Parent $PSScriptRoot
Start-Process powershell -ArgumentList "-NoExit","-Command","& '$root\dist\server\FleetTridi.Server.exe'"
Start-Sleep -Seconds 2
Start-Process "$root\dist\admin\FleetTridi.Admin.exe"