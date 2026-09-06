$ErrorActionPreference="Stop"
$root=Split-Path -Parent $PSScriptRoot
$out="$root/dist/FleetTridi-Windows"

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null

$publishArgs=@(
  "-c","Release",
  "-r","win-x64",
  "--self-contained","true",
  "-p:PublishSingleFile=true",
  "-p:IncludeNativeLibrariesForSelfExtract=true",
  "-p:DebugType=None",
  "-p:DebugSymbols=false",
  "-o",$out
)

dotnet publish "$root/src/FleetTridi.Admin/FleetTridi.Admin.csproj" @publishArgs
dotnet publish "$root/src/FleetTridi.Server/FleetTridi.Server.csproj" @publishArgs
dotnet publish "$root/src/FleetTridi.Central/FleetTridi.Central.csproj" @publishArgs

Write-Host "Pronto em: $out"
Write-Host "Abra FleetTridi.Central.exe para iniciar servidor local + painel."
