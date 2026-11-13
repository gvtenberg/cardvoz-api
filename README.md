# CardVoz API 🎙️🃏

API RESTful em **C# (.NET 9)** do **CardVoz**, app de fichas de estudo (flashcards) para pessoas cegas e com baixa visão, do Instituto Federal do Tocantins (IFTO, *Campus* Palmas).

O CardVoz tem quatro repositórios: o site (`cardvoz`, Next.js estático), esta API, o banco (`cardvoz-db`, migrations do Supabase) e o painel da equipe (`cardvoz-admin`). Regras do projeto e combinados da equipe, para pessoas e para ferramentas de IA: [`AGENTS.md`](AGENTS.md).

---

## 🏛️ Arquitetura

O projeto adota uma arquitetura em camadas limpas, com separação de responsabilidades e foco em desacoplamento:

```
cardvoz-api/
├── Auth/              # Configuração do JWT e do Google, limite de tentativas e leitura do usuário logado
├── Controllers/       # Controladores REST com anotações OpenAPI/Swagger
├── DTOs/              # Objetos de transferência de dados (Request / Response)
├── Models/            # Entidades (User, RefreshToken, Deck, Card, StudySession, StudyReview)
├── Data/              # EF Core DbContext, mapeamento fluente e conexão com o Supabase
├── Services/          # Interfaces e regras de negócio (inclui login, Google e tokens)
├── Program.cs         # Inicialização, autenticação, autorização, CORS e Swagger
└── appsettings.json   # Configurações da aplicação
```

---

## ⚡ Recursos e Diferenciais

- **Feita para leitor de tela**: quem usa o CardVoz estuda com o leitor de tela do aparelho (TalkBack, NVDA); o site não tem voz própria. Os campos `voiceUsed` (na resposta de estudo) e `voiceNotes` (na ficha) existem, mas o site não os usa nem exibe.
- **Duas formas de entrar**: e-mail e senha, ou Google (fluxo *authorization code* com PKCE). No Google, a API troca o código pelo `id_token`, confere a assinatura e devolve os mesmos tokens próprios do login por senha; na primeira vez a conta é criada sozinha.
- **Contas e login**: senha guardada só como hash (PBKDF2-SHA512, 100 mil iterações, com salt); sessão com token de acesso JWT de 15 minutos e token de renovação de 30 dias.
- **Baralhos privados**: cada pessoa só vê, estuda, edita e exclui os próprios baralhos, fichas e sessões. Recursos de outra pessoa respondem 404.
- **IDs aleatórios**: todos os registros usam UUID v4, impossível de adivinhar ou varrer em sequência.
- **Banco de Dados PostgreSQL (Supabase)**: O esquema (tabelas, índices e RLS) fica no repositório [`cardvoz-db`](../cardvoz-db), como migrations do Supabase. A API não cria tabelas: ela só se conecta e avisa no log se as migrations não tiverem sido aplicadas.
- **Documentação Interativa**: Swagger UI acessível em `/swagger`.

---

## 🚀 Como Executar

### Pré-requisitos
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Um projeto no [Supabase](https://supabase.com) (ou qualquer PostgreSQL)

### Configuração do banco
1. Aplique as migrations do repositório [`cardvoz-db`](../cardvoz-db) no seu projeto do Supabase (veja o README dele).
2. Copie `.env.example` para `.env`, gere a `JWT_SIGNING_KEY` (`openssl rand -base64 48`) e preencha `ConnectionStrings__DefaultConnection` com a conexão do Supabase: no painel, **Connect → Session pooler** (porta 5432). Use o *pooler*, não a conexão direta: a direta só funciona por IPv6, que hospedagens como o Render não suportam. A senha pode ter qualquer caractere; troque `[YOUR-PASSWORD]` inteiro, inclusive os colchetes.

### Passos
```bash
# Restaurar dependências
dotnet restore

# Compilar o projeto
dotnet build

# Executar a API
dotnet run
```

A API iniciará por padrão em:
- **Swagger UI:** [http://localhost:5000/swagger](http://localhost:5000/swagger)
- **Base URL:** `http://localhost:5000/api`

---

## 🔐 Autenticação

Todas as rotas exigem login, exceto as de `/api/auth` (menos `/me`). Envie o token de acesso no cabeçalho:

```
Authorization: Bearer <accessToken>
```

### Contas e sessão (`/api/auth`)
- `POST /api/auth/google` - Entra com o Google. Corpo: `code` (devolvido pelo Google), `codeVerifier` (PKCE) e `redirectUri` (o mesmo usado na ida, ex.: `https://<site>/entrar/`). Na primeira vez cria a conta. Liga o Google a uma conta criada com senha no mesmo e-mail só se esse e-mail já foi confirmado (link do cadastro, Perfil ou "Esqueci minha senha"); sem confirmar, a conta pode ter sido criada por outra pessoa. Erros: `400` (endereço de retorno fora do CORS), `401` (código inválido/vencido/já usado ou e-mail do Google não confirmado), `409` (e-mail de uma conta com senha ainda não confirmado, ou de outra conta Google), `503` (Google não configurado).
- `POST /api/auth/register` - Cria a conta (`name`, `email`, `password` com 8 a 128 caracteres) e já devolve os tokens. E-mail repetido: `409`.
- `POST /api/auth/login` - Entra com `email` e `password`. Erro: `401` com a mesma mensagem para e-mail inexistente e senha errada.
- `POST /api/auth/refresh` - Troca o `refreshToken` por um novo par de tokens. O token usado deixa de valer.
- `POST /api/auth/logout` - Revoga o `refreshToken` (sempre `204`).
- `GET /api/auth/me` - Dados da pessoa logada.

Google, login, cadastro e renovação devolvem:

```json
{
  "accessToken": "eyJ...", "accessTokenExpiresAt": "2026-10-13T12:15:00Z",
  "refreshToken": "q1w2...", "refreshTokenExpiresAt": "2026-11-12T12:00:00Z",
  "user": { "id": "3f6c…", "name": "Ana", "email": "ana@exemplo.com", "createdAt": "…" }
}
```

### Como o frontend deve usar
1. Guarde o `accessToken` **em memória** (variável/estado) e envie em todas as chamadas.
2. Guarde o `refreshToken` de forma persistente para manter a pessoa logada (ex.: `localStorage`). Ele só deve ir para `/api/auth/refresh` e `/api/auth/logout`.
3. Ao receber `401` em qualquer rota, chame `/api/auth/refresh` **uma única vez** e repita a requisição. Se o refresh também der `401`, mande para a tela de login.
4. Não faça duas renovações ao mesmo tempo (ex.: duas abas): reaproveite a mesma promessa. Se um token já trocado for reapresentado depois de 30 segundos, a API entende como roubo e encerra **todas** as sessões da pessoa.

### Proteções
- **Bloqueio de conta**: 5 senhas erradas seguidas bloqueiam o login por 15 minutos (`429` com `Retry-After`).
- **Limite por IP**: até 10 chamadas por minuto em cadastro e login com senha; até 60 por minuto em Google, renovação e logout (`429`). O limite maior existe porque numa rede compartilhada (o Wi-Fi da escola) todo mundo sai pelo mesmo IP, e cada página recarregada é uma renovação.
- **Login com Google**: o segredo do cliente fica só na API; o `redirectUri` precisa ser `/entrar/` de uma origem do CORS; o `id_token` tem assinatura, emissor, público e validade conferidos. Conta nova só com e-mail confirmado pelo Google.
- **Gravações ao mesmo tempo**: dois aparelhos enviando estudo do mesmo baralho, ou o mesmo pedido repetido enquanto o primeiro grava, esperam um pelo outro (trava do PostgreSQL por baralho e por pessoa, `Data/DbLocks.cs`). Nenhum acerto se perde e o pedido repetido responde 200 com o que já foi salvo.
- **Datas do aparelho**: horários do futuro (relógio adiantado) viram a hora do servidor, no estudo e no histórico importado.
- **Tokens**: assinatura HS256 com algoritmo fixo; emissor, público e validade conferidos. Os tokens de renovação são aleatórios (256 bits) e só o hash SHA-256 fica no banco.

### Variáveis de ambiente
| Variável | Obrigatória | Descrição |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | Sim | URI do Session pooler do Supabase |
| `JWT_SIGNING_KEY` | Sim | Chave de assinatura dos tokens, mínimo 32 caracteres (`openssl rand -base64 48`). Sem ela a API não sobe. |
| `CORS_ALLOWED_ORIGINS` | Em produção | Endereços do site e do painel de admin, separados por vírgula e sem barra no final |
| `GOOGLE_CLIENT_ID` | Para o login com Google | ID do cliente OAuth (o mesmo do `NEXT_PUBLIC_GOOGLE_CLIENT_ID` do site). Passo a passo no README do CardVoz, seção "Login com Google" |
| `GOOGLE_CLIENT_SECRET` | Para o login com Google | Segredo do cliente OAuth. Nunca vai para o site nem para o repositório |
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM` | Para "Esqueci minha senha" | Servidor de e-mail (Gmail com senha de app, Brevo, Resend...). Porta 587 com STARTTLS (a 465 não funciona). `SMTP_FROM` = remetente, ex.: `CardVoz <nao-responda@seudominio>`. Sem eles, a rota responde 503 |
| `SITE_URL` | Para "Esqueci minha senha" | Endereço do site, sem barra no final (ex.: `https://cardvoz.onrender.com`). O link do e-mail é `SITE_URL/redefinir-senha/#codigo=...` |
| `JWT_ISSUER`, `JWT_AUDIENCE` | Não | Padrão: `cardvoz-api` e `cardvoz` |

---

## 📋 Endpoints Principais

Todos os IDs são UUID (texto, ex.: `"3f6c2b1e-…"`).

### Senha esquecida (`/api/auth`)
- `POST /api/auth/forgot-password` - `{ "email" }`. Se o e-mail tiver conta, manda um link com código de uso único, válido por 1 hora (só o hash fica no banco; um e-mail por minuto por conta). A resposta é sempre a mesma, para não revelar quais e-mails têm conta. Sem SMTP configurado, `503`. Serve também para quem entra só com o Google criar uma senha
- `POST /api/auth/reset-password` - `{ "token", "password" }`. Troca a senha, zera o bloqueio por tentativas e encerra as sessões abertas (`204`). Código usado ou vencido: `400`

### Confirmação de e-mail (`/api/auth`)
- O cadastro com senha manda um link de confirmação (`SITE_URL/confirmar-email/#codigo=...`, uso único, 24 horas), se o SMTP estiver configurado. Confirmar **não é obrigatório** para usar o CardVoz.
- `POST /api/auth/confirm-email` - `{ "token" }`. Confirma (`204`); código usado ou vencido: `400`
- `POST /api/auth/resend-confirmation` - Com login. Manda o link de novo (`200`); já confirmado: `409`; menos de 1 minuto desde o último: `429`; sem SMTP: `503`
- Contas criadas pelo Google e quem cria senha pelo link de "Esqueci minha senha" ficam confirmadas. O `user` devolvido no login traz `emailConfirmed`.
- **Junção com o Google:** se a conta com senha tiver o e-mail confirmado, entrar com o Google no mesmo e-mail liga o Google a essa conta (a pessoa entra dos dois jeitos). Sem confirmar, a API responde `409` pedindo para confirmar o e-mail no Perfil.

### Passe do painel (`/api/auth`)
- `POST /api/auth/admin-pass` - Com login de admin. Devolve `{ "code", "expiresAt" }`: código de uso único que vale 1 minuto. O site abre `cardvoz-admin/entrar/#passe=<code>` com ele
- `POST /api/auth/admin-pass/redeem` - `{ "code" }`. Troca o passe por uma sessão nova do painel (mesma resposta do login). Código usado, vencido ou de conta que deixou de ser admin: `400`
- O passe fica só na memória da API (sem tabela): se ela reiniciar nesse minuto, o painel mostra o login normal ([`docs/tarefas/passe-do-painel.md`](docs/tarefas/passe-do-painel.md))

### Conta (`/api/account`)
Direitos da pessoa sobre os próprios dados (LGPD).
- `GET /api/account/export` - Tudo o que a conta guarda em JSON: dados da conta, baralhos com fichas (acertos, erros, revisão) e histórico de estudo. Sem hash de senha nem tokens
- `POST /api/account/delete` - Exclui a conta com baralhos, fichas, sessões e tokens. Pede `confirmEmail` (o e-mail da conta) e, em conta com senha, `password`. Única pessoa admin recebe `409`. Limite de 10 por minuto por IP

### Saúde (`/api/health`)
- `GET /api/health` - Sem login. Lê o banco e responde `{ "status": "ok" }` (ou `503`). A Action **Manter ativo** (`.github/workflows/manter-ativo.yml`) chama esta rota a cada 10 minutos: a API não "adormece" no Render (sem a espera de até 1 minuto ao abrir o site) e o Supabase gratuito não pausa por falta de uso. Usa cerca de 730 das 750 horas gratuitas mensais da conta do Render. Para mudar o endereço, crie a variável `API_HEALTH_URL` no repositório que publica a API.

### Baralhos (`/api/decks`)
- `GET /api/decks` - Lista os baralhos da pessoa logada (suporta `?category=` e `?search=`)
- `GET /api/decks/categories` - Lista as categorias dos baralhos da pessoa logada
- `GET /api/decks/changes?since=<data>` - Baralhos criados, alterados ou estudados depois de `since` (com as fichas), `allIds` (todos os baralhos atuais, para o site saber o que foi excluído) e `serverTime` (o `since` da próxima vez). Sem `since`, todos. Folga de 5 s no `since`
- `POST /api/decks/import` - Cria o baralho com todas as fichas numa chamada, numa transação (até 1000 fichas; cada ficha pode trazer os acertos e erros que tinha no aparelho). Com `id` (UUID gerado no aparelho) já salvo da mesma pessoa, responde `200` com o que existe, sem duplicar; de outra pessoa, `409`
- `POST /api/decks/{id}/cards/batch` - Acrescenta várias fichas no fim do baralho, numa transação (`{ "cards": [...] }`, até 1000)
- `GET /api/decks/{idOrSlug}` - Obtém baralho detalhado por ID ou slug
- `POST /api/decks` - Cria um novo baralho
- Os slugs `changes`, `import` e `categories` são reservados (ganham `-1`), porque esses endereços fixos têm prioridade sobre `{idOrSlug}`
- `PUT /api/decks/{id}` - Edita título, categoria, descrição ou slug (só os campos enviados)
- `DELETE /api/decks/{id}` - Exclui o baralho com suas fichas e sessões

### Fichas (`/api/cards`)
- `GET /api/decks/{deckId}/cards` - Lista as fichas de um baralho
- `GET /api/cards/{id}` - Detalhes de uma ficha
- `POST /api/cards` - Adiciona uma ficha a um baralho
- `PUT /api/cards/{id}` - Atualiza uma ficha
- `DELETE /api/cards/{id}` - Remove uma ficha
- Cada ficha devolve `reviewLevel` e `nextReviewAt` (revisão espaçada, ver [`docs/tarefas/revisao-espacada.md`](docs/tarefas/revisao-espacada.md)), `correctCount`, `incorrectCount`, `lastAnsweredAt` e `lastWasCorrect` (os dois últimos ficam de fora do JSON quando nulos: ficha nunca estudada). O baralho devolve `lastStudiedAt`, a hora em que o último estudo chegou à conta. `POST /api/cards` aceita os mesmos números, para levar o histórico de uma ficha estudada sem login

### Sessões de Estudo (`/api/study-sessions`)
- `POST /api/study-sessions/complete` - Guarda uma sessão já concluída numa chamada (feita com ou sem internet): sessão, respostas e acertos e erros das fichas, numa transação. Respostas de fichas que não são do baralho são ignoradas; horários no futuro viram o horário do servidor; até 1000 respostas; `completedAt` antes de `startedAt` responde `400`. Com `id` repetido da mesma pessoa, `200` sem somar de novo; de outra pessoa, `409`
- `POST /api/study-sessions/start` - Inicia sessão de estudo por `deckId` ou `deckSlug`
- `POST /api/study-sessions/{id}/review` - Registra acerto/erro e flag `voiceUsed` (também soma na ficha)
- `POST /api/study-sessions/{id}/finish` - Conclui a sessão e consolida pontuação
- `GET /api/study-sessions/{id}` - Consulta detalhes de uma sessão
- `GET /api/study-sessions/history` - Histórico de sessões da pessoa logada (`?deckId=`, `?limit=` até 100)

### Baralhos compartilhados (`/api/shared-decks`)
Baralho sem dono (`UserId` nulo), criado pelo painel de admin. Não aparece na lista de ninguém: cada pessoa adiciona uma cópia aos próprios baralhos e estuda ou edita a cópia.
- `GET /api/shared-decks` - Lista os baralhos compartilhados
- `POST /api/shared-decks/{id}/copy` - Copia o baralho, com as fichas, para os baralhos da pessoa logada (`201`). A cópia guarda `sourceDeckId` (o compartilhado de origem, que vira nulo se ele for excluído), e `GET /api/decks`, `GET /api/decks/{id}`, `/api/decks/changes` e `/api/decks/import` devolvem esse campo para o site saber o que já foi adicionado

### Painel de admin (`/api/admin`)
Só para contas com `IsAdmin` (as demais recebem `403`). A API confere no banco a cada chamada, então tirar o admin vale na hora. O primeiro admin é definido pelo SQL Editor do Supabase (README do `cardvoz-db`); os seguintes, pelo painel. O `user` devolvido no login traz `isAdmin`.
- `GET /api/admin/stats` - Números gerais (usuários, admins, baralhos, compartilhados, fichas, sessões) e de estudo: acertos e erros somados (`correctAnswers`, `incorrectAnswers`), fichas para revisar agora (`cardsDueNow`), pessoas que estudaram nos últimos 7 dias (`activeLast7Days`) e e-mails confirmados (`confirmedEmails`)
- `GET /api/admin/users` - Contas, com forma de entrar (senha/Google) e número de baralhos
- `PUT /api/admin/users/{id}/admin` - Dá ou tira o admin (`{ "isAdmin": true }`). Ninguém muda o próprio acesso (`400`)
- `DELETE /api/admin/users/{id}` - Exclui a conta com baralhos, fichas e sessões. Ninguém exclui a própria conta (`400`)
- `GET /api/admin/shared-decks` - Lista os baralhos compartilhados
- `GET /api/admin/shared-decks/{id}` - Baralho compartilhado com as fichas
- `POST /api/admin/shared-decks` - Cria (`title`, `description`, `cards: [{ question, answer }]`, de 1 a 1000 fichas)
- `PUT /api/admin/shared-decks/{id}` - Substitui título, descrição e fichas. As cópias já feitas não mudam
- `DELETE /api/admin/shared-decks/{id}` - Exclui. As cópias já feitas continuam com quem copiou

### Pedidos em andamento
- **Idioma das fichas**: baralhos devolvem e aceitam `questionLanguage` e `answerLanguage` (código como `en`, `es` ou `pt-BR`; nulo = português; no `PUT`, texto vazio volta para português). Também nos baralhos compartilhados e nas cópias ([`docs/tarefas/idioma-das-fichas.md`](docs/tarefas/idioma-das-fichas.md)).
- **Menos chamadas (parte 4 do modo sem login)** e **acertos e erros das fichas**: implementados juntos na branch `feat/estatisticas-das-fichas` ([`docs/tarefas/parte-4-menos-chamadas.md`](docs/tarefas/parte-4-menos-chamadas.md) e [`docs/tarefas/estatisticas-das-fichas.md`](docs/tarefas/estatisticas-das-fichas.md)). Precisam da migration `20261111000000_estatisticas_das_fichas.sql` do `cardvoz-db` aplicada antes de publicar.

---

## 🧪 Testes

```bash
dotnet test tests/cardvoz-api.Tests
```

Testes em xUnit com SQLite em memória (`tests/cardvoz-api.Tests/`; relacional como o PostgreSQL, sem precisar de servidor): revisão espaçada (`Card.RecordAnswer`), importação sem duplicar, sessão completa, exclusão e exportação da conta, confirmação de e-mail, junção com o Google e as regras de Google e e-mail. Rodam no GitHub Actions em todo PR, e o deploy só acontece se passarem. O projeto da API ignora a pasta `tests/` (não vai para a imagem Docker).

---

## 🤝 Para quem continua o projeto

- Leia o [`AGENTS.md`](AGENTS.md): arquitetura, regras de segurança, como mudar o banco e os combinados da equipe. Vale para quem usa ou não ferramentas de IA (o `CLAUDE.md` só aponta para ele).
- **Publicação:** push na `main` publica. O GitHub Actions constrói a imagem Docker e dispara o deploy no Render pelo secret `RENDER_DEPLOY_HOOK_URL`. Em pull request, só constrói. Trabalhe numa branch e abra PR.
- **Ordem de publicação** de uma mudança que mexe em tabela: migration no `cardvoz-db` → API → site.
- Mudança pedida por outra pessoa do grupo chega descrita em `docs/tarefas/`.

---

## 📄 Licença
Projeto acadêmico de extensão do IFTO, *Campus* Palmas.
