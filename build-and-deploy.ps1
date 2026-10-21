# Compila o TeklaMcp e publica em %LOCALAPPDATA%\TeklaMcp\app (o exe que o Claude Desktop executa).
# Uso: .\build-and-deploy.ps1 [-TeklaBin C:\TeklaStructures\2026.0\bin] [-Register]
#   -Register  também cria/atualiza a entrada "tekla" no claude_desktop_config.json.
param(
    [string]$TeklaBin = "C:\TeklaStructures\2026.0\bin",
    [switch]$Register
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA "TeklaMcp\app"

dotnet build "$root\TeklaMcp.csproj" -c Release -v q -p:TeklaBin="$TeklaBin"
if ($LASTEXITCODE -ne 0) { throw "Falha na compilação." }

# O Claude Desktop mantém o exe aberto: encerra as instâncias antes de copiar (ele relança na próxima chamada).
Get-Process TeklaMcp -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$root\bin\Release\net48\*" $dest -Recurse -Force
Write-Host "Publicado em $dest"

if ($Register) {
    $cfg = Get-ChildItem "$env:LOCALAPPDATA\Claude-Data\*\claude_desktop_config.json", "$env:APPDATA\Claude\claude_desktop_config.json" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cfg) { throw "claude_desktop_config.json não encontrado." }
    Copy-Item $cfg.FullName "$($cfg.FullName).bak-teklamcp" -Force
    # Python preserva a formatação/acentos do arquivo melhor que o ConvertTo-Json do PowerShell 5.1.
    $py = @"
import json, sys
p, exe = sys.argv[1], sys.argv[2]
with open(p, encoding='utf-8') as f: c = json.load(f)
c.setdefault('mcpServers', {})['tekla'] = {'command': exe, 'args': []}
with open(p, 'w', encoding='utf-8') as f: json.dump(c, f, indent=2, ensure_ascii=False)
"@
    python -I -c $py $cfg.FullName (Join-Path $dest "TeklaMcp.exe")
    Write-Host "Entrada 'tekla' gravada em $($cfg.FullName) (backup: .bak-teklamcp). Reinicie o Claude Desktop."
}

& (Join-Path $dest "TeklaMcp.exe") --check
