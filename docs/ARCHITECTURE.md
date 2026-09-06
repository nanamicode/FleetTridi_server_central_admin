# Arquitetura FleetTridi

## Modelo de conexão

O IP do totem é usado somente no bootstrap inicial. A identidade permanente é um deviceId com token de enrollment.

Depois do bootstrap, cada FleetTridi Agent abre um WebSocket de saída até o FleetTridi.Server. Isso funciona muito melhor entre redes, cidades e links com IP dinâmico do que tentar manter ADB TCP aberto na Internet.

Admin Windows -> FleetTridi.Server <- WebSocket de saída <- Totens

Apenas o servidor central precisa ser alcançável pela Internet.

## FleetTridi.Server

Central em .NET 8.

Responsabilidades da v0.3:

- login administrativo;
- cadastro de dispositivos por ID;
- persistência em data/devices.json;
- online/offline e último IP observado;
- telemetria;
- fila e histórico de jobs;
- upload de APK e arquivos;
- ações individuais e em massa;
- screenshot remoto;
- entrega de jobs ao agente por WebSocket;
- reentrega de jobs pendentes após reconexão.

O armazenamento em JSON mantém a primeira implantação simples e sem banco externo. A camada pode migrar para PostgreSQL quando a frota exigir sem mudar o protocolo dos totens.

## FleetTridi Agent

Aplicativo Android/Kotlin para Android 9+.

O agente inicia no boot, mantém foreground service, reconecta sozinho e usa root local somente para operações explícitas de gerenciamento da frota.

Operações implementadas:

- instalar/atualizar APK;
- sincronizar criativos;
- enviar arquivos para áreas FleetTridi;
- reiniciar TridiAudience;
- reiniciar o dispositivo;
- HOME/BACK e keyevents;
- tap;
- swipe;
- screenshot;
- telemetria de memória, armazenamento, carga, temperatura, uptime e tela.

O agente não depende de uma sessão ADB após o enrollment.

## Bootstrap inicial

1. Cadastre o totem no Admin.
2. O Server gera deviceId e enrollmentToken.
3. Conecte uma vez por ADB ao IP inicial ou por USB.
4. O Admin instala FleetTridi Agent.
5. O Admin grava URL do servidor, ID, token, cidade e local no agente.
6. O agente inicia e passa a se conectar de saída.
7. A partir daí o IP inicial deixa de ser necessário para operação normal.

## Tela remota

A v0.3 usa screenshots periódicos para priorizar compatibilidade com a BTV/Android 9.

O painel transforma cliques na imagem em coordenadas reais do Android e envia tap. Setas, Enter, Esc/Home também podem ser encaminhados.

O próximo degrau é substituir screenshots periódicos por stream H.264/MediaCodec com menor latência.

## Escala

Uma atualização em massa é enviada uma vez ao servidor e referenciada por N jobs. Cada totem baixa o arquivo diretamente do servidor.

A arquitetura já permite agrupamento por cidade, ponto e IDs. As próximas camadas de escala são grupos permanentes, rollout gradual, rollback, séries históricas e canais de versão.
