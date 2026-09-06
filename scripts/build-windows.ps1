$ErrorActionPreference="Stop"
$root=Split-Path -Parent $PSScriptRoot
dotnet publish "$root/src/FleetTridi.Admin/FleetTridi.Admin.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root/dist/admin"
dotnet publish "$root/src/FleetTridi.Server/FleetTridi.Server.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root/dist/server"
Write-Host "Pronto: dist/admin/FleetTridi.Admin.exe e dist/server/FleetTridi.Server.exe"