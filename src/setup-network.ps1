# Libera o Stardew Web API para a rede local (rodar UMA vez, como administrador).
# Clique com o botao direito > "Executar com o PowerShell" (vai pedir permissao de admin).

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

netsh http delete urlacl url=http://+:7882/ | Out-Null
netsh http add urlacl url=http://+:7882/ "sddl=D:(A;;GX;;;WD)"

netsh advfirewall firewall delete rule name=StardewWebApi | Out-Null
netsh advfirewall firewall add rule name=StardewWebApi dir=in action=allow protocol=TCP localport=7882 profile=any
netsh advfirewall firewall add rule name=StardewWebApi dir=in action=allow protocol=UDP localport=5353 profile=any

Write-Host ""
Write-Host "Pronto! Abra o jogo pelo SMAPI." -ForegroundColor Green
Start-Sleep -Seconds 4
