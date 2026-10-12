<#
  wf-sys-mon - inicializador do Workflow System Monitor.

  Na primeira execucao instala o que faltar (.NET 10 e GitHub CLI, via winget),
  compila o app se necessario e abre a TUI como administrador.

    wf-sys-mon             executa (instalando/compilando o que for preciso)
    wf-sys-mon -Install    registra o comando 'wf-sys-mon' no PATH do usuario
    wf-sys-mon -Rebuild    forca a recompilacao antes de executar
#>
[CmdletBinding()]
param(
    [switch]$Install,
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

$sudo     = Join-Path $env:SystemRoot 'System32\sudo.exe'
$sudoKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Sudo'
function Get-SudoMode { (Get-ItemProperty $sudoKey -ErrorAction SilentlyContinue).Enabled }

# A TUI so abre no mesmo terminal com o sudo do Windows em modo Inline (Enabled = 3).
function Enable-InlineSudo {
    if (-not (Test-Path $sudo)) {
        Write-Host '    sudo do Windows indisponivel (requer Windows 11 24H2+). A TUI abrira em uma nova janela,' -ForegroundColor Yellow
        Write-Host '    a menos que o terminal ja esteja aberto como administrador.' -ForegroundColor Yellow
        return
    }
    if ((Get-SudoMode) -eq 3) { Write-Ok 'sudo inline ja habilitado.'; return }

    Write-Host ''
    Write-Host '    Para abrir a TUI no mesmo terminal, o sudo do Windows precisa estar no modo Inline.' -ForegroundColor Yellow
    Write-Host '    Isso vale para todo o sistema: qualquer comando com sudo passa a rodar elevado no' -ForegroundColor Yellow
    Write-Host '    terminal atual (o UAC continua pedindo confirmacao).' -ForegroundColor Yellow
    $answer = Read-Host '    Habilitar o sudo inline agora? [S/n]'
    if ($answer -and $answer -notmatch '^[sSyY]') { Write-Ok 'Mantido como esta; a TUI abrira em uma nova janela.'; return }

    Write-Step 'Habilitando sudo inline (confirme no UAC)...'
    try {
        Start-Process $sudo -ArgumentList 'config', '--enable', 'normal' -Verb RunAs -Wait -WindowStyle Hidden
    }
    catch { Write-Host "    nao foi possivel habilitar: $($_.Exception.Message)" -ForegroundColor Yellow; return }

    if ((Get-SudoMode) -eq 3) { Write-Ok 'sudo inline habilitado.' }
    else { Write-Host '    sudo inline nao foi habilitado. Ative em Configuracoes > Sistema > Para desenvolvedores > sudo (modo Embutido).' -ForegroundColor Yellow }
}

# ------------------------------------------------------------------ dependencias

if ($Install) { Write-Step 'Registrando o comando wf-sys-mon...'; Register-Command }

if (Test-NeedsBuild) {
    if (-not (Test-DotnetSdk)) { Install-WingetPackage 'Microsoft.DotNet.SDK.10' '.NET 10 SDK' }
    Write-Step 'Compilando o WfSysMon...'
    & $dotnet publish $project -c Release -o $outDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'falha na compilacao.' }
    Write-Ok 'Compilado em out\'
}
elseif (-not (Test-DotnetRuntime)) {
    Install-WingetPackage 'Microsoft.DotNet.Runtime.10' '.NET 10 Runtime'
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue) -and -not (Test-Path $ghPath)) {
    Install-WingetPackage 'GitHub.cli' 'GitHub CLI'
}

if ($Install) {
    Enable-InlineSudo
    Write-Ok 'Pronto! Use o comando wf-sys-mon em um novo terminal.'
    exit 0
}

# ------------------------------------------------------------------ execucao

# Sensores exigem administrador. Com o sudo do Windows em modo inline (Enabled = 3) a TUI abre
# neste mesmo terminal; caso contrario o proprio app solicita UAC e abre um novo console.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin -and (Test-Path $sudo) -and (Get-SudoMode) -eq 3) {
    & $sudo $dotnet $dll @AppArgs
}
else {
    if (-not $isAdmin -and (Test-Path $sudo)) {
        Write-Host 'A TUI abrira em uma nova janela. Para usar este terminal, rode: wf-sys-mon -Install' -ForegroundColor DarkGray
    }
    & $dotnet $dll @AppArgs
}
exit $LASTEXITCODE
