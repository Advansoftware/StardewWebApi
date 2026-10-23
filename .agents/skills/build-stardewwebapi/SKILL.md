---
name: build-stardewwebapi
description: >
  Como buildar, deployar e publicar o mod StardewWebApi (SMAPI) corretamente,
  incluindo pre-requisitos, comandos de build, deploy local, rede local (LAN/mDNS)
  e release no GitHub.
---

# Build, Deploy e Release - StardewWebApi

## Contexto

Mod SMAPI para Stardew Valley. Build com .NET 6 SDK + pacote
Pathoschild.Stardew.ModBuildConfig, que copia os arquivos para a pasta de mods
e gera o .zip de release automaticamente.

Repositorio: Advansoftware/StardewWebApi (fork de zunderscore/StardewWebApi)
Remote: https  (gh ja autenticado via `gh auth login`, escopo repo)

REGRA FUNDAMENTAL: o pacote do mod deve conter APENAS:

    StardewWebApi/
      manifest.json
      StardewWebApi.dll
      INSTALL.md          (instrucoes para o usuario)
      setup-network.ps1   (libera a rede local, roda uma vez como admin)

NENHUMA DLL de terceiros. O SMAPI reescreve todos os assemblies da pasta via
Mono.Cecil e falha se alguma dependencia nao resolver.

---

## Pre-requisitos

| Ferramenta             | Versao    | Instalacao                             |
|------------------------|-----------|----------------------------------------|
| .NET SDK               | 6.0.x     | winget install Microsoft.DotNet.SDK.6  |
| GitHub CLI (gh)        | qualquer  | winget install GitHub.cli              |
| Stardew Valley + SMAPI | instalados| Steam + SMAPI 4.x                      |

PATH (os executaveis nao estao no PATH do shell do agente):

    $env:PATH = "C:\Program Files\dotnet;C:\Program Files\GitHub CLI;" + $env:PATH

Caminho do jogo: C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley

ExecutionPolicy da maquina bloqueia scripts .ps1: rode com
`powershell -ExecutionPolicy Bypass -File script.ps1`.

---

## .csproj correto

    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <TargetFramework>net6.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <Version>X.Y.Z</Version>
      </PropertyGroup>
      <ItemGroup>
        <None Update="INSTALL.md;setup-network.ps1" CopyToOutputDirectory="PreserveNewest" />
      </ItemGroup>
      <ItemGroup>
        <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="4.1.1" />
      </ItemGroup>
    </Project>

NAO adicionar:
  - BundleExtraAssemblies (inclui DLLs de terceiros que quebram o SMAPI)
  - EnableModZip=false (impede o zip)
  - Targets customizados de empacotamento
  - Pacotes com dependencias nativas (ex.: Makaretu.Dns.Multicast -> Tmds.LibC)

---

## Rede local (LAN) - como funciona e armadilhas

### HTTP (porta 7882)
- `HttpListener` escuta em `http://+:7882/` (todas as interfaces).
- NAO usar TcpListener/forwarder na mesma porta: o HTTP.sys reserva a porta em
  todas as interfaces e o TcpListener recebe WSAEACCES
  ("tentativa de acesso a um soquete... proibida").
- Requer URL reservation (uma vez, admin). Use SDDL, NAO `user=Everyone`
  (no Windows PT-BR o grupo se chama "Todos" e o comando falha):

      netsh http add urlacl url=http://+:7882/ "sddl=D:(A;;GX;;;WD)"

- Sem a urlacl o WebServer cai para `http://localhost:7882/` e loga um aviso.

### mDNS (descoberta pelo app, UDP 5353)
Implementado nativamente em src/Server/MdnsAdvertiser.cs (sem libs). Regras
que, se quebradas, fazem o celular NAO achar o servidor:
  1. Socket bound em 0.0.0.0:5353 com ReuseAddress (compartilha com o mDNS
     do Windows). Respostas de outra porta sao IGNORADAS (RFC 6762 §6).
  2. Precisa RESPONDER queries (PTR _stardewapi._tcp.local.), nao so anunciar.
  3. PTR e registro compartilhado: SEM bit cache-flush. SRV/TXT/A: com.
  4. Query vinda de porta != 5353 (legada) -> resposta unicast, mesmo ID,
     sem cache-flush, TTL <= 10.
  5. Registro A usa o IP da interface na mesma sub-rede de quem perguntou.

### Firewall
setup-network.ps1 cria a urlacl + regras de entrada TCP 7882 e UDP 5353
(profile=any - a rede do usuario e "Publica").

### Diagnostico rapido
    netstat -ano | Select-String ":7882|:5353 "      # 7882 PID 4 (HTTP.sys); 5353 deve incluir o PID do StardewModdingAPI
    Invoke-WebRequest http://<IP-LAN>:7882/ -UseBasicParsing   # 404 = rede OK
    netsh http show urlacl url=http://+:7882/
    Get-NetFirewallRule -DisplayName StardewWebApi

---

## Build

### Build + deploy local (jogo FECHADO - senao a DLL fica travada)

    dotnet build src\StardewWebApi.csproj -c Release /p:EnableModDeploy=true

### Build so do zip (jogo aberto)

    dotnet build src\StardewWebApi.csproj -c Release /p:EnableModDeploy=false

Zip gerado em: src\bin\Release\net6.0\StardewWebApi <versao>.zip

Conferir conteudo do zip:

    Add-Type -A System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::OpenRead("$PWD\src\bin\Release\net6.0\StardewWebApi X.Y.Z.zip").Entries.FullName

---

## Release no GitHub

Convencao de tag: SEM "v" (ex.: `0.7.1`), igual ao upstream (`0.6.0`).
Nunca reaproveitar uma versao ja publicada: incremente o patch.

Versao aparece em 4 lugares - atualize TODOS:
  - src/StardewWebApi.csproj        <Version>
  - src/manifest.json               "Version"
  - src/Server/MdnsAdvertiser.cs    TXT "version=..."
  - src/INSTALL.md                  titulo

Fluxo:

    $env:PATH = "C:\Program Files\dotnet;C:\Program Files\GitHub CLI;" + $env:PATH
    $v = "X.Y.Z"

    Remove-Item src\bin\Release\net6.0\*.zip -ErrorAction SilentlyContinue
    dotnet build src\StardewWebApi.csproj -c Release /p:EnableModDeploy=false

    git add -A
    git commit -m "Release $v: <resumo>"
    git push origin main
    git tag -a $v -m "Stardew Web API $v"
    git push origin $v

    gh release create $v "src\bin\Release\net6.0\StardewWebApi $v.zip" `
      --repo Advansoftware/StardewWebApi `
      --title "Stardew Web API $v" `
      --notes-file <arquivo-notas.md> --latest

---

## Checklist

  [ ] Versao atualizada nos 4 lugares
  [ ] Build sem erros
  [ ] Zip contem so manifest.json, StardewWebApi.dll, INSTALL.md, setup-network.ps1
  [ ] SMAPI carrega o mod sem erros
  [ ] Console mostra "API escutando em http://+:7882/" e "Anuncio mDNS ativo"
  [ ] Celular encontra o servidor
  [ ] Commit, push, tag e release com o zip

---

## Troubleshooting

| Sintoma | Causa / Fix |
|---|---|
| "DLL couldn't be loaded" / "Rewriting X.dll failed" | DLL de terceiro no pacote. Ver %APPDATA%\StardewValley\ErrorLogs\SMAPI-latest.txt |
| "acesso a um soquete... proibida" na 7882 | TcpListener disputando porta com HTTP.sys. Use so HttpListener em http://+:7882/ |
| "Sem permissao para escutar na rede" | Falta urlacl -> rodar setup-network.ps1 |
| Celular nao acha o servidor, mas http://IP:7882 responde | mDNS: ver regras 1-5 acima; firewall UDP 5353 |
| Deploy falha "file is being used" | Jogo aberto; feche ou use EnableModDeploy=false |
| netsh urlacl com user=Everyone falha | Windows PT-BR; use sddl=D:(A;;GX;;;WD) |
