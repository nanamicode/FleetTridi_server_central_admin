# Deploy do FleetTridi v0.4

## Teste local

No PowerShell:

```powershell
$env:FLEETTRIDI_DEV_MODE="1"
dotnet run --project src/FleetTridi.Server
```

Abra o Admin e use:

- servidor: `http://localhost:8787`;
- usuário: `nanamicode`;
- senha local: `fleettridi-local`.

O modo de desenvolvimento deve ficar restrito ao próprio PC.

## Produção

A central deve ter uma URL estável com HTTPS, por exemplo:

```
https://fleet.exemplo.com
```

Configure:

```powershell
$env:FLEETTRIDI_ADMIN_USER="nanamicode"
$env:FLEETTRIDI_ADMIN_PASSWORD="<senha-forte>"
$env:FLEETTRIDI_PUBLIC_URL="https://fleet.exemplo.com"
$env:FLEETTRIDI_URLS="http://0.0.0.0:8787"
./FleetTridi.Server.exe
```

Coloque Nginx, Caddy, Traefik ou outro reverse proxy TLS na frente da porta 8787. Não publique ADB/5555.

## Docker

Crie um `.env` dentro de `docker/`:

```
FLEETTRIDI_PUBLIC_URL=https://fleet.exemplo.com
FLEETTRIDI_ADMIN_USER=nanamicode
FLEETTRIDI_ADMIN_PASSWORD=troque-por-uma-senha-forte
```

Depois:

```bash
cd docker
docker compose up -d --build
```

Os dados ficam em `docker/fleettridi-data`.

## Primeiro totem

1. Ative depuração USB durante manutenção física.
2. Conecte o PC ao equipamento por USB ou ADB na rede local.
3. Confirme que o equipamento possui `su` persistente caso queira instalação silenciosa posterior.
4. Cadastre nome, cidade, local e IP inicial.
5. Instale o FleetTridi Agent.
6. Faça o enrollment de uso único.
7. Confirme que o painel mostra `PrivilegeMode=su-root`.
8. Desative qualquer exposição externa de ADB.

Depois disso o IP inicial não participa mais do controle normal.

## Migração da v0.3

Agentes v0.3 colocavam o token do dispositivo na query string e baixavam arquivos sem header de autenticação.

Para uma janela curta de migração, o servidor pode aceitar o protocolo antigo:

```
FLEETTRIDI_ALLOW_LEGACY_AGENT_AUTH=1
FLEETTRIDI_ALLOW_LEGACY_FILE_DOWNLOADS=1
```

Atualize os agentes para v0.4 e remova essas opções.

## Backup

Copie regularmente todo o diretório `data/`. Ele contém cadastro da frota, catálogo de releases, auditoria e APKs armazenados.
