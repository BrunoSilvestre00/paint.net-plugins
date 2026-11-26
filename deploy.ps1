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

# Projetos: qualquer *.csproj em src\ (exceto projetos de teste). -Project filtra pelo nome do csproj.
$projects = Get-ChildItem "$root\src" -Recurse -Filter *.csproj |
    Where-Object { $_.BaseName -notlike '*.Tests' -and (-not $Project -or $_.BaseName -eq $Project) }
if (-not $projects) { throw "Nenhum projeto encontrado ($Project)" }

$dlls = @()
foreach ($p in $projects) {
    # PaintNetPath: usado pelos nossos plugins. PdnDir/EffectsDir (com '\' no fim): usados pelo Liquify (upstream).
    # A barra final é duplicada de propósito: '\"' escaparia a aspa na linha de comando do Windows.
    dotnet build $p.FullName -c $Configuration -p:PaintNetPath="$paintNet" -p:PdnDir="$paintNet\\" -p:EffectsDir="$effectsDir\\"
    if ($LASTEXITCODE -ne 0) { throw "Build falhou: $($p.Name)" }
    $dlls += Get-ChildItem (Join-Path $p.DirectoryName "bin\$Configuration") -Filter "$($p.BaseName).dll" -Recurse | Select-Object -First 1
}

# Pasta de distribuição na raiz: é o que o usuário final baixa
$distDir = Join-Path $root 'dlls'
New-Item -ItemType Directory -Force $distDir | Out-Null

if (Get-Process paintdotnet -ErrorAction SilentlyContinue) {
    Write-Warning 'O Paint.NET está aberto: ele segura as DLLs carregadas e a cópia para Effects pode falhar. Feche-o antes.'
}

$failed = @()
foreach ($dll in $dlls) {
    Copy-Item $dll.FullName $distDir -Force
    try {
        Copy-Item $dll.FullName $effectsDir -Force -ErrorAction Stop
    } catch [System.UnauthorizedAccessException] {
        Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command Copy-Item '$($dll.FullName)' '$effectsDir' -Force"
    } catch {
        Write-Warning "Cópia falhou para $($dll.Name): $($_.Exception.Message)"
    }

    # Confere de verdade: o hash do instalado tem que ser o do recém-compilado.
    $installed = Join-Path $effectsDir $dll.Name
    $ok = (Test-Path $installed) -and ((Get-FileHash $installed).Hash -eq (Get-FileHash $dll.FullName).Hash)
    if ($ok) { Write-Host "Instalado: $($dll.Name) -> $effectsDir" }
    else { Write-Warning "NÃO instalado: $($dll.Name) (a DLL em $effectsDir é diferente da compilada)"; $failed += $dll.Name }
}

if ($failed.Count -gt 0) { throw "Falha ao instalar: $($failed -join ', '). Feche o Paint.NET e rode de novo." }
Write-Host 'Reinicie o Paint.NET para carregar o plugin.'
