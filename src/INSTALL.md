# Stardew Web API 0.7.0 (fork do Guia do Vale)

Esta pasta é um mod de SMAPI. Ela não funciona sozinha.

## Requisitos

- **Stardew Valley 1.6** no PC.
- **SMAPI 4.5.2** instalado: https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2
- Guia oficial de instalação do SMAPI: https://smapi.io/

## Instalação

1. Instale o SMAPI conforme o guia oficial.
2. Extraia a pasta `StardewWebApi` inteira em:
   - Windows: `C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\Mods\`
   - Linux: `~/.local/share/Steam/steamapps/common/Stardew Valley/Mods/`
   - macOS: `~/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS/Mods/`
3. Abra o jogo pelo SMAPI e **carregue um save**.
4. No Windows, permita o acesso do jogo em redes privadas se o firewall perguntar.
5. Teste no PC: `http://localhost:7882/api/v1/mods`
6. Teste de outro aparelho na mesma rede: `http://IP_DO_PC:7882/api/v1/mods`

A API escuta em `localhost:7882` e também aceita conexões da rede local na porta 7882.
O anúncio `_stardewapi._tcp` permite que o app Guia do Vale encontre o servidor sozinho.

## Observações

- Os endpoints de mundo, jogador e inventário só respondem com um save carregado.
- Sem save, use `/api/v1/mods` para confirmar que o servidor está de pé.
- O tráfego é HTTP local, sem TLS. Use apenas em rede confiável.

Créditos: projeto original de zunderscore (MIT), fork com descoberta local por Advansoftware.
