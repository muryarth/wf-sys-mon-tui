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

if ($Install) { Write-Ok 'Pronto! Use o comando wf-sys-mon em um novo terminal.'; exit 0 }

# ------------------------------------------------------------------ execucao

# Sensores exigem administrador. Com o sudo do Windows em modo inline (Enabled = 3) a TUI abre
# neste mesmo terminal; caso contrario o proprio app solicita UAC e abre um novo console.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$sudo = Join-Path $env:SystemRoot 'System32\sudo.exe'
$sudoMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Sudo' -ErrorAction SilentlyContinue).Enabled

if (-not $isAdmin -and (Test-Path $sudo) -and $sudoMode -eq 3) {
    & $sudo $dotnet $dll @AppArgs
}
else {
    & $dotnet $dll @AppArgs
}
exit $LASTEXITCODE
