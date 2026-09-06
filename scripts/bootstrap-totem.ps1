param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$Ip,
  [Parameter(Mandatory=$true)][string]$AgentApk,
  [string]$Name="Totem",
  [string]$City="",
  [string]$Site="",
  [string]$AdminUser="nanamicode",
  [string]$AdminPassword="veralucia12",
  [string]$Adb="adb"
)
$ErrorActionPreference="Stop"
$Server=$Server.TrimEnd('/')
$login=Invoke-RestMethod -Method Post -Uri "$Server/api/login" -ContentType "application/json" -Body (@{username=$AdminUser; password=$AdminPassword} | ConvertTo-Json)
$headers=@{"X-Fleet-Token"=$login.token}
$device=Invoke-RestMethod -Method Post -Uri "$Server/api/devices" -Headers $headers -ContentType "application/json" -Body (@{name=$Name; city=$City; site=$Site; initialIp=$Ip} | ConvertTo-Json)
$serial = if ($Ip.Contains(":")) { $Ip } else { "$($Ip):5555" }
& $Adb connect $serial
& $Adb -s $serial wait-for-device
& $Adb -s $serial root
Start-Sleep -Milliseconds 700
& $Adb connect $serial
& $Adb -s $serial install -r $AgentApk
& $Adb -s $serial shell settings put global adb_enabled 1
& $Adb -s $serial shell su -c "setprop persist.sys.usb.config adb"
& $Adb -s $serial shell am broadcast -n com.tridi.fleet.agent/.ConfigReceiver -a com.tridi.fleet.agent.CONFIG --es server $Server --es deviceId $device.deviceId --es token $device.enrollmentToken --es name $Name --es city $City --es site $Site
Write-Host "Bootstrap concluido. Device ID: $($device.deviceId)"
Write-Host "O IP nao sera mais necessario para o controle remoto normal."
