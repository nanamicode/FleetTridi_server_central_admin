# FleetTridi v0.5 — início rápido

## Teste em um único PC

Baixe o artefato **FleetTridi-Windows** da aba Actions e extraia a pasta.

Abra:

`FleetTridi.Central.exe`

Esse executável:

1. inicia `FleetTridi.Server.exe` em `127.0.0.1:8787`;
2. usa um diretório `data/` ao lado dos executáveis;
3. abre o painel `FleetTridi.Admin.exe`;
4. preenche e autentica automaticamente o login local.

Credenciais do modo local:

- usuário: `nanamicode`
- senha: `veralucia12`

O modo local fica preso ao próprio PC e serve para provisionamento e desenvolvimento.

## Provisionar o primeiro totem

O pacote Windows já contém:

- `FleetTridi-Agent.apk`;
- `platform-tools/adb.exe`.

No painel:

1. clique **+ Adicionar totem**;
2. informe nome, cidade, local e o IP inicial;
3. selecione o totem;
4. clique **Bootstrap via ADB**;
5. escolha `FleetTridi-Agent.apk` da mesma pasta;
6. aguarde a validação de root persistente e o enrollment.

Depois do enrollment, o FleetTridi Agent inicia no boot e abre uma conexão WebSocket de saída. O IP inicial não é mais usado no controle normal.

## Entre cidades e redes diferentes

O agente já funciona atrás de NAT/CGNAT porque a conexão parte do totem para a central.

O único componente que precisa ser alcançável publicamente é o **FleetTridi.Server**.

Em uma máquina própria com endereço público ou encaminhamento de porta:

```powershell
$env:FLEETTRIDI_ADMIN_USER="nanamicode"
$env:FLEETTRIDI_ADMIN_PASSWORD="<senha-da-central>"
$env:FLEETTRIDI_PUBLIC_URL="https://fleet.seudominio.com"
$env:FLEETTRIDI_URLS="http://0.0.0.0:8787"
./FleetTridi.Server.exe
```

Use HTTPS/WSS na frente do servidor. Não exponha ADB/5555 na Internet.

## Atualização do TridiAudience

O fluxo recomendado continua sendo:

1. cadastrar o APK como release;
2. simular o rollout;
3. atualizar poucos totens;
4. conferir versão/telemetria;
5. aumentar gradualmente até 100%.

O APK é armazenado uma vez na central e distribuído aos dispositivos selecionados.
