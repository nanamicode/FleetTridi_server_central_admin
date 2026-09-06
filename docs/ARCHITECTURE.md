# Arquitetura FleetTridi v0.4

## Objetivo

Gerenciar N totens TridiAudience em uma ou várias cidades sem manter uma sessão ADB permanente e sem depender de IP fixo.

## Plano de controle

```
                    +--------------------+
FleetTridi.Admin -->| FleetTridi.Server  |<-- WSS -- Totem A
        HTTPS       |                    |<-- WSS -- Totem B
                    | releases + jobs    |<-- WSS -- Totem N
                    +--------------------+
```

O servidor é o único componente que precisa de endereço público. Os totens iniciam conexões de saída.

## Identidade

Cada dispositivo possui:

- `deviceId` permanente;
- token aleatório de 256 bits;
- nome;
- cidade;
- local/ponto;
- tags;
- canal de atualização;
- estado online;
- versão do agente;
- versão atual do TridiAudience;
- modo real de privilégio.

O token deixa de trafegar na query string no protocolo v0.4 e passa pelo header `X-Fleet-Device-Token`.

## Jobs

O agente implementa uma allowlist, não um shell remoto genérico:

- `installApk`;
- `pushFile`;
- `syncCreative`;
- `restartAudience`;
- `rebootDevice`;
- `captureScreen`;
- `tap`, `swipe`, `keyevent`.

Isso reduz o impacto de erro operacional e mantém o escopo de administração explícito.

## Releases

Um APK pode ser armazenado como uma release contendo:

- versão;
- package name;
- canal;
- notas;
- SHA-256;
- tamanho;
- data de criação.

O totem baixa o APK autenticado, calcula SHA-256 localmente e só tenta instalar se o hash for idêntico.

Depois da instalação, o agente consulta o PackageManager e devolve a versão realmente instalada.

## Rollouts

O servidor resolve os alvos por:

- lista de IDs;
- cidade;
- local;
- tag;
- canal;
- somente online;
- todos online.

O parâmetro `percent` permite canary/rollout gradual. `dryRun=true` retorna a lista que seria atingida sem criar jobs.

Rollback é um novo deploy de uma release anterior conhecida.

## Persistência

A v0.4 mantém arquivos JSON para facilitar o primeiro deployment:

- `data/devices.json`;
- `data/releases.json`;
- `data/audit.jsonl`;
- `data/uploads/`.

Para centenas/milhares de dispositivos, o próximo passo natural é PostgreSQL + object storage, preservando o protocolo do agente.

## Privilégio Android

O agente atual usa `su -c` para operações que exigem privilégio. `adb root` não concede automaticamente esse privilégio a um APK depois que ADB deixa de participar.

Por isso o agente reporta:

- `su-root`: `su -c id` retorna UID 0;
- `unprivileged`: root persistente não está disponível.

A instalação silenciosa deve ser considerada disponível somente no primeiro caso.

## Segurança de rede

Produção deve usar:

- HTTPS/WSS;
- senha administrativa por variável de ambiente;
- token de dispositivo individual;
- rotação de token em caso de comprometimento;
- ADB limitado à manutenção local;
- firewall sem exposição pública da porta 5555;
- backup do diretório de dados.
