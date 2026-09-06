param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$Ip,
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

$serial = if ($Ip.Contains(":")) { $Ip } else { "$($Ip):5555" }

& $Adb connect $serial
& $Adb -s $serial wait-for-device

Write-Host "Tentando adb root (quando suportado pela ROM)..."
& $Adb -s $serial root
Start-Sleep -Milliseconds 700
& $Adb connect $serial
& $Adb -s $serial wait-for-device

Write-Host "Instalando FleetTridi Agent..."
& $Adb -s $serial install -r $AgentApk

Write-Host "Validando root persistente para operacoes remotas..."
$rootCheck = (& $Adb -s $serial shell su -c id 2>&1 | Out-String).Trim()
if ($rootCheck -notmatch "uid=0") {
  throw @"
O agente foi instalado, mas este equipamento nao possui su persistente acessivel ao app.
adb root nao equivale a root persistente para o FleetTridi Agent.

Sem isso, instalacao silenciosa de APK e reboot remoto nao serao confiaveis depois que o ADB sair da operacao.
Resultado: $rootCheck
"@
}

& $Adb -s $serial shell am broadcast -n com.tridi.fleet.agent/.ConfigReceiver -a com.tridi.fleet.agent.CONFIG --es server $Server --es deviceId $device.deviceId --es token $device.enrollmentToken --es name $Name --es city $City --es site $Site --es audiencePackage $AudiencePackage

Write-Host ""
Write-Host "Bootstrap concluido com root persistente validado."
Write-Host "Device ID: $($device.deviceId)"
Write-Host "Depois deste ponto o IP nao e usado para o controle remoto normal."
