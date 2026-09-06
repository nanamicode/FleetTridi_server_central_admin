# FleetTridi

Central de gerenciamento remoto da frota TridiAudience.

## Estado atual - v0.3

A base funcional agora contém:

- FleetTridi.Server em .NET 8;
- FleetTridi.Admin para Windows;
- FleetTridi Agent para Android 9+;
- cadastro por deviceId;
- bootstrap ADB uma única vez;
- conexão persistente do totem para o servidor;
- reconexão automática e inicialização no boot;
- online/offline e último IP;
- telemetria de hardware;
- instalação/atualização remota de APK;
- envio remoto de arquivos;
- atualização de APK e arquivos em massa;
- reinício do TridiAudience;
- reboot do equipamento;
- captura de tela;
- clique remoto, HOME, BACK, setas e keyevents;
- persistência da frota e dos jobs;
- Docker para hospedar a central;
- builds automáticos para Windows e APK pelo GitHub Actions.

## Por que não controlar pela lista de IPs

IP local não atravessa NAT/CGNAT e IP público pode mudar. Por isso o IP é apenas informação auxiliar.

Depois do enrollment, cada totem mantém uma conexão de saída com o FleetTridi.Server. Assim um Admin no Rio pode comandar um totem em São Paulo desde que ambos alcancem o servidor central.

## Credenciais de desenvolvimento

Login: nanamicode

Senha: veralucia12

Esses valores são os padrões solicitados para a fase atual e podem ser substituídos por variáveis de ambiente.

## Build pronto para baixar

Na aba Actions:

- build-windows gera FleetTridi-Windows;
- build-android gera FleetTridi-Agent-APK.

O pacote Windows inclui o Admin, o Server e Android platform-tools oficial para o bootstrap ADB.

## Primeiro teste

1. Execute FleetTridi.Server.exe.
2. Abra FleetTridi.Admin.exe.
3. Use http://localhost:8787.
4. Entre com as credenciais acima.
5. Clique Adicionar totem.
6. Informe nome, cidade, local e IP inicial.
7. Selecione o totem e clique Bootstrap via ADB.
8. Escolha o FleetTridi Agent APK.
9. Depois que aparecer online, teste captura da tela, HOME/BACK, APK e envio de arquivo.

## Uso entre cidades

FleetTridi.Server precisa ficar em um endereço alcançável pela Internet. Pode ser uma máquina da própria empresa com IP público/porta encaminhada ou um servidor Linux próprio.

Para arquivos e APKs, configure FLEETTRIDI_PUBLIC_URL com o endereço público da central.

Veja docs/ARCHITECTURE.md e docs/DEPLOYMENT.md.

## Pastas

- src/FleetTridi.Server
- src/FleetTridi.Admin
- android-agent
- scripts
- docker
- docs

## Próximos degraus

- stream H.264 de baixa latência;
- grupos por cidade/local;
- versionamento do TridiAudience por dispositivo;
- rollout percentual;
- rollback automático;
- dashboard histórico de temperatura/memória/uptime;
- atualização agendada;
- TLS e rotação de tokens antes da implantação pública.
