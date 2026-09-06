@echo off
setlocal
cd /d "%~dp0"

set FLEETTRIDI_DEV_MODE=1
set FLEETTRIDI_URLS=http://0.0.0.0:8787

echo ==========================================
echo FleetTridi - TESTE LOCAL
echo ==========================================
echo.
echo Servidor Admin: http://localhost:8787
echo Usuario: nanamicode
echo Senha: fleettridi-local
echo.
echo O servidor ouvira na rede local para que a TV box consiga conectar.
echo Se o Windows Firewall perguntar, permita somente em redes privadas.
echo.

start "FleetTridi Server" "%~dp0FleetTridi.Server.exe"
timeout /t 2 /nobreak >nul
start "FleetTridi Admin" "%~dp0FleetTridi.Admin.exe"

echo FleetTridi iniciado.
echo No Admin, clique "Detectar IP LAN" antes do Bootstrap.
echo.
pause
endlocal
