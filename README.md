# Claude Pulse

App de bandeja para Windows 11 (C# / WinForms, .NET 8) que mostra, num clique, quanto do plano do Claude você já usou e quanto ainda tem de janela de contexto em cada sessão do Claude Code.

> **Projeto não oficial**, sem vínculo com a Anthropic. "Claude" e "Claude Code" são marcas da Anthropic.

<!-- Print do painel: salve em docs/painel.png e descomente a linha abaixo -->
<!-- ![Painel do Claude Pulse](docs/painel.png) -->

## Começando

**Pré-requisitos:** Windows 10/11, [Claude Code](https://docs.claude.com/en/docs/claude-code) instalado e logado (`claude` e depois `/login`) e o [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

**Opção 1: baixar pronto.** Pegue o `ClaudePulse.exe` na página de [Releases](https://github.com/viniciusvacare/claude-pulse/releases) e execute.

**Opção 2: compilar** (requer o .NET SDK 8+):

```powershell
git clone https://github.com/viniciusvacare/claude-pulse.git
cd claude-pulse
dotnet publish src/ClaudePulse/ClaudePulse.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
.\publish\ClaudePulse.exe
```

O ícone aparece na bandeja, ao lado do relógio. Clique nele para abrir o painel. Também dá para abrir `ClaudePulse.sln` no Visual Studio 2022.

## O que ele mostra

**Ícone na bandeja:** um número colorido (verde < 60%, âmbar < 85%, vermelho ≥ 85%) que se atualiza sozinho. Pelo menu, escolha se o ícone mostra a sessão de 5h, o limite semanal ou o contexto da sessão mais recente. Há também um estilo com duas barras.

**Painel** (clique com o botão esquerdo no ícone):

| Seção | Conteúdo |
|---|---|
| Limites do plano | Sessão de 5h, semana (todos os modelos) e, quando existirem, Opus/Sonnet semanal e créditos extras. Cada barra tem um **marcador de ritmo** (onde você estaria gastando de forma uniforme) e uma previsão: "⚠ esgota ~15:10" ou "projeção ~40%" no reset. |
| Uso da semana por app | Quanto do limite semanal veio do Claude Code, dos chats etc. |
| Janela de contexto | Até 4 sessões do Claude Code das últimas 24h (Windows e WSL): tokens no contexto / limite do modelo, quanto ainda está livre, modelo, branch e há quanto tempo houve atividade. **Clique numa sessão** para copiar o comando que a retoma: entra na pasta do projeto e roda `claude --resume <id>` (em PowerShell para sessões do Windows, em bash para as do WSL). |
| Hoje | Respostas, tokens gerados, custo equivalente na API (estimativa) e taxa de acerto de cache. |

**Histórico de uso** (ícone de calendário no painel, clique no card "Hoje" ou *menu → Histórico de uso…*):

- Navegue mês a mês e veja o resumo do mês: tokens totais, tokens gerados, custo equivalente na API e dias com uso.
- Um gráfico de barras por dia, com três métricas: tokens, saída ou custo. Passe o mouse para ver os valores e clique num dia para selecioná-lo.
- O detalhe do dia mostra entrada, cache lido, cache escrito, saída, custo, o pico dos limites (5h e semana) e a divisão por modelo e por projeto.
- **Exportar CSV do mês** gera um arquivo com separador `;` e vírgula decimal, que abre direto no Excel pt-BR.
- Atalhos: ←/→ trocam o dia, PageUp/PageDown trocam o mês, Home volta para hoje e Esc fecha.

O histórico é **separado por conta**. O seletor no topo da janela alterna entre as contas e "Todas as contas", e o CSV inclui uma coluna `conta`.

O Claude Code apaga transcripts antigos (por padrão, após ~30 dias). Por isso o Claude Pulse guarda os totais diários em `%APPDATA%\ClaudePulse\history.json`. Na primeira execução ele importa tudo o que ainda existe nos transcripts e depois atualiza o histórico em tempo real. Um dia já registrado nunca é rebaixado quando seus transcripts somem. Os picos de limite só são registrados enquanto o app está aberto.

**Notificações:** ao passar de 75%, 90% e 100% de um limite, quando uma janela reinicia e quando o contexto de uma sessão ativa passa de 80% (sugerindo `/compact`).

## Contas

O app não tem login próprio: ele usa o login do Claude Code.

- **Onde ele procura:** o token fica em `.claude\.credentials.json` e a conta (e-mail e organização) em `.claude.json`. Ele procura no Windows e nas distros WSL em execução.
- **De quem é cada login:** a conta é confirmada pelo próprio token, via `GET /api/oauth/profile` (uma consulta por token novo). O `.claude.json` não basta: o app desktop grava nele a conta dele, enquanto o `.credentials.json` continua com o login da CLI, que pode ser de outra organização ou plano. Quando os dois não batem, o painel mostra um aviso e o menu *Conta* marca o login com "⚠ difere do .claude.json". Sem resposta da API, vale o `.claude.json`. Os limites e o card "Hoje" seguem a conta do token; no histórico, cada resposta vai para a conta de quem a gerou (veja abaixo).
- **Qual conta ele usa:** em *menu → Conta → Automática*, usa o login válido mais recente. Também dá para **fixar uma conta**: se houver logins diferentes no Windows e no WSL, o app fica sempre naquela.
- **Para trocar de conta:** rode `/logout` e depois `/login` no Claude Code. O app percebe a troca em segundos. Se a conta fixada deixar de estar logada, o painel avisa em vez de cair para outra conta.
- **Como o histórico sabe de qual conta é cada resposta:** os transcripts não dizem qual conta respondeu, mas dizem quem respondeu (o campo `entrypoint`). O app guarda duas linhas do tempo por ambiente (Windows / WSL): a conta do `.claude.json`, que é a do app desktop, e a conta do token do `.credentials.json`, que é a da CLI e do VS Code. Respostas do app desktop (`claude-desktop`) vão para a primeira; as da CLI e do VS Code, para a segunda. Cada resposta fica com a conta ativa naquele ambiente no momento dela.
  - A hora da troca é estimada pela última gravação do `.credentials.json`.
  - A linha do tempo do token só vale a partir de quando o app passou a registrá-la. Respostas anteriores e as de transcripts sem `entrypoint` ficam com a conta do `.claude.json`.
  - O histórico anterior às contas fica com a primeira conta observada.

## Como funciona

- **Login:** somente leitura. O app nunca grava os arquivos do Claude Code nem renova o token por conta própria.
- **Renovar login:** quando o token expira ou é recusado, o painel mostra o botão **Renovar**. Ele roda a CLI do Claude Code em segundo plano (`claude -p ok --model haiku --no-session-persistence --strict-mcp-config`), e é a própria CLI que renova e regrava o `.credentials.json`, sem disputar o refresh token com sessões abertas. Custa uma resposta mínima do Haiku e não gera transcript. A CLI é procurada no PATH, em `~\.local\bin` e em `%APPDATA%\npm`, e o Git Bash que ela exige no Windows é localizado automaticamente (PATH, `Program Files\Git`, `%LOCALAPPDATA%\Programs\Git` ou scoop) quando `CLAUDE_CODE_GIT_BASH_PATH` não está definida; para logins do WSL, o comando roda dentro da distro.
  - Isso é necessário porque o Claude Code do app desktop faz o login por conta própria e não atualiza o `.credentials.json`: quem só usa o app desktop vê o token expirar algumas horas depois do último uso da CLI.
- **Limites:** consulta `GET https://api.anthropic.com/api/oauth/usage`, o mesmo endpoint do `/usage`, a cada 2 minutos (configurável) e respeita `Retry-After` em caso de 429.
- **Contexto e estatísticas:** lê os transcripts `~/.claude/projects/**/*.jsonl` de forma incremental, processando apenas os bytes novos. O tamanho do contexto é `input + cache_read + cache_creation + output` da última resposta da sessão principal.
- **WSL:** só lê distros que já estão rodando (`wsl --list --running`), para nunca iniciar uma distro parada.
- **Configurações:** `%APPDATA%\ClaudePulse\settings.json`. "Iniciar com o Windows" grava em `HKCU\...\Run`.

> O endpoint `/api/oauth/usage` não é uma API pública documentada e pode mudar. O custo "≈ API" é só uma referência, calculado com a tabela de preços em `Core/Models.cs`. Assinantes Pro/Max não pagam por token.

## Compilar

Requer o .NET 8 Desktop Runtime para rodar e o .NET SDK 8+ para compilar.

```powershell
# Windows
dotnet publish src/ClaudePulse/ClaudePulse.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

```bash
# Também compila de dentro do WSL/Linux (EnableWindowsTargeting já está no .csproj)
dotnet publish src/ClaudePulse/ClaudePulse.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

O resultado é `publish/ClaudePulse.exe`, com cerca de 250 KB. Para um exe que não depende do runtime instalado, use `--self-contained true`; ele fica com dezenas de MB.

Dicas:
- Para fixar o ícone ao lado do relógio: *Configurações → Personalização → Barra de tarefas → Outros ícones da bandeja do sistema → Claude Pulse*.
- Abrir o `.exe` com o app já rodando apenas abre o painel. Dá para fixá-lo no Iniciar e usá-lo como atalho.
- `ClaudePulse.exe --show` abre o painel ao iniciar; `--history` abre o histórico (também funciona com o app já rodando).
- No painel, `F5` atualiza e `Esc` fecha.

## Estrutura

```
src/ClaudePulse/
├── Program.cs              instância única + sinal "mostrar painel"
├── TrayApp.cs              ícone, timers, menu, notificações
├── Core/
│   ├── ClaudeHome.cs       localiza ~/.claude no Windows e no WSL
│   ├── Credentials.cs      lê o token OAuth do Claude Code
│   ├── CliRenewer.cs       pede para a CLI do Claude Code renovar o login
│   ├── TokenAccounts.cs    confirma pelo token (/api/oauth/profile) de qual conta é cada login
│   ├── UsageApi.cs         /api/oauth/usage e /api/oauth/profile, ritmo e projeção
│   ├── SessionTracker.cs   leitura incremental dos transcripts .jsonl, totais por dia/modelo/projeto
│   ├── UsageHistory.cs     histórico diário persistido + exportação CSV
│   ├── Models.cs           preços, janelas de contexto, nomes de modelos
│   ├── Settings.cs         configurações + iniciar com o Windows
│   └── JsonExtensions.cs
└── UI/
    ├── PopupForm.cs        painel flutuante desenhado em GDI+ (tema claro/escuro, DPI por monitor)
    ├── HistoryForm.cs      janela de histórico mensal (gráfico diário + detalhe do dia)
    ├── Gfx.cs              primitivas de desenho e paleta de dados
    ├── IconRenderer.cs     ícone dinâmico da bandeja
    ├── Theme.cs            paleta e cores de severidade
    └── Format.cs           formatação pt-BR
```

## Licença

[MIT](LICENSE)
