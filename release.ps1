#Requires -Version 5.1
<#
.SYNOPSIS
    Publica uma nova versão do EasyPDF no GitHub.
.EXAMPLE
    .\release.ps1 1.6.0
#>
param(
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = "Stop"

function Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok([string]$msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "    ERRO: $msg" -ForegroundColor Red; exit 1 }

# ---------- 1. Atualiza versão no .csproj ----------
Step "Atualizando versão para $Version..."
$csproj = "src\EasyPDF.UI\EasyPDF.UI.csproj"
$content = Get-Content $csproj -Raw
$content = $content -replace '<Version>[^<]+</Version>',               "<Version>$Version</Version>"
$content = $content -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
$content = $content -replace '<FileVersion>[^<]+</FileVersion>',         "<FileVersion>$Version.0</FileVersion>"
[System.IO.File]::WriteAllText((Resolve-Path $csproj), $content, [System.Text.Encoding]::UTF8)
Ok "EasyPDF.UI.csproj atualizado."

# ---------- 2. Limpa outputs anteriores ----------
Step "Limpando outputs anteriores..."
Remove-Item -Recurse -Force publish\   -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force releases\  -ErrorAction SilentlyContinue
Ok "Pastas limpas."

# ---------- 3. Publica ----------
Step "Publicando (dotnet publish)..."
dotnet publish src/EasyPDF.UI -c Release -r win-x64 --self-contained -o publish/
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish falhou." }
Ok "Publicado em publish\"

# ---------- 4. Empacota com Velopack ----------
Step "Empacotando com Velopack..."
vpk pack --packId EasyPDF --packVersion $Version --packDir publish/ --outputDir releases/
if ($LASTEXITCODE -ne 0) { Fail "vpk pack falhou." }
Ok "Pacotes gerados em releases\"

# ---------- 5. Commit + tag ----------
# Git warnings vão para stderr; desativa Stop temporariamente para não tratar warnings como erros.
Step "Commitando e criando tag v$Version..."
$ErrorActionPreference = "Continue"
git add src/EasyPDF.UI/EasyPDF.UI.csproj
git commit -m "chore: bump version to $Version"
git tag "v$Version"
git push origin master --tags
$gitExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($gitExit -ne 0) { Fail "git push falhou (exit $gitExit)." }
Ok "Tag v$Version enviada para o GitHub."

# ---------- 6. Cria release no GitHub ----------
Step "Criando GitHub Release v$Version..."
$ErrorActionPreference = "Continue"
gh release create "v$Version" releases/* --title "EasyPDF $Version" --generate-notes
$ghExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($ghExit -ne 0) { Fail "gh release create falhou (exit $ghExit)." }

Write-Host "`nEasyPDF v$Version publicado com sucesso!" -ForegroundColor Green
