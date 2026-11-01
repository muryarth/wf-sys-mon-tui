<#
  Status line do Claude Code para o wf-sys-mon.

  O Claude Code executa este script e entrega um JSON documentado no stdin. Ele guarda a cota
  (rate_limits) em um arquivo por sessao, lido pela TUI, e imprime um resumo na status line.
  Nao usa credenciais nem rede. Docs: https://code.claude.com/docs/en/statusline

  O -Install copia este arquivo para %LOCALAPPDATA%\wf-sys-mon e registra o comando.
#>
$ErrorActionPreference = 'Stop'

$line = 'Claude Code'
try {
    $data = [Console]::In.ReadToEnd() | ConvertFrom-Json
    if ($data.model.display_name) { $line = $data.model.display_name }

    $limits = $data.rate_limits
    if ($limits -and $data.session_id -and ($limits.five_hour -or $limits.seven_day)) {
        $parts = @()
        if ($limits.five_hour) { $parts += '5h {0:F0}%' -f [double]$limits.five_hour.used_percentage }
        if ($limits.seven_day) { $parts += '7d {0:F0}%' -f [double]$limits.seven_day.used_percentage }
        $line = "$line | " + ($parts -join ' | ')

        $snapshot = [ordered]@{
            captured_at = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            five_hour   = $limits.five_hour
            seven_day   = $limits.seven_day
        } | ConvertTo-Json -Compress -Depth 4

        $dir = Join-Path $env:LOCALAPPDATA 'wf-sys-mon\claude-status'
        [IO.Directory]::CreateDirectory($dir) | Out-Null
        $name = $data.session_id -replace '[^\w-]', '_'
        $tmp = Join-Path $dir "$name.tmp"
        [IO.File]::WriteAllText($tmp, $snapshot)   # UTF-8 sem BOM
        Move-Item -LiteralPath $tmp -Destination (Join-Path $dir "$name.json") -Force
    }
}
catch {
    # A status line nunca deve quebrar por falha ao gravar: segue com o resumo que houver.
}

# Sem saida a status line fica em branco.
[Console]::Out.WriteLine($line)
exit 0
