# Releases e rollouts

A v0.4 transforma APKs do TridiAudience em releases reutilizáveis.

## 1. Cadastrar uma release

No Admin Windows:

1. Entre na central.
2. Na seção **Releases do TridiAudience**, informe a versão.
3. Escolha o canal, normalmente `stable`.
4. Clique **Cadastrar novo APK no catálogo...**.
5. Selecione o APK.

O servidor calcula e salva:

- SHA-256;
- tamanho;
- nome original;
- versão;
- package name;
- canal;
- data de criação.

O APK fica armazenado uma única vez no servidor.

## 2. Simular antes de atualizar

Informe uma cidade no campo de rollout, por exemplo:

`Ourinhos`

Defina um percentual, por exemplo:

`10`

Clique **Simular rollout**.

Nenhum job é criado. A resposta mostra quais dispositivos seriam atingidos.

Se o campo de cidade ficar vazio, o Admin usa somente os totens selecionados na tabela.

## 3. Canary rollout

Depois de revisar a simulação:

1. publique para 5% ou 10%;
2. aguarde os jobs terminarem;
3. confira a coluna de versão do TridiAudience;
4. confirme ausência de falhas;
5. aumente progressivamente para 25%, 50% e 100%.

O conjunto percentual é determinístico pela ordenação dos IDs, evitando trocar de amostra aleatoriamente entre execuções.

## 4. Rollback

Rollback não precisa de um mecanismo especial destrutivo.

Selecione no catálogo uma release anterior que já tenha sido validada e faça um novo rollout para os mesmos alvos.

Como o agente usa `pm install -r -d`, a instalação permite downgrade quando a ROM aceita essa operação com root.

## 5. Integridade

Cada job de instalação recebe o SHA-256 esperado.

O agente:

1. baixa o APK autenticando-se como o próprio dispositivo;
2. calcula SHA-256 localmente;
3. compara com o hash da central;
4. aborta se houver divergência;
5. instala somente após validação;
6. consulta o PackageManager;
7. devolve a versão realmente instalada.

## API

### Listar releases

`GET /api/releases`

### Criar release

`POST /api/releases` usando `multipart/form-data`:

- `file`;
- `version`;
- `packageName`;
- `channel`;
- `notes`.

### Simular ou publicar rollout

`POST /api/releases/{releaseId}/deploy`

Exemplo:

```json
{
  "city": "Ourinhos",
  "onlyOnline": true,
  "percent": 10,
  "dryRun": true
}
```

Seletores disponíveis:

- `ids`;
- `city`;
- `site`;
- `channel`;
- `tag`;
- `allOnline`;
- `onlyOnline`.

Altere `dryRun` para `false` somente depois de conferir os alvos.
