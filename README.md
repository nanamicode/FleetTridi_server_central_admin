# FleetTridi

Central de gerenciamento remoto da frota TridiAudience.

## Estado atual — v0.4

A solução foi desenhada para crescer de uma cidade pequena para múltiplas cidades sem depender de IP fixo nem de ADB exposto na Internet.

### Componentes

- **FleetTridi.Server (.NET 8):** central online, identidade dos dispositivos, catálogo de versões, jobs, auditoria e distribuição de arquivos.
- **FleetTridi.Admin (Windows):** cadastro, bootstrap ADB, controle remoto, manutenção e ações em massa.
- **FleetTridi Agent (Android 9+):** inicia no boot, mantém conexão de saída e executa somente o conjunto permitido de operações.
- **GitHub Actions:** gera pacote Windows e APK do agente.

### O que já funciona

- cadastro de N totens por `deviceId`;
- cidade, ponto/local, tags e canal de atualização;
- bootstrap inicial por ADB;
- conexão persistente de saída via WebSocket;
- reconexão automática e inicialização no boot;
- online/offline, último IP e telemetria;
- modo de privilégio reportado pelo totem;
- instalação/atualização remota de APK;
- catálogo permanente de versões do TridiAudience;
- SHA-256 calculado pelo servidor e validado no totem;
- confirmação da versão instalada;
- rollout por IDs, cidade, local, tag ou canal;
- rollout percentual para teste gradual;
- `dryRun` para visualizar os alvos antes de atualizar;
- atualização em massa;
- reentrega de jobs após reconexão;
- reinício do TridiAudience e do Android;
- envio de arquivos apenas para áreas permitidas;
- screenshot e controle básico de entrada;
- rotação do token individual do dispositivo;
- log de auditoria;
- Docker para o servidor.

## ADB não fica aberto na Internet

O IP é usado somente no primeiro provisionamento. Depois disso:

```
FleetTridi.Admin -> HTTPS -> FleetTridi.Server <- WSS <- FleetTridi Agent
```

Cada totem abre uma conexão **de saída** até a central. Isso funciona mesmo quando o equipamento está atrás de NAT/CGNAT ou muda de IP.

Não exponha a porta ADB 5555 publicamente.

## Sobre “ADB root uma vez e root para sempre”

`adb root` eleva o processo `adbd` quando a ROM permite, mas isso **não garante** que um APK comum terá root depois que a sessão ADB acabar.

A v0.4 detecta e mostra o modo real de privilégio. Para instalação silenciosa e reboot remotos, o agente atual precisa de **`su` persistente** no equipamento (ou, futuramente, ser integrado à imagem Android como app de sistema/Device Owner com as permissões adequadas).

O bootstrap deve falhar de forma explícita quando esse pré-requisito não existir, em vez de cadastrar um totem aparentemente gerenciável que depois não consegue atualizar APK.

## Segurança de produção

Não há mais senha administrativa de produção hardcoded.

Defina pelo menos:

- `FLEETTRIDI_ADMIN_PASSWORD`
- `FLEETTRIDI_PUBLIC_URL=https://fleet.seudominio.com`

Use TLS/HTTPS na frente do servidor. Downloads são autenticados por dispositivo e o agente valida SHA-256 antes de instalar.

Para desenvolvimento estritamente local:

```powershell
$env:FLEETTRIDI_DEV_MODE="1"
dotnet run --project src/FleetTridi.Server
```

Nesse modo o servidor escuta apenas em localhost por padrão e a senha de desenvolvimento é `fleettridi-local`.

## Fluxo de atualização recomendado

1. Cadastre o APK como uma **release** com versão, package name e canal.
2. Faça um `dryRun` em um ou poucos totens.
3. Atualize um pequeno percentual da cidade.
4. Confira jobs e a versão instalada reportada.
5. Amplie para 100%.
6. Para rollback, faça deploy de uma release anterior do catálogo.

O mesmo arquivo é armazenado uma vez e referenciado pelos jobs de N totens.

## Build

Na aba **Actions**:

- `build-windows` gera `FleetTridi-Windows`;
- `build-android` gera `FleetTridi-Agent-APK`.

Consulte:

- `docs/ARCHITECTURE.md`
- `docs/DEPLOYMENT.md`
- `docs/RELEASES.md`
