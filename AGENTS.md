# CardVoz API — Guia de referência para pessoas e agentes de IA

Leia este arquivo inteiro antes de alterar a API. Ele vale para qualquer pessoa do grupo e para qualquer ferramenta (Claude Code, Cursor, Copilot, Codex etc.). O `CLAUDE.md` só aponta para cá. Como rodar, variáveis de ambiente e a lista de rotas estão no [`README.md`](README.md).

O CardVoz é um app de **fichas de estudo (flashcards) para pessoas cegas e com baixa visão**, que estudam com o leitor de tela do aparelho (TalkBack, NVDA). Esta API guarda as contas, os baralhos, as fichas e o histórico de estudo de quem tem conta.

---

## 1. Regras inegociáveis

1. **Cada pessoa só acessa o que é dela.** Toda consulta de baralho, ficha ou sessão filtra por `UserId` da pessoa logada (`User.GetUserId()`). Recurso de outra pessoa responde **404**, nunca 403 (não revela que existe).
2. **Todas as rotas exigem login** (`[Authorize]`), exceto as de `/api/auth` (menos `/me`). Rotas de admin usam `[AdminOnly]`, que confere `IsAdmin` no banco a cada chamada.
3. **A API não cria nem altera tabelas.** O esquema mora no `cardvoz-db`. Mudança de tabela ou coluna começa lá, numa migration nova, e o modelo do Entity Framework daqui precisa bater com ela (nomes em PascalCase: `"Decks"`, `"Cards"`, `"StudySessions"`, `"StudyReviews"`, `"Users"`, `"RefreshTokens"`).
4. **Todos os IDs são UUID v4.** Nada de IDs numéricos ou sequenciais.
5. **Segredos fora do repositório.** `JWT_SIGNING_KEY`, `GOOGLE_CLIENT_SECRET` e a conexão do banco ficam no `.env` (local) e nas variáveis do Render. Nunca registre token, senha ou hash em log ou em mensagem de erro.
6. **Commits sem atribuição a IA.** Sem `Co-Authored-By`, "Generated with…" ou menção a IA em commits e PRs.
7. **Push na `main` publica em produção** (GitHub Actions → Render). Trabalhe numa branch e abra PR; o build da imagem Docker precisa passar.

---

## 2. O sistema (4 repositórios)

| Repositório | Papel | Hospedagem |
|---|---|---|
| `cardvoz` | Site Next.js estático. Funciona sem conta (dados no aparelho); com conta, envia as mudanças para esta API por uma fila | Render — Static Site |
| **`cardvoz-api`** (este) | API REST em C# (.NET 9) | Render — Web Service (Docker) |
| `cardvoz-db` | Esquema do banco, como migrations do Supabase CLI | Supabase (PostgreSQL) |
| `cardvoz-admin` | Painel da equipe: usuários e baralhos compartilhados (`/api/admin/*`) | Render — Static Site |

- O site libera o domínio na API por `CORS_ALLOWED_ORIGINS` (sem barra no final).
- **Ordem de publicação** de mudança que mexe em tabela: migration no `cardvoz-db` → API → site. O site chama rotas que precisam existir.
- O site trata 404/405 de rota nova como "rota ainda não existe" e volta ao caminho antigo. Por isso uma rota nova pode ser publicada antes de o site usá-la.

---

## 3. Arquitetura

```
Auth/          JWT, Google, limite por IP (RateLimitPolicies), [AdminOnly], GetUserId()
Controllers/   Rotas REST finas: validam a entrada e chamam o serviço
DTOs/          Request/Response por assunto (Auth, Decks, Cards, StudySessions, Admin)
Models/        Entidades do EF Core
Data/          CardVozDbContext (mapeamento fluente) e conexão com o Supabase
Services/      Regras de negócio, cada uma com interface (IDeckService...)
Program.cs     Injeção de dependências, autenticação, CORS, limites e Swagger
```

- Controller não acessa o `DbContext`: chama o serviço.
- DTOs saem em camelCase no JSON; o site espelha esses nomes em `src/lib/api.ts`. Renomear campo quebra o site: combine antes.
- Comentários `/// <summary>` em português, explicando o porquê.

---

## 4. Login e sessão

- Token de acesso JWT de 15 minutos (HS256, algoritmo fixo) e token de renovação de 30 dias, aleatório (256 bits), guardado só como hash SHA-256.
- A renovação troca o token (rotação). Token já trocado reapresentado depois de 30 segundos é tratado como roubo e encerra todas as sessões da pessoa. O site garante uma renovação por vez, inclusive entre abas.
- Senha com hash PBKDF2-SHA512 (100 mil iterações, com salt). 5 erros seguidos bloqueiam por 15 minutos.
- **Não junte contas:** e-mail do Google que já tem conta com senha responde 409. O cadastro com senha não confirma o e-mail; juntar deixaria quem cadastrou o e-mail de outra pessoa entrar na conta dela.
- Limite por IP: 10/min em login e cadastro com senha; 60/min em Google, renovação e logout (rede da escola divide o mesmo IP).

---

## 5. Fluxo de trabalho

```bash
dotnet restore
dotnet build
dotnet run          # http://localhost:5000/swagger
docker build -t cardvoz-api .   # o mesmo build do GitHub Actions
```

- **Antes de concluir:** `dotnet build` sem erros, rota conferida no Swagger (com e sem login, e com recurso de outra pessoa → 404) e `docker build` passando.
- **Commits:** Conventional Commits em português (`feat:`, `fix:`, `docs:`, `refactor:`), sem atribuição a IA.
- **Documentação em toda mudança:** atualize o `README.md` (rotas, variáveis, erros) e este arquivo. Rota nova que o site vai usar precisa da seção "Como o frontend deve usar" atualizada.
- **Tarefas de outras pessoas** chegam descritas em `docs/tarefas/`. Ao concluir uma, apague o "A fazer" do README e diga no PR.

---

## 6. Combinados da equipe

Estas regras vieram de conversas da equipe com assistentes de IA e valem para todo mundo, use ou não uma ferramenta de IA. Os `AGENTS.md` do `cardvoz` e do `cardvoz-db` têm a mesma seção, adaptada a cada repositório.

- **Português do Brasil** em respostas, comentários, commits, PRs e documentação. Quando a pessoa disser que entendeu, siga para o próximo passo sem repetir a explicação.
- **Quem usa o CardVoz:** a cliente é cega e estuda com o TalkBack; o público inclui pessoas com baixa visão. Mensagens de erro da API podem aparecer na tela e ser lidas pelo leitor de tela: escreva em português, curtas e dizendo o que fazer.
- **Trabalho dividido:** este repositório tem dono no grupo (`fiuzaheitor`, repositório `upstream`). Mudança de código aqui só depois de combinar com ele; quem precisa de algo descreve a tarefa em `docs/tarefas/` e abre PR para ele. Correções só de documentação podem ir direto em PR.
- **Problemas** viram issue no GitHub do repositório certo. O commit que corrige cita a issue (`Closes #10`), e ela fecha sozinha quando o PR entra na `main`.
- **Pedidos da cliente em andamento (out/2026):** guardar o idioma das fichas ([`docs/tarefas/idioma-das-fichas.md`](docs/tarefas/idioma-das-fichas.md)) e as rotas da parte 4 do modo sem login (`docs/tarefas/parte-4-menos-chamadas.md`, na branch `docs/parte-4-menos-chamadas`).
