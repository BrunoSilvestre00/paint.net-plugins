# Uso: .\deploy.ps1 [-Project NomeDoProjeto] [-Configuration Release]
# Compila e copia a(s) DLL(s) para $PAINT_NET_PATH\Effects (definido no .env).
# Se a pasta exigir admin (Program Files), a cópia é refeita com elevação (UAC).
param(
    [string]$Project,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Lê PAINT_NET_PATH do .env
$line = Get-Content "$root\.env" | Where-Object { $_ -match '^\s*PAINT_NET_PATH\s*=' } | Select-Object -First 1
if (-not $line) { throw 'PAINT_NET_PATH não encontrado no .env' }
$paintNet = ($line -split '=', 2)[1].Trim().Trim('"')
$effectsDir = Join-Path $paintNet 'Effects'
if (-not (Test-Path $effectsDir)) { throw "Pasta não existe: $effectsDir" }

# Projetos: src\<Nome>\<Nome>.csproj
$projects = if ($Project) { Get-Item "$root\src\$Project\$Project.csproj" }
            else { Get-ChildItem "$root\src" -Recurse -Filter *.csproj }

$dlls = @()
foreach ($p in $projects) {
    dotnet build $p.FullName -c $Configuration -p:PaintNetPath="$paintNet"
    if ($LASTEXITCODE -ne 0) { throw "Build falhou: $($p.Name)" }
    $dlls += Get-ChildItem (Join-Path $p.DirectoryName "bin\$Configuration") -Filter "$($p.BaseName).dll" -Recurse | Select-Object -First 1
}

# Pasta de distribuição na raiz: é o que o usuário final baixa
$distDir = Join-Path $root 'dlls'
New-Item -ItemType Directory -Force $distDir | Out-Null

foreach ($dll in $dlls) {
    Copy-Item $dll.FullName $distDir -Force
    try {
        Copy-Item $dll.FullName $effectsDir -Force -ErrorAction Stop
    } catch [System.UnauthorizedAccessException] {
        Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command Copy-Item '$($dll.FullName)' '$effectsDir' -Force"
    }
    Write-Host "Instalado: $($dll.Name) -> $effectsDir"
}
Write-Host 'Reinicie o Paint.NET para carregar o plugin.'
