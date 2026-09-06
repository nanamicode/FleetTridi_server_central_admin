# FleetTridi v0.5.1 — primeiro teste físico

## O que baixar

Na aba **Actions**, baixe e extraia o artefato **FleetTridi-Windows** do build v0.5.1.

Ele contém:

- `FleetTridi.Central.exe`;
- `FleetTridi.Server.exe`;
- `FleetTridi.Admin.exe`;
- `FleetTridi-Agent.apk` (DEBUG, somente para o primeiro teste);
- Android `platform-tools/adb.exe`.

## Abrir a central

Execute:

`FleetTridi.Central.exe`

A Central:

1. cria uma senha administrativa aleatória para a sessão;
2. inicia o servidor em `0.0.0.0:8787`;
3. abre o Admin conectado localmente por `127.0.0.1:8787`;
4. detecta uma URL IPv4 LAN para o totem.

Se o Windows Firewall perguntar, permita o FleetTridi somente em **redes privadas**.

## Provisionar por USB

1. Ative a depuração USB na TV box.
2. Conecte apenas a box que será testada ao PC.
3. No Admin, confira **URL vista pelos totens**. Ela deve ser algo como `http://192.168.x.x:8787`, nunca localhost.
4. Clique **+ Adicionar totem**.
5. Informe nome, cidade e local.
6. Deixe **ADB inicial** vazio para autodetectar o único dispositivo USB.
7. Selecione o totem e clique **Bootstrap via ADB**.
8. Escolha `FleetTridi-Agent.apk` do pacote Windows.

O bootstrap reinstala apenas o FleetTridi Agent, faz o enrollment e espera a confirmação do próprio APK.

## Critério de sucesso do bootstrap

Só prossiga quando o Admin mostrar:

- totem **online**;
- Agent **0.4.1**;
- privilégio **su-root**.

Depois disso desconecte o USB. O primeiro teste relevante é confirmar que o totem continua online sem ADB.

## Primeiro teste de atualização

1. Cadastre uma release do TridiAudience com o **package name** mostrado pelo próprio totem.
2. Informe exatamente o `versionName` do APK.
3. Faça **Simular rollout** no único totem.
4. Confira que ele aparece como alvo e não como `skippedNoRoot`.
5. Publique o rollout.
6. O Agent baixa o APK autenticado, confere SHA-256, instala, confere a versão no PackageManager e reinicia o TridiAudience.
7. Confirme no painel que a nova versão passou a ser reportada.

## Rollout posterior

Os percentuais são cobertura total determinística. Portanto 10% -> 25% instala apenas a diferença até chegar a 25%, sem reinstalar os primeiros 10%.

Totens sem `su-root` e dispositivos que já estejam na versão-alvo são excluídos automaticamente do novo job.

## APK do FleetTridi Agent

O `FleetTridi-Agent.apk` dentro do pacote de teste é DEBUG. Builds DEBUG de ambientes diferentes podem usar certificados diferentes.

Para implantação permanente, configure os secrets de assinatura do workflow e use sempre o **FleetTridi-Agent-RELEASE-ASSINADO** gerado com a mesma chave.

## Produção entre cidades

Use uma URL pública HTTPS/WSS e defina:

- `FLEETTRIDI_ADMIN_PASSWORD`;
- `FLEETTRIDI_PUBLIC_URL`;
- opcionalmente `FLEETTRIDI_MAX_UPLOAD_MB`.

Não exponha ADB/5555 na Internet.
