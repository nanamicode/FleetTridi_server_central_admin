# FleetTridi

Central de gerenciamento remoto da frota TridiAudience.

## Estado atual — v0.5 plug-and-play

A v0.5 preserva o protocolo de gerenciamento validado na v0.4 e acrescenta uma camada de uso simples para Windows.

### Um único ponto de entrada no Windows

O pacote **FleetTridi-Windows** agora inclui:

- `FleetTridi.Central.exe` — executável que o operador abre;
- `FleetTridi.Server.exe` — servidor central;
- `FleetTridi.Admin.exe` — painel WPF;
- `FleetTridi-Agent.apk` — agente Android;
- Android `platform-tools` / ADB.

Ao abrir `FleetTridi.Central.exe` no modo local, ele inicia o servidor, abre o painel e faz o login automaticamente.

Credenciais locais de desenvolvimento:

- usuário: `nanamicode`
- senha: `veralucia12`

O servidor local fica em `127.0.0.1`, portanto essas credenciais de conveniência não são usadas como senha padrão de produção.

## Arquitetura

A solução foi desenhada para crescer de uma cidade pequena para múltiplas cidades sem depender de IP fixo nem de ADB exposto na Internet.

```text
FleetTridi.Admin -> HTTPS -> FleetTridi.Server <- WSS <- FleetTridi Agent
```

Cada totem abre uma conexão **de saída** até a central. O IP é necessário somente no provisionamento inicial.

### Componentes

- **FleetTridi.Server (.NET 8):** identidade, releases, jobs, auditoria e distribuição de arquivos.
- **FleetTridi.Admin (Windows/WPF):** cadastro, bootstrap ADB, controle remoto, manutenção e ações em massa.
- **FleetTridi.Central (Windows):** inicializador plug-and-play do ambiente local.
- **FleetTridi Agent (Android 9+):** inicia no boot e executa a allowlist de operações de frota.
- **GitHub Actions:** gera o pacote Windows completo e o APK isolado.

## O que já funciona

- cadastro de N totens por `deviceId`;
- cidade, ponto/local, tags e canal de atualização;
- bootstrap inicial por ADB;
- conexão persistente de saída via WebSocket;
- reconexão automática e inicialização no boot;
- online/offline, último IP e telemetria;
- detecção do modo real de privilégio;
- instalação/atualização remota de APK;
- catálogo permanente de versões do TridiAudience;
- SHA-256 calculado pelo servidor e validado no totem;
- confirmação da versão instalada;
- rollout por IDs, cidade, local, tag ou canal;
- rollout percentual / canary;
- `dryRun` antes de atualizar;
- atualização em massa;
- reentrega de jobs após reconexão;
- reinício do TridiAudience e do Android;
- envio de arquivos apenas para áreas permitidas;
- screenshot e controle básico de entrada;
- rotação do token individual do dispositivo;
- log de auditoria;
- Docker para o servidor;
- pacote Windows all-in-one.

## Root Android

`adb root` eleva o processo `adbd` quando a ROM permite, mas não garante que um APK terá root depois que a sessão ADB acabar.

Para instalação silenciosa e reboot remotos, o agente atual exige `su` persistente. O bootstrap valida isso explicitamente.

## Operação entre redes/cidades

O totem não precisa ter IP público. O agente funciona atrás de NAT/CGNAT porque inicia a conexão.

Somente o FleetTridi.Server precisa ter uma URL alcançável, idealmente HTTPS.

Não exponha a porta ADB 5555 publicamente.

## Produção

Defina:

- `FLEETTRIDI_ADMIN_PASSWORD`
- `FLEETTRIDI_PUBLIC_URL=https://fleet.seudominio.com`

Use TLS/HTTPS na frente do servidor.

## Atualizações

1. Cadastre um APK como release.
2. Faça `dryRun`.
3. Publique para um pequeno percentual.
4. Confira jobs e versão instalada.
5. Amplie até 100%.
6. Para rollback, publique uma release anterior conhecida.

## Build

Na aba **Actions**:

- `build-windows` gera **FleetTridi-Windows** com Central + Server + Admin + Agent APK + ADB;
- `build-android` gera **FleetTridi-Agent-APK**.

Documentação:

- `docs/QUICKSTART.md`
- `docs/ARCHITECTURE.md`
- `docs/DEPLOYMENT.md`
- `docs/RELEASES.md`
