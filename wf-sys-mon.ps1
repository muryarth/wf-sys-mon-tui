<#
  wf-sys-mon - inicializador do WF System Monitor.

  Mostra em uma única tela tudo o que precisa ser instalado ou alterado e só continua com a sua
  confirmação. Depois compila o app se necessário (o pacote da release já vem compilado) e abre a TUI.

    wf-sys-mon             abre o app; pergunta antes se faltar o .NET 10
    wf-sys-mon -Install    instala as dependências, o serviço de sensores, a status line do Claude Code
                           (sem sobrescrever uma existente) e o comando 'wf-sys-mon' no PATH do usuário
    wf-sys-mon -Uninstall  remove o serviço de sensores
    wf-sys-mon -Rebuild    força a recompilação antes de abrir
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$Rebuild,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$outDir  = Join-Path $root 'out'
# No pacote da release o app fica ao lado deste script; a partir do código-fonte, em out\.
$released = Test-Path (Join-Path $root 'WfSysMon.dll')
$dll     = if ($released) { Join-Path $root 'WfSysMon.dll' } else { Join-Path $outDir 'WfSysMon.dll' }
$project = Join-Path $root 'WfSysMon.csproj'
$dotnet  = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$ghPath  = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
$pawnIoKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO'

$script:Frames = '|', '/', '-', '\'
$script:StepOutput = @()

# ------------------------------------------------------------------ textos

# Idioma escolhido no app; o padrão é inglês.
function Get-Text($pt, $en) {
    try {
        $file = Join-Path $env:LOCALAPPDATA 'wf-sys-mon\settings.json'
        if ((Get-Content $file -Raw | ConvertFrom-Json).Language -eq 'pt') { return $pt }
    }
    catch { }
    return $en
}

function Write-Title {
    Write-Host ''
    Write-Host '  WF System Monitor' -ForegroundColor White
    Write-Host ''
}

function Stop-WithError($message) {
    Write-Host ''
    Write-Host "  $message" -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------------------ estado da máquina

function Test-DotnetRuntime {
    (Test-Path $dotnet) -and [bool](& $dotnet --list-runtimes 2>$null | Select-String '^Microsoft\.NETCore\.App 10\.')
}

function Test-DotnetSdk {
    (Test-Path $dotnet) -and [bool](& $dotnet --list-sdks 2>$null | Select-String '^10\.')
}

function Test-GitHubCli {
    [bool](Get-Command gh -ErrorAction SilentlyContinue) -or (Test-Path $ghPath)
}

function Test-NeedsBuild {
    if ($released) { return $false }
    if ($Rebuild -or -not (Test-Path $dll)) { return $true }
    $built = (Get-Item $dll).LastWriteTimeUtc
    $sources = @(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs) + (Get-Item $project) + (Get-Item (Join-Path $root 'app.manifest'))
    return [bool]($sources | Where-Object { $_.LastWriteTimeUtc -gt $built })
}

# ------------------------------------------------------------------ tela de confirmação

# Menu com setas, igual ao do app. Esc escolhe a última opção, a de sair. Devolve a opção escolhida.
function Read-Choice($options, $hint) {
    $selected = 0
    $width = [Math]::Max([Console]::WindowWidth - 1, 20)
    $recommended = Get-Text 'Recomendado' 'Recommended'
    $lines = $options.Count + 2
    $done = $false

    try { [Console]::CursorVisible = $false } catch { }
    try {
        for ($i = 0; $i -lt $lines; $i++) { Write-Host '' }
        while ($true) {
            $top = [Console]::CursorTop - $lines
            [Console]::SetCursorPosition(0, $top)
            for ($i = 0; $i -lt $options.Count; $i++) {
                $marker = if ($i -eq $selected) { '  > ' } else { '    ' }
                $color = if ($i -eq $selected) { 'Cyan' } else { 'Gray' }
                $tag = if ($i -eq 0) { "  $recommended" } else { '' }
                $padding = ' ' * [Math]::Max($width - $marker.Length - $options[$i].Length - $tag.Length, 0)
                Write-Host ($marker + $options[$i]) -NoNewline -ForegroundColor $color
                Write-Host ($tag + $padding) -ForegroundColor Green
            }
            Write-Host ''
            Write-Host "  $hint" -ForegroundColor DarkGray

            switch ([Console]::ReadKey($true).Key) {
                'UpArrow'   { $selected = ($selected - 1 + $options.Count) % $options.Count }
                'DownArrow' { $selected = ($selected + 1) % $options.Count }
                'Tab'       { $selected = ($selected + 1) % $options.Count }
                'Enter'     { $done = $true }
                'Spacebar'  { $done = $true }
                'Escape'    { $selected = $options.Count - 1; $done = $true }
            }
            if ($done) { break }
        }

        # A escolha foi feita: o menu sai da tela, e o progresso aparece no lugar dele.
        [Console]::SetCursorPosition(0, $top)
        for ($i = 0; $i -lt $lines; $i++) { Write-Host (' ' * $width) }
        [Console]::SetCursorPosition(0, $top)
        return $selected
    }
    finally {
        try { [Console]::CursorVisible = $true } catch { }
    }
}

# Lista tudo o que vai mudar e pede uma única confirmação. Sem ela, nada é alterado.
function Confirm-Changes($items, $accept) {
    $declined = Get-Text 'Nada foi alterado. Rode de novo quando quiser instalar.' 'Nothing was changed. Run this again when you want to install.'
    Write-Title
    Write-Host ('  ' + (Get-Text 'Para continuar, isto será feito neste PC:' 'To continue, this will be done on this PC:')) -ForegroundColor Gray
    Write-Host ''

    $nameWidth = ($items | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum
    foreach ($item in $items) {
        Write-Host ('    ' + $item.Name.PadRight($nameWidth + 3)) -NoNewline -ForegroundColor White
        Write-Host $item.Why -ForegroundColor DarkGray
    }
    Write-Host ''
    if ($items | Where-Object { $_.Package }) {
        Write-Host ('  ' + (Get-Text 'Os programas vêm do winget, aceitando os termos de cada pacote.' 'Programs come from winget, accepting the terms of each package.')) -ForegroundColor Gray
    }
    if ($items | Where-Object { $_.Admin }) {
        Write-Host ('  ' + (Get-Text 'O Windows pode pedir permissão de administrador (UAC) durante a instalação.' 'Windows may ask for administrator permission (UAC) during the installation.')) -ForegroundColor Gray
    }
    Write-Host ''

    if ([Console]::IsInputRedirected) { Stop-WithError $declined }

    $choice = Read-Choice @($accept, (Get-Text 'Sair' 'Quit')) (Get-Text '↑ ↓ escolhe  ·  Enter confirma' '↑ ↓ choose  ·  Enter confirm')
    if ($choice -ne 0) {
        Write-Host "  $declined" -ForegroundColor DarkGray
        exit 1
    }
}

# ------------------------------------------------------------------ etapas com indicador

function Write-StepResult($ok, $label) {
    $width = [Math]::Max([Console]::WindowWidth - 1, 20)
    Write-Host "`r$(' ' * $width)`r" -NoNewline
    if ($ok) {
        Write-Host '  OK ' -NoNewline -ForegroundColor Green
    }
    else {
        Write-Host '  !! ' -NoNewline -ForegroundColor Red
    }
    Write-Host " $label" -ForegroundColor Gray
}

# Roda um programa mostrando só o indicador; os detalhes ficam guardados para o caso de falha.
function Invoke-Step($label, $file, $arguments, $okCodes = @(0), $waiting = '') {
    $out = [IO.Path]::GetTempFileName()
    $err = [IO.Path]::GetTempFileName()
    $ok = $false
    $script:StepOutput = @()
    $text = if ($waiting) { "$label  ($waiting)" } else { $label }

    try { [Console]::CursorVisible = $false } catch { }
    try {
        $process = Start-Process -FilePath $file -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
        [void]$process.Handle  # necessário para ler o ExitCode depois

        $i = 0
        while (-not $process.HasExited) {
            Write-Host "`r  " -NoNewline
            Write-Host $script:Frames[$i++ % 4] -NoNewline -ForegroundColor Cyan
            Write-Host "   $text" -NoNewline -ForegroundColor DarkGray
            Start-Sleep -Milliseconds 120
        }
        $process.WaitForExit()
        $ok = $okCodes -contains $process.ExitCode
        if (-not $ok) {
            $script:StepOutput = @(Get-Content $out, $err -ErrorAction SilentlyContinue | Select-Object -Last 15)
        }
    }
    catch {
        $script:StepOutput = @($_.Exception.Message)
    }
    finally {
        try { [Console]::CursorVisible = $true } catch { }
        Remove-Item $out, $err -ErrorAction SilentlyContinue
    }

    Write-StepResult $ok $label
    return $ok
}

# -1978335189 (0x8A15002B): já instalado / nada a atualizar
function Install-Package($id, $name) {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        $script:StepOutput = @()
        Write-StepResult $false $name
        Write-Note (Get-Text "winget não encontrado. Instale manualmente: $id" "winget not found. Install it manually: $id")
        return $false
    }
    return Invoke-Step $name $winget.Source "install --id $id -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity" @(0, -1978335189)
}

function Write-Note($text) {
    Write-Host "       $text" -ForegroundColor DarkGray
}

function Register-Command {
    $binDir = Join-Path $env:USERPROFILE 'bin'
    New-Item -ItemType Directory -Force $binDir | Out-Null
    Set-Content -Path (Join-Path $binDir 'wf-sys-mon.cmd') -Encoding Ascii -Value "@call `"$(Join-Path $root 'wf-sys-mon.cmd')`" %*"

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (($userPath -split ';') -contains $binDir)) {
        [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $binDir), 'User')
    }
}

# ------------------------------------------------------------------ o que falta

$needsBuild = Test-NeedsBuild
$dotnetPackage = $null
if ($needsBuild -and -not (Test-DotnetSdk)) {
    $dotnetPackage = @{ Id = 'Microsoft.DotNet.SDK.10'; Name = '.NET 10 SDK' }
}
elseif (-not $needsBuild -and -not (Test-DotnetRuntime)) {
    $dotnetPackage = @{ Id = 'Microsoft.DotNet.Runtime.10'; Name = '.NET 10 Runtime' }
}
$installGh = $Install -and -not (Test-GitHubCli)
$installPawnIo = $Install -and -not (Test-Path $pawnIoKey)

$serviceName = Get-Text 'Serviço de sensores' 'Sensor service'
$statusLineName = Get-Text 'Status line do Claude Code' 'Claude Code status line'
$commandName = Get-Text 'Comando wf-sys-mon' 'wf-sys-mon command'

$items = @()
if ($dotnetPackage) {
    $items += @{ Name = $dotnetPackage.Name; Why = (Get-Text 'Necessário para abrir o app' 'Required to open the app'); Package = $true; Admin = $true }
}
if ($installGh) {
    $items += @{ Name = 'GitHub CLI'; Why = (Get-Text 'Login do painel do Copilot e confirmação das atualizações' 'Copilot panel sign-in and update verification'); Package = $true; Admin = $true }
}
if ($installPawnIo) {
    $items += @{ Name = 'PawnIO'; Why = (Get-Text 'Driver assinado, de código aberto, que lê temperatura e potência da CPU' 'Signed, open-source driver that reads CPU temperature and power'); Package = $true; Admin = $true }
}
if ($Install) {
    $items += @{ Name = $serviceName; Why = (Get-Text 'Lê os sensores para o app abrir sem administrador' 'Reads the sensors so the app opens without administrator'); Admin = $true }
    $items += @{ Name = $statusLineName; Why = (Get-Text 'Envia a cota ao app e muda o rodapé do Claude Code' 'Sends the quota to the app and changes the Claude Code footer') }
    $items += @{ Name = $commandName; Why = (Get-Text 'Adiciona o comando ao PATH do seu usuário' 'Adds the command to your user PATH') }
}

# ------------------------------------------------------------------ execução

if ($items.Count -gt 0) {
    $accept = if ($Install) { Get-Text 'Instalar tudo' 'Install everything' } else { Get-Text 'Instalar e abrir' 'Install and open' }
    Confirm-Changes $items $accept
}
elseif ($needsBuild) {
    Write-Title
}

if ($dotnetPackage -and -not (Install-Package $dotnetPackage.Id $dotnetPackage.Name)) {
    $script:StepOutput | ForEach-Object { Write-Note $_ }
    Stop-WithError (Get-Text "O app não abre sem o $($dotnetPackage.Name). Para instalar: winget install --id $($dotnetPackage.Id) -e" "The app cannot open without $($dotnetPackage.Name). To install it: winget install --id $($dotnetPackage.Id) -e")
}

if ($needsBuild) {
    $built = Invoke-Step (Get-Text 'Preparando o WF System Monitor' 'Preparing WF System Monitor') $dotnet "publish `"$project`" -c Release -o `"$outDir`" --nologo -v quiet"
    if (-not $built) {
        $script:StepOutput | ForEach-Object { Write-Note $_ }
        Stop-WithError (Get-Text 'Falha na compilação.' 'Build failed.')
    }
}

if ($Install) {
    $pending = $false
    if ($installGh -and -not (Install-Package 'GitHub.cli' 'GitHub CLI')) {
        $pending = $true
        Write-Note (Get-Text 'Dá para instalar depois na aba Accounts.' 'You can install it later on the Accounts tab.')
    }
    if ($installPawnIo -and -not (Install-Package 'namazso.PawnIO' 'PawnIO')) {
        $pending = $true
        Write-Note (Get-Text 'Dá para instalar depois na aba Settings.' 'You can install it later on the Settings tab.')
    }

    $serviceOk = Invoke-Step $serviceName $dotnet "`"$dll`" --request-service-install" @(0) (Get-Text 'confirme no Windows' 'confirm in Windows')
    if (-not $serviceOk) {
        $pending = $true
        Write-Note (Get-Text 'Sem o serviço, o app oferece a instalação ao abrir, ou roda em modo limitado.' 'Without the service, the app offers to install it when it opens, or runs in limited mode.')
    }

    $statusLineOk = Invoke-Step $statusLineName $dotnet "`"$dll`" --setup-claude-statusline `"$(Join-Path $root 'claude-statusline.ps1')`""
    if (-not $statusLineOk) {
        $pending = $true
        Write-Note (Get-Text 'A cota do Claude não aparecerá no app até isso ser resolvido.' 'The Claude quota will not appear in the app until this is resolved.')
    }

    Register-Command
    Write-StepResult $true $commandName

    Write-Host ''
    if ($pending) {
        Write-Host ('  ' + (Get-Text 'Instalado, com as pendências acima. Abra um novo terminal e rode wf-sys-mon.' 'Installed, with the pending items above. Open a new terminal and run wf-sys-mon.')) -ForegroundColor Yellow
    }
    else {
        Write-Host ('  ' + (Get-Text 'Pronto! Abra um novo terminal e rode wf-sys-mon.' 'All set! Open a new terminal and run wf-sys-mon.')) -ForegroundColor Green
    }
    exit 0
}

if ($Uninstall) {
    Write-Title
    $removed = Invoke-Step $serviceName $dotnet "`"$dll`" --request-service-uninstall" @(0) (Get-Text 'confirme no Windows' 'confirm in Windows')
    if ($removed) { exit 0 }
    exit 1
}

# A TUI roda como usuário comum: os sensores vêm do serviço, e ela explica o que fazer se ele faltar.
& $dotnet $dll @AppArgs
exit $LASTEXITCODE
