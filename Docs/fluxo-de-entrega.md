# Fluxo de entrega — do commit ao comprador

> **Para quem é isto:** quem vai entregar uma alteração em produção. Descreve o caminho
> completo — o que é automático, o que exige uma decisão humana, e por que a divisão é essa.
> Complementa o [README do projeto](../README.md) §5, que tem a justificativa de cada peça;
> aqui está a **ordem de execução**.
>
> Ao contrário das docs numeradas desta pasta, este arquivo não é material didático de ML.

---

## 1. Visão de 30 segundos

Um commit em `main` produz **três** artefatos, que seguem para **dois** destinos por
caminhos diferentes. Nem tudo é automático, e isso é deliberado.

```mermaid
flowchart LR
    C["commit em main"] --> CI["GitHub Actions<br/>ci-imagens.yml"]

    CI --> IMG["4 imagens no GHCR<br/>apiservice, webfrontend,<br/>worker, db-migrator"]
    CI --> CMP["artefato aspire-compose<br/>docker-compose.yaml + .env"]
    CI --> EXE["artefato extrator<br/>extrator.exe + manifesto.json"]

    IMG --> DEP["Deploy no Dokploy<br/>MANUAL, um clique"]
    CMP --> DEP
    DEP --> VPS["Backend no ar<br/>na VPS"]

    EXE --> PUB["job publicar-extrator<br/>AUTOMATICO, com portao"]
    PUB --> MINIO["bucket MinIO extrator"]
    MINIO --> COMP["comprador baixa<br/>pela pagina da sessao"]

    style DEP fill:#fea
    style PUB fill:#dfd
    style VPS fill:#eef
    style COMP fill:#eef
```

**O executável não viaja em imagem nenhuma.** Ele é WinForms, roda na máquina que enxerga o
PBS, e chega ao comprador por download da página da sessão — servido do MinIO. Por isso são
dois caminhos de entrega, e não um.

| Artefato | Vai para | Quem leva | Quando |
|---|---|---|---|
| 4 imagens de container | GHCR, duas tags cada | CI (`images`) | todo push verde em `main` |
| `docker-compose.yaml` + `.env` | artefato do run | CI (`images`) | idem |
| `extrator.exe` + `manifesto.json` | bucket MinIO `extrator` | CI (`publicar-extrator`) | **só quando a versão muda** |
| — | containers rodando na VPS | **você**, no Dokploy | quando você decidir |

---

## 2. O que é automático e o que não é

```mermaid
flowchart TB
    subgraph AUTO["Automático — não pede permissão"]
        A1["Rodar a suíte inteira"]
        A2["Construir e empurrar as imagens"]
        A3["Gerar o compose e o .env"]
        A4["Publicar o extrator, se a versão mudou"]
        A5["Aplicar DACPAC e EF migrations<br/>no start do db-migrator"]
    end

    subgraph MANUAL["Manual — decisão sua"]
        M1["Clicar Deploy no Dokploy"]
        M2["Recolar o YAML, se a topologia mudou"]
        M3["Preencher o .env / Environment"]
        M4["Subir o Version do extrator"]
    end

    style AUTO fill:#dfd
    style MANUAL fill:#fea
```

**Por que o deploy do backend continua manual.** O `db-migrator` aplica DACPAC no `Stage`
(com `DropObjectsNotInSource`) e EF migrations no `engine`, contra banco com dado real da
rede. Cada deployment do Dokploy carrega uma descrição escrita à mão dizendo quais migrations
entram e o que muda na leitura dos dados existentes — é o único ponto do fluxo em que alguém
lê a mudança antes de ela alcançar o banco. `autoDeploy` está em `false` de propósito.

**Por que a publicação do extrator é automática.** Não toca banco, não derruba serviço, e o
contrato de import é tolerante nas duas direções (§5). O que ela substituiu era pior: baixar
o `.zip` do Actions, logar como `PowerUser` e subir à mão — passo que ficou esquecido por sete
deploys seguidos.

---

## 3. Etapa 0 — antes de empurrar

```mermaid
flowchart LR
    W["trabalhar em<br/>branch de fase"] --> T["dotnet test<br/>nos projetos afetados"]
    T --> F5["F5 do AppHost<br/>se a mudança tem tela"]
    F5 --> V{"mexeu no<br/>extrator?"}
    V -->|sim| B["subir Version<br/>no csproj"]
    V -->|não| M["merge em main"]
    B --> M
    style B fill:#fea
```

Dois pontos que não são cerimônia:

- **Consulta nova do extrator só vale depois de rodar contra um PBS real.** Nada na suíte
  executa SQL — as queries são recurso embarcado e os testes são unitários. Foi assim que uma
  coluna inexistente passou por 288 testes verdes e morreu na mão do comprador. Use o acesso
  de leitura à instância da Natusfarma.
- **Mexeu no extrator, suba o `<Version>`.** Se não subir, o CI fica vermelho pedindo (§6) —
  e é bem melhor descobrir aqui.

---

## 4. Etapa 1 — push em `main` dispara o CI

`.github/workflows/ci-imagens.yml`, em push na `main` ou por `workflow_dispatch`. Quatro
jobs, com essa dependência:

```mermaid
flowchart TB
    W["windows-tests<br/>Windows"]
    L["linux-tests<br/>Linux"]
    I["images"]
    P["publicar-extrator"]

    W --> I
    L --> I
    W --> P
    L --> P

    W -.->|"artefato extrator"| P
    I -.->|"artefato aspire-compose"| OP(["você, na etapa 2"])

    style I fill:#eef
    style P fill:#dfd
```

| Job | O que faz | Por que existe assim |
|---|---|---|
| `windows-tests` | Testes do extrator e o binário: publica `win-x64` self-contained, calcula o SHA-256, escreve o `manifesto.json`, sobe o par como artefato `extrator`. | O extrator é WinForms (`net10.0-windows`) e **não compila em Linux** — nem ele nem o teste dele. A suíte é dividida por sistema operacional por necessidade, não por paralelismo. |
| `linux-tests` | Compila em **Debug**, roda os dez projetos de teste puros e depois os dois que sobem o AppHost real com SQL Server e MinIO em container. | Debug porque é a configuração dos testes, e é dela que sai o DACPAC copiado para o `bin` do `Migrator`. Integração e E2E ficam em passos **sequenciais**: eles se excluem por um lock de arquivo, e em paralelo o segundo esperaria o tempo do lock. |
| `images` | Imagem base do worker, `aspire do push` (as quatro imagens, tag imutável), **smoke** da imagem do worker, tag móvel, `aspire publish`. | Só roda se os dois jobs de teste passarem. |
| `publicar-extrator` | Compara versões e publica o extrator no destino. Só em `main`. | Não depende de `images` — o `.exe` não entra em imagem. Depende de `linux-tests` **por mérito**: extrator não vai para a mão do comprador a partir de commit com o backend vermelho. |

### O smoke do worker, e por que ele não é redundante

```mermaid
flowchart LR
    T["Forecasting.Tests<br/>treina LightGBM<br/>de verdade, em Linux"] -->|"passa"| OK1["verde"]
    S["smoke: abre a IMAGEM<br/>dotnet --list-runtimes<br/>+ ldd no lib_lightgbm.so"] --> OK2["verde ou vermelho"]

    OK1 -.->|"não cobre"| GAP["a imagem publicada"]
    S --> GAP

    style GAP fill:#fdd
```

Os testes passam porque o runner do GitHub tem `libgomp1`; a **imagem** não tinha, e o treino
morria em produção com `Unable to load shared library 'lib_lightgbm'`. Entre o teste verde e o
destino existe um artefato que nenhum teste abre. O smoke abre — e confere framework **antes**
do nativo, porque a primeira versão dele passou numa imagem que nem iniciava (exit 150, `No
frameworks were found`, dois dias de fila parada).

O smoke **não barra o push** (o `aspire do push` constrói e empurra na mesma operação). O que
a falha impede é o **uso**: a tag móvel não avança e o artefato de compose não é publicado.

---

## 5. Etapa 2 — deploy do backend (manual)

```mermaid
sequenceDiagram
    actor Você
    participant GH as GitHub Actions
    participant DK as Dokploy
    participant MIG as db-migrator
    participant SVC as apiservice / web / worker

    Você->>GH: baixar artefato aspire-compose
    Note over Você,GH: só se a topologia do AppHost mudou
    Você->>DK: recolar o docker-compose.yaml (raw)
    Você->>DK: preencher o Environment<br/>(*_IMAGE com a tag sha-xxxxxxx)
    Você->>DK: clicar Deploy
    DK->>MIG: sobe primeiro
    MIG->>MIG: DACPAC no Stage
    MIG->>MIG: EF migrations no engine
    alt qualquer etapa falha
        MIG-->>DK: exit != 0
        DK-->>Você: nenhum serviço sobe
    else tudo aplicado
        MIG-->>DK: exit 0
        DK->>SVC: service_completed_successfully libera
        SVC-->>Você: no ar
    end
```

### Quando é preciso recolar o YAML

**Recolar** quando o AppHost mudou de topologia — recurso novo, parâmetro novo, env var nova,
`depends_on` diferente. **Não recolar** quando só o código mudou: aí basta apontar as
`*_IMAGE` para as tags novas e deployar.

```mermaid
flowchart TB
    Q1{"o AppHost.cs<br/>mudou?"}
    Q1 -->|não| SO["só trocar as *_IMAGE<br/>e clicar Deploy"]
    Q1 -->|sim| Q2{"mudou parâmetro,<br/>env var ou recurso?"}
    Q2 -->|não| SO
    Q2 -->|sim| RE["aspire publish<br/>+ recolar o YAML<br/>+ preencher a variável nova"]

    style RE fill:#fea
```

**A armadilha silenciosa:** parâmetro novo no AppHost é **linha nova no compose**. Preencher a
variável no Environment do Dokploy não basta — se o YAML colado não tiver o
`Extrator__PublishToken: "${EXTRATOR_PUBLISH_TOKEN}"` no serviço, o processo não recebe nada e
a falha não aparece como erro: a rota responde 503 como se ninguém tivesse configurado.

### Use a tag imutável

`*_IMAGE` recebe `…/worker:sha-5b36f24` — os 7 primeiros do commit, sem precisar caçar no log
do CI. A tag móvel (`:main`) serve para "o último verde daquela branch"; num `.env` de produção
ela faria o próximo `docker compose pull` puxar outra coisa em silêncio, e "qual commit está
rodando?" deixaria de ter resposta. Os quatro serviços têm `pull_policy: always` justamente
porque o Compose aceita "já existe local" — sem isso, deploy depois de CI verde subiria o
binário antigo **e reportaria sucesso**.

### O que sobe sozinho depois

- **`restart: unless-stopped`** em todos os serviços menos o `db-migrator` (one-shot; com
  política de reinício ele republicaria o DACPAC em loop). Produção já ficou **dois dias sem
  worker** porque o compose não trazia `restart:` e o default do Docker é `no`.
- **`OrfaosWorker`** encerra por idade job preso em `Processando` — as filas reclamam sem
  lease, então linha reclamada por processo morto não é encerrada por ninguém.

---

## 6. Etapa 3 — publicação do extrator (automática)

```mermaid
flowchart TB
    ST["job publicar-extrator"] --> CFG{"EXTRATOR_PUBLISH_URL<br/>e TOKEN configurados?"}
    CFG -->|não| SKIP["aviso e sai VERDE<br/>publicação pela UI segue valendo"]
    CFG -->|sim| GET["GET /extrator/publicacao"]

    GET --> ST404{"status"}
    ST404 -->|"404"| E404["VERMELHO: a Web no ar é<br/>anterior a esta rota.<br/>Deploye o backend primeiro"]
    ST404 -->|"401"| E401["VERMELHO: secret != env var<br/>do destino"]
    ST404 -->|"503"| E503["VERMELHO: destino sem<br/>token preenchido"]
    ST404 -->|"200"| CMP{"versão no ar<br/>== versão do build?"}

    CMP -->|"não"| POST["POST do ZIP<br/>e confere versão + SHA<br/>que o servidor recalculou"]
    POST --> DONE["PUBLICADO"]

    CMP -->|"sim"| GRD{"o extrator mudou depois<br/>do commit que definiu<br/>essa versão?"}
    GRD -->|"não"| NOP["aviso: já está no ar,<br/>nada publicado"]
    GRD -->|"sim"| EBMP["VERMELHO: suba o Version"]

    style DONE fill:#dfd
    style SKIP fill:#eef
    style NOP fill:#eef
    style EBMP fill:#fdd
    style E404 fill:#fdd
    style E401 fill:#fdd
    style E503 fill:#fdd
```

**O portão é a versão, não o commit.** O binário muda em **todo** build mesmo sem mudança no
extrator — o SDK anexa o commit ao `InformationalVersion`, então o SHA-256 sai diferente — com
o `<Version>` parado. Publicar por push encheria a tela do comprador de "0.18.2" com checksum
novo a cada vez, e o checksum é justamente o que ele confere com `Get-FileHash`.

**A guarda existe para o portão não virar o próprio problema.** A base da comparação é o commit
que **definiu** a versão, não o push anterior: com o push anterior como base, ignorar um build
vermelho e empurrar qualquer outra coisa devolveria o verde com a mudança ainda parada. Assim a
guarda não esquece — fica vermelha em todo push até a versão subir. Ela nasceu de um caso real:
`0.18.1` foi definida em `2d03534`, `cc31b0e` mexeu no extrator depois sem bump, e as duas
correções atravessaram sete deploys sem chegar ao comprador.

**Publicar antes de o backend novo subir é seguro, e por desenho.** O contrato de import é
tolerante nas duas direções: entrada desconhecida no ZIP nunca é validada nem carregada,
arquivo novo entra como `OptionalFiles` e coluna é conferida por nome. Foi assim que o
`catalogo_eans.csv` da 0.18.0 conviveu com o backend anterior. Só uma mudança **destrutiva** de
contrato — renomear ou remover coluna obrigatória — pediria ordenar deploy e publicação.

---

## 7. Decidindo o que fazer, pelo que o commit mudou

```mermaid
flowchart TB
    START(["o que mudou<br/>no commit?"])

    START --> Q_APP{"AppHost:<br/>parâmetro ou<br/>recurso novo?"}
    START --> Q_MIG{"EF migration ou<br/>.sqlproj?"}
    START --> Q_EXT{"projeto<br/>Extractor?"}
    START --> Q_COD{"só código de<br/>backend?"}

    Q_APP -->|sim| A_APP["aspire publish, recolar o YAML,<br/>preencher a variável nova<br/>no Environment"]
    Q_MIG -->|sim| A_MIG["nada a mais: o db-migrator aplica<br/>no deploy. Descreva a migration<br/>na descrição do deployment"]
    Q_EXT -->|sim| A_EXT["subir Version no csproj.<br/>O CI publica no próximo push"]
    Q_COD -->|sim| A_COD["trocar as *_IMAGE<br/>para a tag sha nova<br/>e clicar Deploy"]

    style A_APP fill:#fea
    style A_EXT fill:#fea
```

**Mudança no contrato CSV toca os dois lados de uma vez** — `Database`, `Worker`, `ApiService`
e `Extractor/StageContract`. É por isso que os testes do extrator referenciam `Worker` e
`ApiService`: `StageContractTests` e `ImportCompatibilityTests` são o único guarda contra as
duas definições divergirem em silêncio. Nesse caso valem as quatro linhas acima ao mesmo tempo.

---

## 8. Checklist de entrega

```
[ ] Testes verdes localmente nos projetos afetados
[ ] Consulta nova do extrator rodada contra o PBS real
[ ] <Version> do extrator subido, se o extrator mudou
[ ] Push em main; CI verde nos quatro jobs
[ ] Se a topologia mudou: aspire publish, recolar o YAML no Dokploy
[ ] Environment do Dokploy com as *_IMAGE na tag sha-xxxxxxx
[ ] Deploy clicado, com descrição dizendo quais migrations entram
[ ] db-migrator saiu com exit 0 (senão nenhum serviço subiu)
[ ] Login na Web funciona
[ ] Versão do extrator no ar confere em /admin/extrator
```

---

## 9. Quando algo falha, onde olhar

| Sintoma | Causa provável | Onde |
|---|---|---|
| Job `publicar-extrator` em 404 | a Web no ar é anterior à rota `/extrator/publicacao` | deploye o backend primeiro |
| Job `publicar-extrator` em 503 | `EXTRATOR_PUBLISH_TOKEN` vazio no destino, **ou** o YAML colado sem a linha da env var | Environment do Dokploy **e** o compose colado |
| Job `publicar-extrator` em 401 | secret do GitHub != env var do destino | os dois valores |
| Job pede bump de versão | extrator mudou depois do commit que definiu a versão atual | `<Version>` no csproj do extrator |
| Nenhum serviço sobe depois do deploy | `db-migrator` saiu != 0 | log do `db-migrator` no Dokploy |
| Deploy verde mas comportamento antigo | `*_IMAGE` apontando para a tag anterior | Environment do Dokploy |
| Treino morre com `lib_lightgbm` | imagem do worker sem `libgomp1` | smoke do CI; `worker-base.Dockerfile` |
| Fila parada e tela dizendo "importando" | worker morto sem reinício | `restart:` no compose; `docker ps` |
| Tela de mercado toda com travessão | ZIP de extrator anterior à versão que traz `Cnpj`/`catalogo_eans.csv` | versão publicada em `/admin/extrator` |

**Extrator publicado é global, não por rede.** O `.exe` é o mesmo para todo inquilino, então
publicar troca a versão de todos ao mesmo tempo — e um `manifesto.json` incoerente é recusado
sem escrever nada, deixando a versão anterior intacta.

---

## 10. Configuração de uma vez só

Precisa existir antes de o fluxo automático funcionar. Depois disso, nada aqui se repete.

| Onde | Entrada | Valor |
|---|---|---|
| GitHub → Settings → **Variables** | `EXTRATOR_PUBLISH_URL` | URL pública da Web |
| GitHub → Settings → **Secrets** | `EXTRATOR_PUBLISH_TOKEN` | um segredo longo |
| Dokploy → Environment | `EXTRATOR_PUBLISH_TOKEN` | **o mesmo valor** |

A URL fica em *Variables* e não em *Secrets* de propósito: o GitHub mascara valor de secret no
log, e "não consegui falar com `***`" não diz com quem o job tentou falar — justamente na hora
em que o log é necessário. Ela não é segredo; o token é.

Sem essas entradas o fluxo **não falha**: o job avisa e sai verde, e a publicação pela UI em
`/admin/extrator` continua sendo o caminho.

---

## Leituras relacionadas

- [README §5 — Como rodar, publicar o extrator, CI e deploy](../README.md) — a justificativa
  de cada peça, e os buracos que cada uma fechou.
- [CLAUDE.md §5 — Operações de risco](../CLAUDE.md) — o que exige confirmação.
- [extracao-pbs-stage.md](extracao-pbs-stage.md) — o que o extrator lê do PBS.
- [schema.md](schema.md) — as tabelas dos dois bancos.
