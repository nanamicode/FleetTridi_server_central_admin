$ErrorActionPreference="Stop"
$root=Split-Path -Parent $PSScriptRoot
$central="$root/dist/FleetTridi-Windows/FleetTridi.Central.exe"

if (-not (Test-Path $central)) {
  throw "FleetTridi.Central.exe nao encontrado em dist/FleetTridi-Windows. Execute o build Windows primeiro."
}

Start-Process $central -WorkingDirectory (Split-Path -Parent $central)
Write-Host "FleetTridi.Central iniciado. A Central gerencia servidor, senha de sessao e Admin."
