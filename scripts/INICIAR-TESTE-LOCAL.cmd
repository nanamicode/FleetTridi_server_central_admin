@echo off
setlocal
cd /d "%~dp0"

if not exist "FleetTridi.Central.exe" (
  echo FleetTridi.Central.exe nao foi encontrado nesta pasta.
  echo Use este arquivo somente dentro do pacote FleetTridi-Windows.
  pause
  exit /b 1
)

start "" "%~dp0FleetTridi.Central.exe"
endlocal
