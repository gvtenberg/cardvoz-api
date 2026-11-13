# Tarefa: abrir o painel Admin sem entrar de novo

**Banco:** nada muda (sem migration).
**Site:** versão 0.14.0 (botão "Abrir painel Admin" no Perfil). **Painel:** `cardvoz-admin` 0.2.0.
**Situação (12/11/2026):** implementada na branch `feat/passe-do-painel`, combinada com o grupo.

## Por quê

O site (`cardvoz.onrender.com`) e o painel (`cardvoz-admin.onrender.com`) são endereços diferentes: um não lê a sessão guardada pelo outro. Quem é admin e acabou de entrar no site precisava entrar de novo no painel.

## Como funciona

1. No Perfil do site, "Abrir painel Admin" chama `POST /api/auth/admin-pass` (com login, só admin; `[AdminOnly]` confere no banco).
2. A API devolve um código de 256 bits, de uso único, que vale 1 minuto. Guarda só o hash, na memória (`AdminPassService`, singleton).
3. O site abre `cardvoz-admin/entrar/#passe=<código>`. O código vai depois do `#`: o navegador não o manda a servidor nenhum, e o painel o tira da barra de endereço na hora.
4. O painel chama `POST /api/auth/admin-pass/redeem` `{ code }`. A API apaga o passe, confere de novo se a conta ainda é admin e devolve tokens novos (sessão própria do painel, que não mexe na sessão do site).
5. Passe usado, vencido ou perdido (a API reiniciou): `400`, e o painel mostra o login normal com o aviso.

## Decisões

- Sem tabela no banco: o passe dura 1 minuto, e perdê-lo num reinício só faz a pessoa entrar como antes. Se a API passar a rodar com mais de uma cópia, o passe precisa ir para o banco.
- Funciona para quem entrou com senha ou com o Google: o passe vem da sessão, não da forma de entrar.
- `redeem` usa o limite de 10/min por IP (`Auth`), como o login com senha.
