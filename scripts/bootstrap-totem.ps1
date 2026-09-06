param(
  [Parameter(Mandatory=$true)][string]$Server,
  [string]$AgentServer="",
  [string]$Ip="",
  [Parameter(Mandatory=$true)][string]$AgentApk,
  [string]$Name="Totem",
  [string]$City="",
  [string]$Site="",
  [string]$AudiencePackage="com.tridi.audience",
  [string]$AdminUser="nanamicode",
  [Parameter(Mandatory=$true)][string]$AdminPassword,
  [string]$Adb="adb"
)

$ErrorActionPreference="Stop"
$Server=$Server.TrimEnd('/')

if ([string]::IsNullOrWhiteSpace($AgentServer)) {
  $AgentServer=$Server
}
$AgentServer=$AgentServer.TrimEnd('/')

$agentUri=[Uri]$AgentServer
if ($agentUri.IsLoopback) {
  throw "AgentServer nao pode ser localhost/127.0.0.1. Informe o IP LAN do PC, por exemplo http://192.168.1.10:8787."
}

$login=Invoke-RestMethod -Method Post -Uri "$Server/api/login" -ContentType "application/json" -Body (@{
  username=$AdminUser
  password=$AdminPassword
} | ConvertTo-Json)

$headers=@{"X-Fleet-Token"=$login.token}
$device=Invoke-RestMethod -Method Post -Uri "$Server/api/devices" -Headers $headers -ContentType "application/json" -Body (@{
  name=$Name
  city=$City
  site=$Site
  initialIp=$Ip
  updateChannel="stable"
} | ConvertTo-Json)

$isNetwork=$false
$serial=$Ip.Trim()

if ([string]::IsNullOrWhiteSpace($serial)) {
  $found=@(& $Adb devices | Select-String "\tdevice$" | ForEach-Object { ($_ -split "\t")[0].Trim() })
  if ($found.Count -eq 0) { throw "Nenhum dispositivo ADB conectado." }
  if ($found.Count -gt 1) { throw "Mais de um dispositivo ADB conectado. Informe -Ip com o serial USB ou IP do totem." }
  $serial=$found[0]
}
elseif ($serial -match "^\d{1,3}(\.\d{1,3}){3}$") {
  $serial="$($serial):5555"
  $isNetwork=$true
}
elseif ($serial.Contains(":") -or $serial.Contains(".")) {
  $isNetwork=$true
}

if ($isNetwork) {
  & $Adb connect $serial
}

& $Adb -s $serial wait-for-device

Write-Host "Tentando adb root quando suportado pela ROM..."
& $Adb -s $serial root
Start-Sleep -Milliseconds 900

if ($isNetwork) {
  & $Adb connect $serial
}
& $Adb -s $serial wait-for-device

Write-Host "Reinstalando FleetTridi Agent de forma limpa..."
& $Adb -s $serial uninstall com.tridi.fleet.agent
& $Adb -s $serial install $AgentApk

Write-Host "Preflight: verificando su no shell ADB..."
$rootCheck = (& $Adb -s $serial shell su -c id 2>&1 | Out-String).Trim()
if ($rootCheck -notmatch "uid=0") {
  throw "su -c id falhou no shell ADB. Resultado: $rootCheck"
}

Write-Host "Enviando enrollment one-shot..."
& $Adb -s $serial shell am broadcast -n com.tridi.fleet.agent/.ConfigReceiver -a com.tridi.fleet.agent.CONFIG --es server $AgentServer --es deviceId $device.deviceId --es token $device.enrollmentToken --es name $Name --es city $City --es site $Site --es audiencePackage $AudiencePackage

Write-Host "Aguardando o proprio agente confirmar conexao e su-root..."
$confirmed=$null
for ($i=0; $i -lt 20; $i++) {
  Start-Sleep -Seconds 1
  $fleet=Invoke-RestMethod -Method Get -Uri "$Server/api/devices" -Headers $headers
  $confirmed=$fleet | Where-Object { $_.id -eq $device.deviceId } | Select-Object -First 1
  if ($confirmed -and $confirmed.online -and $confirmed.privilegeMode) { break }
}

if (-not $confirmed -or -not $confirmed.online) {
  throw "O agente nao ficou online em 20s. Verifique AgentServer, firewall e se a central esta ouvindo na LAN."
}

if ($confirmed.privilegeMode -ne "su-root") {
  throw "O totem conectou, mas o FleetTridi Agent reportou privilegeMode=$($confirmed.privilegeMode)."
}

Write-Host ""
Write-Host "Bootstrap validado."
Write-Host "Device ID: $($device.deviceId)"
Write-Host "Agente: $($confirmed.agentVersion)"
Write-Host "PrivilegeMode: $($confirmed.privilegeMode)"
Write-Host "A partir daqui o ADB nao e necessario para o controle normal."
