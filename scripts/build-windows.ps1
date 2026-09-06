$ErrorActionPreference="Stop"
$root=Split-Path -Parent $PSScriptRoot
$out="$root/dist/FleetTridi-Windows"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null
dotnet publish "$root/src/FleetTridi.Admin/FleetTridi.Admin.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
dotnet publish "$root/src/FleetTridi.Server/FleetTridi.Server.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
Write-Host "Pronto em: $out"
