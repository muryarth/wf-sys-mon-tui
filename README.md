# wf-sys-mon-tui

**Workflow System Monitor** — monitor de sistema e de uso do GitHub Copilot em modo texto (TUI) para o terminal do Windows.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)
![C#](https://img.shields.io/badge/C%23-13-239120?logo=csharp)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

Mostra em tempo real os sensores do computador (CPU, memória, GPU, placa-mãe e discos) e, no mesmo painel, quanto do seu plano do GitHub Copilot já foi consumido, convertido em tokens dos modelos de IA mais recentes.

## Sumário

- [Funcionalidades](#funcionalidades)
- [Tecnologias e dependências](#tecnologias-e-dependências)
- [Requisitos](#requisitos)
- [Instalação](#instalação)
- [Uso](#uso)
- [Painel do GitHub Copilot](#painel-do-github-copilot)
- [Compilação manual](#compilação-manual)
- [Estrutura do projeto](#estrutura-do-projeto)
- [Solução de problemas](#solução-de-problemas)
- [Licença](#licença)

## Funcionalidades

**Hardware** (atualizado a cada 1 segundo)

| Painel | Informações |
|---|---|
| CPU | Temperatura do pacote, uso total, potência e, por núcleo, temperatura, uso e clock |
| Memória RAM | Em uso, disponível e percentual |
| GPU | Temperatura, uso, VRAM, ventoinha e potência (um painel por GPU) |
| Placa-mãe | Temperaturas e rotação das ventoinhas |
| Storage | Temperatura e espaço usado de cada disco |

**GitHub Copilot**

- Plano, créditos usados e restantes (em créditos e em US$) e data de renovação.
- Equivalente em tokens de entrada/saída para a versão mais recente de cada modelo Claude e GPT, com preços lidos da documentação oficial do GitHub.
- Login no GitHub feito dentro da própria TUI.

**Interface**

- Tela cheia que se reorganiza ao redimensionar o terminal: escolhe o número de colunas, equilibra a altura entre elas e estica os painéis até o fim da tela.
- Cores por painel, barras em gradiente e escala de cores para temperaturas.

## Tecnologias e dependências

| Item | Uso | Licença |
|---|---|---|
| [C#](https://learn.microsoft.com/dotnet/csharp/) / [.NET 10](https://dotnet.microsoft.com/) | Linguagem e runtime | MIT |
| [Spectre.Console](https://spectreconsole.net/) 0.49.1 | Renderização da TUI (painéis, tabelas, cores) | MIT |
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.4 | Leitura dos sensores de hardware | MPL-2.0 |
| [GitHub CLI](https://cli.github.com/) (`gh`) | Login no GitHub e armazenamento do token | MIT |
| [winget](https://learn.microsoft.com/windows/package-manager/) | Instalação automática das dependências | — |
| PowerShell 5.1+ | Script inicializador `wf-sys-mon.ps1` | — |

## Requisitos

- Windows 10 ou 11, x64, com **winget** (já incluído no Windows 11).
- **Permissão de administrador**, exigida pelo driver de sensores. O app pede pelo UAC automaticamente.
- Uma conta do GitHub com Copilot, apenas para o painel do Copilot.

O .NET 10 e o GitHub CLI não precisam estar instalados: o inicializador instala o que faltar.

## Instalação

```powershell
git clone https://github.com/muryarth/wf-sys-mon-tui.git
cd wf-sys-mon-tui
.\wf-sys-mon.cmd -Install
```

O `-Install`:

1. Instala via winget o que faltar: .NET 10 SDK e GitHub CLI.
2. Compila o app em `out\`.
3. Cria o atalho `%USERPROFILE%\bin\wf-sys-mon.cmd` e adiciona essa pasta ao `PATH` do usuário.
4. Oferece habilitar o [sudo do Windows](https://learn.microsoft.com/windows/sudo/) em modo *Inline*, para a TUI abrir no mesmo terminal (pede confirmação e UAC).

Depois, abra **um novo terminal** para o comando `wf-sys-mon` ficar disponível.

> O atalho aponta para a pasta onde o `-Install` foi executado. Se mover ou renomear a pasta, rode `.\wf-sys-mon.cmd -Install` de novo.

### zsh / bash (MSYS2, Git Bash)

O atalho funciona em cmd e PowerShell. Em shells Unix-like, adicione um alias ao `~/.zshrc` ou `~/.bashrc`, ajustando o caminho:

```sh
alias wf-sys-mon='powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:/caminho/wf-sys-mon-tui/wf-sys-mon.ps1'
```

## Uso

```cmd
wf-sys-mon
```

Aceite o pedido de administrador. A TUI abre **no próprio terminal** quando o [sudo do Windows](https://learn.microsoft.com/windows/sudo/) está em modo *Inline*, o que o `-Install` oferece configurar, ou quando o terminal já está aberto como administrador. Caso contrário, o UAC abre a TUI numa nova janela.

> O modo *Inline* do sudo exige Windows 11 24H2 ou superior e vale para o sistema todo: comandos com `sudo` passam a rodar elevados no terminal atual. Para ativá-lo manualmente: *Configurações → Sistema → Para desenvolvedores → sudo*, modo **Embutido**, ou `sudo config --enable normal` num terminal de administrador.

| Tecla | Ação |
|---|---|
| `Q` ou `Esc` | Sair |
| `R` | Atualizar os dados do Copilot agora |
| `L` | Entrar no GitHub (quando não há conta conectada) |

**Opções do inicializador**

| Comando | Efeito |
|---|---|
| `wf-sys-mon` | Executa; recompila sozinho se o código em `src\` mudou |
| `wf-sys-mon -Rebuild` | Força a recompilação antes de executar |
| `wf-sys-mon -Install` | Instala dependências, compila e registra o comando |

## Painel do GitHub Copilot

### Primeiro acesso

1. Na TUI, pressione `L`.
2. O navegador abre em `github.com/login/device` e o código aparece na tela (também copiado para a área de transferência).
3. Digite o código e autorize. O painel carrega sozinho.

### Onde fica o token

O `wf-sys-mon` não grava credenciais. Quem guarda o token é o GitHub CLI, no **Gerenciador de Credenciais do Windows** (entrada `gh:github.com:<usuário>`). O app obtém o token, nesta ordem, de:

1. variável de ambiente `GH_TOKEN`;
2. variável de ambiente `GITHUB_TOKEN`;
3. `gh auth token`.

Para desconectar: `gh auth logout`.

### Atualização e limites

Os dados são atualizados a cada 1 minuto (ou com `R`). São 60 requisições por hora, contra o limite de 5.000 por hora da API do GitHub. Se a API indicar limite excedido (403/429), o app espera o tempo informado pelo GitHub antes de tentar de novo.

### Créditos e tokens

1 crédito de IA = US$ 0,01. O equivalente em tokens é calculado a partir dos [preços por 1M de tokens](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing) da documentação do GitHub, filtrados para mostrar só a versão mais recente de cada família (Claude Opus, Sonnet, Haiku...; GPT base, mini, nano, Codex...). Assim, modelos novos aparecem sem precisar alterar o código.

A tabela de preços fica em cache por 24 horas em `%LOCALAPPDATA%\wf-sys-mon\models-cache.json`. Sem internet, o app usa uma lista embutida de reserva.

> **Aviso:** os dados de uso vêm do endpoint **não documentado** `api.github.com/copilot_internal/user`, o mesmo usado pela extensão do Copilot no VS Code. Ele pode mudar ou deixar de funcionar sem aviso.

## Compilação manual

Com o [.NET 10 SDK](https://dotnet.microsoft.com/download) instalado:

```powershell
dotnet publish -c Release -o out
```

Para executar sem o inicializador:

```powershell
dotnet out\WfSysMon.dll   # ou abra out\WfSysMon.exe
```

O app é *framework-dependent*: precisa do .NET 10 Runtime na máquina.

## Estrutura do projeto

```
WfSysMon.csproj     projeto .NET 10 (win-x64)
app.manifest        manifesto do Windows
wf-sys-mon.ps1      inicializador: dependências, compilação e execução
wf-sys-mon.cmd      atalho para o wf-sys-mon.ps1
src/
  Program.cs        entrada, elevação UAC, loop principal e teclado
  Screen.cs         renderização em tela cheia ajustada ao tamanho do terminal
  Dashboard.cs      layout, painéis da TUI e paleta de cores (classe Theme)
  CopilotUsage.cs   consulta do plano e dos créditos do Copilot
  ModelCatalog.cs   lista de modelos e preços (docs.github.com, com cache)
  GitHubAuth.cs     login no GitHub dentro da TUI (via GitHub CLI)
```

## Solução de problemas

| Problema | Solução |
|---|---|
| `wf-sys-mon` não é reconhecido | Abra um novo terminal após o `-Install`. Confira se `%USERPROFILE%\bin` está no `PATH`. |
| "O sistema não pode encontrar o caminho especificado" | A pasta do projeto foi movida ou renomeada. Rode `.\wf-sys-mon.cmd -Install` na nova pasta. |
| Erro `0x800711C7` ao abrir | O **Smart App Control** bloqueou a DLL recompilada (ele libera arquivos pelo hash). Desative-o em *Segurança do Windows → Controle de aplicativos e navegador*. |
| A TUI abre numa janela separada | O sudo não está em modo *Inline*. Rode `wf-sys-mon -Install` e aceite habilitá-lo. No Windows 10 ou Windows 11 anterior ao 24H2 (sem sudo), abra o terminal como administrador. |
| Temperaturas aparecem vazias | O app não está rodando como administrador. |
| Falha ao recompilar | O app ainda está aberto e bloqueia os arquivos em `out\`. Feche-o e tente de novo. |
| Painel do Copilot com "conta sem Copilot" | A conta conectada não tem assinatura do Copilot. |
| Scripts bloqueados pela política de execução | Use `wf-sys-mon.cmd`, que já roda o script com `-ExecutionPolicy Bypass`. |

## Licença

Distribuído sob a licença [MIT](LICENSE). As dependências mantêm suas próprias licenças (Spectre.Console: MIT; LibreHardwareMonitorLib: MPL-2.0).