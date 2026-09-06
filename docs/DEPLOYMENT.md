# Colocando o FleetTridi para funcionar

## Teste no mesmo PC

1. Baixe o artefato FleetTridi-Windows do GitHub Actions.
2. Execute FleetTridi.Server.exe.
3. Execute FleetTridi.Admin.exe.
4. Use o servidor http://localhost:8787.
5. Login: nanamicode.
6. Senha: veralucia12.

## Primeiro totem

O primeiro enrollment deve acontecer com o PC conseguindo alcançar ADB da placa.

1. Ative depuração USB.
2. Coloque ADB TCP/IP na placa se essa for a forma de conexão.
3. No Admin, clique Adicionar totem.
4. Informe nome, cidade, local e IP inicial.
5. Selecione o totem.
6. Clique Bootstrap via ADB.
7. Selecione FleetTridiAgent.apk.
8. O equipamento deverá aparecer online depois que o agente iniciar.

A partir desse momento o IP inicial deixa de ser necessário para o controle normal.

## Entre cidades

FleetTridi.Server precisa possuir um endereço que Admin e totens consigam alcançar.

Pode ser:

- um PC ou servidor da empresa com IP público e porta 8787 encaminhada;
- uma máquina Linux em datacenter;
- outro host próprio alcançável pela Internet.

Antes de enviar APKs e arquivos, configure FLEETTRIDI_PUBLIC_URL com o endereço usado pelos totens. Exemplo PowerShell:

    $env:FLEETTRIDI_PUBLIC_URL="http://203.0.113.20:8787"
    ./FleetTridi.Server.exe

Se a sede estiver atrás de CGNAT, apenas abrir porta no roteador não cria um endereço público. Nesse caso o servidor central precisa ficar em um ponto realmente alcançável pela Internet.

## Docker

    cd docker
    docker compose up -d --build

Os dados persistentes ficam em docker/fleettridi-data.

## Variáveis

- FLEETTRIDI_URLS
- FLEETTRIDI_PUBLIC_URL
- FLEETTRIDI_DATA_DIR
- FLEETTRIDI_ADMIN_USER
- FLEETTRIDI_ADMIN_PASSWORD
- FLEETTRIDI_ADMIN_TOKEN

As credenciais padrão existem somente para a fase de construção solicitada.
