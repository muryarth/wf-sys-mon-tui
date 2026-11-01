<#
  wf-sys-mon - inicializador do Workflow System Monitor.

  Na primeira execucao instala o que faltar (.NET 10 e GitHub CLI, via winget),
  compila o app se necessario e abre a TUI. Com o servico de sensores instalado, ela abre sem
  pedir permissao.

    wf-sys-mon             executa (instalando/compilando o que for preciso)
    wf-sys-mon -Install    registra o comando 'wf-sys-mon' no PATH do usuario, configura a status
                           line do Claude Code (sem sobrescrever uma existente) e instala o
                           servico de sensores (o Windows confirma uma vez)
    wf-sys-mon -Uninstall  remove o servico de sensores
    wf-sys-mon -Rebuild    forca a recompilacao antes de executar
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
$dll     = Join-Path $outDir 'WfSysMon.dll'
$project = Join-Path $root 'WfSysMon.csproj'
$dotnet  = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$ghPath  = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Stop-WithError($msg) { Write-Host "ERRO: $msg" -ForegroundColor Red; exit 1 }

function Install-WingetPackage($id, $name) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Stop-WithError "winget nao encontrado. Instale '$name' manualmente e rode 'wf-sys-mon' de novo."
    }
    Write-Step "Instalando $name via winget (pode pedir permissao de administrador)..."
    & winget install --id $id -e --silent --accept-source-agreements --accept-package-agreements
    # -1978335189 (0x8A15002B): ja instalado / nada a atualizar
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne -1978335189) {
        Stop-WithError "falha ao instalar $name (codigo $LASTEXITCODE)."
    }
    Write-Ok "$name instalado."
}

function Test-DotnetRuntime {
    (Test-Path $dotnet) -and [bool](& $dotnet --list-runtimes 2>$null | Select-String '^Microsoft\.NETCore\.App 10\.')
}

function Test-DotnetSdk {
    (Test-Path $dotnet) -and [bool](& $dotnet --list-sdks 2>$null | Select-String '^10\.')
}

function Test-NeedsBuild {
    if ($Rebuild -or -not (Test-Path $dll)) { return $true }
    $built = (Get-Item $dll).LastWriteTimeUtc
    $sources = @(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs) + (Get-Item $project) + (Get-Item (Join-Path $root 'app.manifest'))
    return [bool]($sources | Where-Object { $_.LastWriteTimeUtc -gt $built })
}

function Register-Command {
    $binDir = Join-Path $env:USERPROFILE 'bin'
    New-Item -ItemType Directory -Force $binDir | Out-Null
    $shim = Join-Path $binDir 'wf-sys-mon.cmd'
    Set-Content -Path $shim -Encoding Ascii -Value "@call `"$(Join-Path $root 'wf-sys-mon.cmd')`" %*"
    Write-Ok "Atalho criado: $shim"

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (($userPath -split ';') -contains $binDir)) {
        [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $binDir), 'User')
        Write-Ok "$binDir adicionado ao PATH do usuario (abra um novo terminal)."
    }
}

# Idioma escolhido no app; o padrao e ingles.
function Get-Text($pt, $en) {
    try {
        $file = Join-Path $env:LOCALAPPDATA 'wf-sys-mon\settings.json'
        if ((Get-Content $file -Raw | ConvertFrom-Json).Language -eq 'pt') { return $pt }
    }
    catch { }
    return $en
}

# Compila mostrando so um indicador animado; os detalhes aparecem apenas se falhar.
function Invoke-Build {
    $log = [IO.Path]::GetTempFileName()
    $err = [IO.Path]::GetTempFileName()
    $arguments = "publish `"$project`" -c Release -o `"$outDir`" --nologo -v quiet"
    $build = Start-Process -FilePath $dotnet -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $err
    [void]$build.Handle  # necessario para ler o ExitCode depois

    $text = Get-Text 'Preparando o WF System Monitor...' 'Preparing WF System Monitor...'
    $frames = '|', '/', '-', '\'
    $i = 0
    # Sem console (redirecionado ou em CI) nao ha cursor para esconder.
    try { [Console]::CursorVisible = $false } catch { }
    try {
        while (-not $build.HasExited) {
            Write-Host "`r  " -NoNewline
            Write-Host $frames[$i++ % 4] -NoNewline -ForegroundColor Cyan
            Write-Host " $text" -NoNewline -ForegroundColor DarkGray
            Start-Sleep -Milliseconds 120
        }
    }
    finally {
        try { [Console]::CursorVisible = $true } catch { }
    }
    Write-Host ("`r" + (' ' * ($text.Length + 6)) + "`r") -NoNewline

    $build.WaitForExit()
    if ($build.ExitCode -ne 0) {
        Get-Content $log, $err | Select-Object -Last 20 | Write-Host
        Remove-Item $log, $err -ErrorAction SilentlyContinue
        Stop-WithError (Get-Text 'falha na compilacao.' 'build failed.')
    }
    Remove-Item $log, $err -ErrorAction SilentlyContinue
}

# Driver de sensores usado pela biblioteca (temperatura e potencia da CPU, entre outros).
function Install-SensorDriver {
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO'
    if (Test-Path $key) { Write-Ok 'Driver PawnIO ja instalado.'; return }

    Write-Host ''
    Write-Host '    O PawnIO e um driver do Windows, de codigo aberto e assinado, usado para ler os sensores.' -ForegroundColor Yellow
    Write-Host '    Sem ele, temperatura e potencia da CPU, entre outros, nao aparecem.' -ForegroundColor Yellow
    $answer = Read-Host '    Instalar o PawnIO agora? [S/n]'
    if ($answer -and $answer -notmatch '^[sSyY]') { Write-Ok 'Nao instalado; da para instalar depois na aba Accounts.'; return }
    Install-WingetPackage 'namazso.PawnIO' 'PawnIO'
}

# ------------------------------------------------------------------ dependencias

if ($Install) { Write-Step 'Registrando o comando wf-sys-mon...'; Register-Command }

if (Test-NeedsBuild) {
    if (-not (Test-DotnetSdk)) { Install-WingetPackage 'Microsoft.DotNet.SDK.10' '.NET 10 SDK' }
    Invoke-Build
}
elseif (-not (Test-DotnetRuntime)) {
    Install-WingetPackage 'Microsoft.DotNet.Runtime.10' '.NET 10 Runtime'
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue) -and -not (Test-Path $ghPath)) {
    Install-WingetPackage 'GitHub.cli' 'GitHub CLI'
}

if ($Install) {
    Write-Step 'Configurando a status line do Claude Code...'
    & $dotnet $dll --setup-claude-statusline (Join-Path $root 'claude-statusline.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Host '    A cota do Claude nao aparecera na TUI ate isso ser resolvido.' -ForegroundColor Yellow }

    Install-SensorDriver

    Write-Step 'Instalando o servico de sensores (confirme no UAC)...'
    & $dotnet $dll --request-service-install
    if ($LASTEXITCODE -ne 0) { Write-Host '    Sem o servico, a TUI oferece instalar ao abrir, ou roda em modo limitado.' -ForegroundColor Yellow }

    Write-Ok 'Pronto! Use o comando wf-sys-mon em um novo terminal.'
    exit 0
}

if ($Uninstall) {
    Write-Step 'Removendo o servico de sensores (confirme no UAC)...'
    & $dotnet $dll --request-service-uninstall
    exit $LASTEXITCODE
}

# ------------------------------------------------------------------ execucao

# A TUI roda como usuario comum: os sensores vem do servico, e ela explica o que fazer se ele faltar.
& $dotnet $dll @AppArgs
exit $LASTEXITCODE
