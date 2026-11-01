# wf-sys-mon-tui

**Workflow System Monitor** — monitor de sistema e de uso do GitHub Copilot e do Claude em modo texto (TUI) para o terminal do Windows.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)
![C#](https://img.shields.io/badge/C%23-13-239120?logo=csharp)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

Mostra em tempo real os sensores do computador (CPU, memória, GPU, placa-mãe e discos) e, no mesmo painel, quanto dos seus planos do GitHub Copilot e do Claude já foi consumido, em tokens e US$.

> **Projeto independente.** Não é oficial nem afiliado ao GitHub, à Microsoft ou à Anthropic. GitHub Copilot e Claude são marcas de seus respectivos donos.

## Sumário

- [Funcionalidades](#funcionalidades)
- [Tecnologias e dependências](#tecnologias-e-dependências)
- [Requisitos](#requisitos)
- [Instalação](#instalação)
- [Uso](#uso)
- [Serviço de sensores](#serviço-de-sensores)
- [Painel do GitHub Copilot](#painel-do-github-copilot)
- [Painel do Claude](#painel-do-claude)
- [Compilação manual](#compilação-manual)
- [Estrutura do projeto](#estrutura-do-projeto)
- [Solução de problemas](#solução-de-problemas)
- [Licença](#licença)

## Funcionalidades

**Hardware** (lido a cada 1 segundo; por padrão, os números mudam a cada 2 segundos, com a média das 3 últimas leituras, e as barras deslizam até o novo valor)

| Painel | Informações |
|---|---|
| CPU | Temperatura geral em destaque, uso total, potência e, por núcleo, temperatura, uso e clock |
| Memória RAM | Em uso, disponível, total e percentual |
| GPU | Temperatura geral em destaque, uso, VRAM (em uso, disponível e total, com alerta por cor), ventoinha e potência (um painel por GPU) |
| Placa-mãe | Temperaturas e rotação das ventoinhas |
| Armazenamento | Temperatura e espaço em uso, disponível e total de cada disco (somando os volumes com letra) |

As temperaturas gerais do CPU e da GPU também ficam no topo da tela. Os valores de mínimo e máximo são as leituras brutas desde que o app abriu; por núcleo, aparecem em colunas com 60 caracteres ou mais.

**GitHub Copilot**

- Plano com preço mensal, valor gasto, cota do plano, extra budget e restante em US$, créditos (AIC) usados e disponíveis, e data de renovação.
- Cor dos valores em US$ por faixa do total (plano + extra budget): verde até 33,33%, laranja até 66,66%, vermelho acima disso.
- Tokens reais do ciclo usados pelo Copilot CLI neste PC, com a parcela do valor gasto que eles representam.
- Fechamento do ciclo anterior, guardado localmente porque a API só informa o ciclo atual.
- Login no GitHub feito dentro da própria TUI.

**Claude**

- Plano com preço mensal.
- Cota de 5 horas e de 7 dias do plano, com horário de renovação, idade do dado e estimativa de tokens restantes.
- Tokens e custo estimado em US$ acumulados no mês pelo Claude Code neste PC, por modelo.

**Interface**

- Seções separadas de **Hardware** e **Inteligência Artificial**, que podem ser recolhidas; o cabeçalho continua visível para abrir de novo.
- Layout responsivo: com pouco espaço horizontal, uma coluna só, usando toda a largura; com espaço, uma coluna por seção, de tamanhos iguais, limitadas a 110 caracteres e centralizadas.
- Rolagem independente por coluna, com barra de rolagem, quando falta espaço na tela.
- Menu clicável, com atalho de teclado para cada ação.
- Abas **Monitoramento**, **Contas** e **Ajustes**. Inglês (padrão) e português (Brasil), escolhidos no seletor do menu ou alternados com `L`.
- Ícones em ASCII em cada painel, que podem ser desligados em Ajustes.
- "Em uso", "Disponível" e "Total" lado a lado para RAM, VRAM e discos, com a cor do uso seguindo o alerta da barra.

## Tecnologias e dependências

| Item | Uso | Licença |
|---|---|---|
| [C#](https://learn.microsoft.com/dotnet/csharp/) / [.NET 10](https://dotnet.microsoft.com/) | Linguagem e runtime | MIT |
| [Spectre.Console](https://spectreconsole.net/) 0.49.1 | Renderização da TUI (painéis, tabelas, cores) | MIT |
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6 | Leitura dos sensores de hardware | MPL-2.0 |
| [PawnIO](https://github.com/namazso/PawnIO) | Driver de sensores, instalado à parte pelo winget | GPL-2.0 |
| [GitHub CLI](https://cli.github.com/) (`gh`) | Login no GitHub e armazenamento do token | MIT |
| [winget](https://learn.microsoft.com/windows/package-manager/) | Instalação automática das dependências | — |
| PowerShell 5.1+ | Script inicializador `wf-sys-mon.ps1` | — |

## Requisitos

- Windows 10 ou 11, x64, com **winget** (já incluído no Windows 11).
- **Permissão de administrador uma vez**, para instalar o serviço de sensores. Depois, o app abre sem pedir nada. Sem o serviço, ele roda em modo limitado.
- Uma conta do GitHub com Copilot, apenas para o painel do Copilot.

O .NET 10 e o GitHub CLI não precisam estar instalados: o inicializador instala o que faltar. O driver PawnIO é oferecido no `-Install` e na aba Contas, sempre com confirmação.

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
4. Configura a status line do Claude Code em `~/.claude/settings.json`, sem sobrescrever uma que já exista.
5. Oferece instalar o driver PawnIO, usado para ler os sensores.
6. Instala o serviço de sensores. O Windows confirma pelo UAC, só desta vez.

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

A TUI abre no próprio terminal, como usuário comum e sem pedir permissão, porque os sensores vêm do [serviço](#serviço-de-sensores). Sem o serviço, ela explica o motivo e oferece instalá-lo, abrir como administrador só daquela vez ou continuar em modo limitado.

| Tecla | Ação |
|---|---|
| `1` / `2` | Recolher ou abrir a seção Hardware / IA |
| `R` | Atualizar os dados de IA agora: consulta o Copilot e relê a cota, os logs e o plano do Claude |
| `←` `→` | Trocar entre as abas Monitoramento, Contas e Ajustes |
| `A` | Abrir a aba Contas |
| `S` | Abrir a aba Ajustes |
| `L` | Alternar entre português e inglês |
| `Tab` | Trocar a coluna ativa |
| `↑` `↓` `PgUp` `PgDn` `Home` `End` | Rolar a coluna ativa (a roda do mouse rola a coluna sob o ponteiro) |
| `Q` ou `Esc` | Sair (nas telas de Contas e Ajustes, `Esc` volta) |

As abas, o menu e os títulos das seções também respondem ao clique.

### Contas e permissões

Na primeira abertura, se faltar algo, a aba **Contas** abre sozinha. Use `↑` `↓` para escolher uma conta e `Enter` para entrar ou sair; com o mouse, o primeiro clique seleciona e o segundo executa.

| Item | O que faz |
|---|---|
| Driver de sensores | Mostra se o PawnIO está instalado e permite instalar com confirmação. Reabra o app depois. Para remover: `winget uninstall namazso.PawnIO`. |
| Serviço de sensores | Instala, atualiza ou remove o serviço. Sem ele, o app continua em modo limitado, sem temperatura, potência e discos. |
| GitHub Copilot | Faz o login no navegador, com um código de uso único. Se o GitHub CLI não estiver instalado, pede confirmação antes de instalar. Sair remove o login do GitHub CLI neste PC, o que também afeta outras ferramentas que usam o `gh`; o token não é revogado no GitHub. |
| Claude Code | Abre o login do próprio Claude Code em outra janela e detecta quando ele termina. Sair usa `claude auth logout` e interrompe as sessões abertas até um novo login. |
| Cota do Claude | Configura a status line depois de pedir confirmação. |

### Ajustes

Cada ajuste mostra todas as opções lado a lado, com a atual em destaque. Clique em uma opção para escolher, ou use `↑` `↓` para ir até o ajuste e `Enter` para passar à próxima opção.

### Tamanhos de tela

O layout não depende de uma resolução fixa: ele decide pelo tamanho da janela, em células do terminal. Foi ajustado para cerca de 286×58 (ultrawide 3440×1440) e 90×81 (vertical 1080×1920), com fonte de cerca de 12×23 px. Janelas menores continuam funcionando, com rolagem.

Nos painéis, `~` indica estimativa: o valor do mês no Claude é tokens × preço de lista da API, e o que resta em cada janela vem da proporção entre o percentual e o uso deste PC.

**Opções do inicializador**

| Comando | Efeito |
|---|---|
| `wf-sys-mon` | Executa; recompila sozinho se o código em `src\` mudou |
| `wf-sys-mon -Rebuild` | Força a recompilação antes de executar |
| `wf-sys-mon -Install` | Instala dependências, compila, registra o comando e instala o serviço |
| `wf-sys-mon -Uninstall` | Remove o serviço de sensores |

## Serviço de sensores

Temperatura, potência e discos exigem privilégio de administrador. Em vez de rodar o app inteiro assim, só um pequeno serviço do Windows (`WfSysMonSensors`) tem esse privilégio:

- **Fica em `Program Files\wf-sys-mon`**, onde programas comuns não escrevem.
- **Só lê sensores e envia os valores**, por um canal de mão única (pipe local). Não há comando que a TUI possa mandar a ele.
- **Só atende a conta do Windows que o instalou.** A TUI também confere se quem responde é mesmo um processo privilegiado.
- **Só lê enquanto há uma janela aberta.** Sem janelas, fica ocioso e fecha o acesso aos sensores.

Instalar, atualizar e remover pedem confirmação do Windows. Para isso, use a aba Contas, ou `wf-sys-mon -Install` e `wf-sys-mon -Uninstall`.

**Depois de atualizar o código:** o serviço roda da cópia em `Program Files`, não de `out\`. Se o formato dos dados mudar, a aba Contas mostra "Outdated"; use a mesma aba para atualizar.

## Painel do GitHub Copilot

### Primeiro acesso

1. Na TUI, pressione `A` e escolha **GitHub Copilot**.
2. O navegador abre em `github.com/login/device` e o código aparece na tela.
3. Digite o código e autorize. O painel carrega sozinho.

### Onde fica o token

O `wf-sys-mon` não grava credenciais. Quem guarda o token é o GitHub CLI, no **Gerenciador de Credenciais do Windows** (entrada `gh:github.com:<usuário>`). O app obtém o token, nesta ordem, de:

1. variável de ambiente `GH_TOKEN`;
2. variável de ambiente `GITHUB_TOKEN`;
3. `gh auth token`.

Para desconectar: `gh auth logout`.

### Atualização e limites

Os dados são atualizados a cada 30 segundos, por padrão (ou com `R`; o intervalo muda em Ajustes). São 120 requisições por hora, contra o limite de 5.000 por hora da API do GitHub. Se a API indicar limite excedido (403/429), o app espera o tempo informado pelo GitHub antes de tentar de novo.

### Créditos e tokens

1 crédito de IA (AIC) = US$ 0,01. O valor gasto vem da API e cobre todas as superfícies do Copilot na conta. O ciclo vira às 00:00 UTC da data de renovação (21:00 do dia anterior no horário de Brasília).

A API só informa o ciclo atual, então o app guarda a última leitura de cada ciclo em `%LOCALAPPDATA%\wf-sys-mon\copilot-cycles.json` e mostra o anterior na linha "Last cycle". É a última leitura feita com o app aberto: uso depois dela, no mesmo ciclo, fica de fora.

Os tokens vêm dos eventos `session.usage_record` que o Copilot CLI grava em `~/.copilot/session-state/<sessão>/events.jsonl`, porque a API só informa créditos. Por isso eles cobrem só o Copilot CLI neste PC; uso no VS Code ou em outro computador fica de fora. O painel mostra quanto do valor gasto esses tokens representam (`CLI, 76% of spend`). O formato dos eventos não é documentado e pode mudar. Sem registros, os tokens aparecem como `?`.

> **Aviso:** os dados de uso vêm do endpoint **não documentado** `api.github.com/copilot_internal/user`, o mesmo usado pela extensão do Copilot no VS Code. Ele pode mudar ou deixar de funcionar sem aviso. O extra budget vem do campo `overage_entitlement` desse endpoint, porque o GitHub só documenta a API de budgets para organizações.

## Painel do Claude

O painel não usa credenciais. As únicas consultas de rede são as páginas públicas de preços; o uso vem de arquivos locais e o nome do plano, do comando `claude auth status`:

| Dado | Origem | Atualização | Abrangência |
|---|---|---|---|
| Cota de 5h e de 7 dias | Campo `rate_limits` da [status line](https://code.claude.com/docs/en/statusline) do Claude Code | A cada resposta do Claude Code; a TUI confere a cada 5 s | Conta |
| Tokens do mês, por modelo | Logs de sessão em `~/.claude/projects` | A cada 30 s | Só o Claude Code neste PC |
| Custo estimado em US$ | Tokens × [preço de lista da API](https://platform.claude.com/docs/en/about-claude/pricing), com cache de 24h | A cada 30 s | Só o Claude Code neste PC |

**Limites**

- A Anthropic não publica os limites em tokens. O "restam" é uma estimativa: a proporção entre o percentual da janela e o uso registrado neste PC nela. Aparece como faixa porque o percentual vem arredondado, e como `-` enquanto a janela estiver em 0%. Uso em outro PC ou no claude.ai faz a estimativa ficar abaixo do real, e ela também muda conforme a mistura de modelos e de cache.
- A cota só muda quando o Claude Code responde. "Atualizado" mostra a idade desse dado, e "lido" mostra a última leitura feita pelo app (a cada 5 s ou com `R`). Uso em outro PC ou no claude.ai entra na próxima resposta, e o painel mostra a idade do dado até lá.
- A cota só existe para assinantes Pro, Max e Ultra, depois da primeira resposta de uma sessão.
- O total de tokens é uma referência local, não um número oficial. Cada PC conta só o próprio uso, e o claude.ai fica de fora.
- O valor em US$ mostra quanto esse uso custaria a preço de lista da API, não o que o plano cobra. Sem internet nem cache de preços, o valor aparece como `?`.
- O preço do plano, nos dois painéis, é o preço mensal de lista publicado em [claude.com/pricing](https://claude.com/pricing) e na [documentação do GitHub](https://docs.github.com/en/copilot/concepts/billing/individual-plans), não a sua fatura. Assinatura anual ou desconto podem custar outro valor. Sem internet nem cache, aparece `?`.
- O saldo de usage credits não aparece: não há fonte documentada para lê-lo. Consulte [Settings > Usage](https://claude.ai/settings/usage).
- O formato interno dos logs não é documentado e pode mudar com atualizações. Se nada for reconhecido, o painel mostra um aviso.

**Arquivos em `%LOCALAPPDATA%\wf-sys-mon`**

| Arquivo | Conteúdo |
|---|---|
| `claude-statusline.ps1` | Script executado pelo Claude Code |
| `claude-status\<sessão>.json` | Última cota vista em cada sessão |
| `claude-prices.json` | Cache de 24h da tabela de preços por token |
| `plan-prices.json` | Cache de 24h dos preços mensais dos planos (Claude e Copilot) |
| `settings.json` | Preferências: idioma, seções recolhidas, intervalos e ícones |
| `claude-usage-days.json` | Tokens e custo estimado por dia e modelo; o total do mês continua disponível depois que o Claude Code apaga os logs (30 dias por padrão) |

> Com uma status line personalizada, o Claude Code deixa de exibir a maioria das dicas de teclado do rodapé. Para desfazer, remova a chave `statusLine` de `~/.claude/settings.json`; o `-Install` guarda uma cópia anterior em `settings.json.wf-sys-mon.bak`.

Se você já tiver outra status line, o `-Install` a mantém e a cota não aparece na TUI.

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

## Testes e releases

```powershell
dotnet test tests\WfSysMon.Tests
```

Os testes usam sensores falsos e uma pasta temporária, sem tocar nas suas preferências nem exigir administrador. A leitura de sensores reais continua sendo conferida manualmente.

O GitHub Actions compila e testa a cada push na `dev` e em cada pull request para a `main`. Uma tag `vX.Y.Z` na `main` gera a release: um `.zip` *self-contained* com hash SHA-256 e atestação de origem, que pode ser conferida com:

```powershell
gh attestation verify wf-sys-mon-X.Y.Z-win-x64.zip --repo muryarth/wf-sys-mon-tui
```

## Estrutura do projeto

```
WfSysMon.csproj     projeto .NET 10 (win-x64)
app.manifest        manifesto do Windows
wf-sys-mon.ps1      inicializador: dependências, compilação e execução
wf-sys-mon.cmd      atalho para o wf-sys-mon.ps1
claude-statusline.ps1  status line do Claude Code que grava a cota para a TUI
tests/              testes automatizados
.github/workflows/  CI e geração de releases
src/
  Program.cs        entrada, escolha da fonte dos sensores e loop principal
  AppPaths.cs       pasta dos dados do usuário
  SensorService.cs  serviço de sensores e sua instalação
  SensorServer.cs   envio das leituras pelo pipe local
  SensorClient.cs   recebimento das leituras na TUI
  SensorProtocol.cs formato dos dados entre serviço e TUI
  Screen.cs         renderização em tela cheia ajustada ao tamanho do terminal
  Dashboard.cs      conteúdo dos painéis e paleta de cores (classe Theme)
  CopilotUsage.cs   consulta do plano e dos créditos do Copilot
  TuiApp.cs         layout, seções, colunas, rolagem, telas de Contas e Ajustes e comandos
  InputReader.cs    teclado e mouse pela API de console do Windows
  Settings.cs       preferências salvas, idioma e ícones
  DiskVolumes.cs    tamanho dos discos (WMI)
  CopilotLocalUsage.cs  tokens reais lidos dos registros do Copilot CLI
  CopilotCycleHistory.cs  fechamento de cada ciclo do Copilot
  ExternalTool.cs   execução segura de claude, gh e winget
  JsonlTail.cs      leitura incremental de arquivos JSONL
  GitHubAuth.cs     login no GitHub dentro da TUI (via GitHub CLI)
  ClaudeUsage.cs    leitura da cota e soma dos tokens do mês do Claude
  ClaudePricing.cs  busca dos preços de lista da API e estimativa de custo do Claude
  PlanPrices.cs     busca dos preços mensais dos planos Claude e Copilot
  SensorDisplay.cs  média dos sensores e animação das barras
  ClaudeSetup.cs    registro da status line do Claude Code (usado pelo -Install)
```

## Solução de problemas

| Problema | Solução |
|---|---|
| `wf-sys-mon` não é reconhecido | Abra um novo terminal após o `-Install`. Confira se `%USERPROFILE%\bin` está no `PATH`. |
| "O sistema não pode encontrar o caminho especificado" | A pasta do projeto foi movida ou renomeada. Rode `.\wf-sys-mon.cmd -Install` na nova pasta. |
| Erro `0x800711C7` ao abrir | O **Smart App Control** bloqueou a DLL recompilada (ele libera arquivos pelo hash). Desative-o em *Segurança do Windows → Controle de aplicativos e navegador*. |
| O app ainda pede permissão ao abrir | O serviço não está instalado. Rode `wf-sys-mon -Install` ou instale pela aba Contas. |
| Temperaturas aparecem vazias | Em Contas (`A`), confira se o serviço está ativo e se o PawnIO está instalado. |
| Falha ao recompilar | O app ainda está aberto e bloqueia os arquivos em `out\`. Feche as janelas e tente de novo. |
| Painel do Copilot com "conta sem Copilot" | A conta conectada não tem assinatura do Copilot. |
| Painel do Claude com "sem dados" na cota | Em Contas (`A`), configure a cota do Claude, reinicie o Claude Code e envie uma mensagem. Confira se não há outra status line configurada. |
| Cliques do mouse não funcionam | Use os atalhos de teclado, que cobrem todas as ações. |
| Scripts bloqueados pela política de execução | Use `wf-sys-mon.cmd`, que já roda o script com `-ExecutionPolicy Bypass`. |

## Licença

Distribuído sob a licença [MIT](LICENSE). As dependências mantêm suas próprias licenças (Spectre.Console: MIT; LibreHardwareMonitorLib: MPL-2.0).