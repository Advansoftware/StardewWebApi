# Stardew Web API 0.7.1 (fork do Guia do Vale)

Esta pasta é um mod de SMAPI. Ela não funciona sozinha.

## Requisitos

- **Stardew Valley 1.6** no PC.
- **SMAPI** instalado (testado com a 4.5.2): https://github.com/Pathoschild/SMAPI/releases/latest
- Guia oficial de instalação do SMAPI: https://smapi.io/

## Instalação

1. Instale o SMAPI conforme o guia oficial.
2. Extraia a pasta `StardewWebApi` inteira em:
   - Windows: `C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\Mods\`
   - Linux: `~/.local/share/Steam/steamapps/common/Stardew Valley/Mods/`
   - macOS: `~/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS/Mods/`

### Configuração extra no Windows (obrigatória uma única vez)

O mod precisa escutar na rede local. Para isso, abra o **PowerShell como Administrador**
e execute os dois comandos abaixo:

```powershell
# 1. Permite que o HttpListener do mod use a porta 7882 em todas as interfaces
netsh http add urlacl url=http://+:7882/ sddl=D:(A;;GX;;;WD)

# 2. Libera a porta 7882 no Firewall do Windows
netsh advfirewall firewall add rule `
  name="Stardew Web API" `
  dir=in `
  action=allow `
  protocol=TCP `
  localport=7882
```

Esses comandos só precisam ser executados uma vez. Após isso, o mod funciona
automaticamente sem precisar de privilégios de administrador.

## Testando

3. Abra o jogo pelo SMAPI e **carregue um save**.
4. Teste no PC: `http://localhost:7882/api/v1/mods`
5. Teste de outro aparelho na mesma rede: `http://IP_DO_PC:7882/api/v1/mods`

A API escuta em `http://+:7882/` (localhost e rede local na mesma porta).
O anúncio `_stardewapi._tcp` permite que o app Guia do Vale encontre o servidor sozinho.

## Observações

- Os endpoints de mundo, jogador e inventário só respondem com um save carregado.
- Sem save, use `/api/v1/mods` para confirmar que o servidor está de pé.
- O tráfego é HTTP local, sem TLS. Use apenas em rede confiável.

Créditos: projeto original de zunderscore (MIT), fork com descoberta local por Advansoftware.
