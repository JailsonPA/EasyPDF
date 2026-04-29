<#
.SYNOPSIS
    Builds EasyPDF, packages it as an MSIX, and signs it with a dev certificate.

.DESCRIPTION
    Full pipeline:
        1. Locate MakeAppx.exe and SignTool.exe from the Windows SDK.
        2. Create (or reuse) a self-signed "CN=EasyPDF Dev" certificate in
           Cert:\CurrentUser\My and export it as packaging\EasyPDF-Dev.pfx.
        3. dotnet publish -> publish\win-x64\
        4. Copy AppxManifest.xml + Assets\ into the publish folder.
        5. MakeAppx pack  -> publish\EasyPDF.msix
        6. SignTool sign  -> signed MSIX.
        7. (Optional) Install the certificate to TrustedPeople so Windows will
           install the MSIX without a Store signature.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER OutDir
    Folder that receives EasyPDF.msix. Default: publish\ (repo root).

.PARAMETER SkipBuild
    Skip dotnet publish (useful when iterating on the manifest only).

.PARAMETER InstallCert
    Import the dev certificate into Cert:\LocalMachine\TrustedPeople so that
    the MSIX can be double-clicked and installed without Store signing.
    Requires an elevated shell.

.EXAMPLE
    .\scripts\publish-msix.ps1
    .\scripts\publish-msix.ps1 -InstallCert
    .\scripts\publish-msix.ps1 -SkipBuild -InstallCert

.NOTES
    Run from the repo root or from anywhere -- the script resolves paths from
    its own location ($PSScriptRoot).
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutDir        = '',
    [switch] $SkipBuild,
    [switch] $InstallCert
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- Paths ------------------------------------------------------------------
$repoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectFile = Join-Path $repoRoot 'src\EasyPDF.UI\EasyPDF.UI.csproj'
$packaging   = Join-Path $repoRoot 'packaging'
$publishDir  = Join-Path $repoRoot 'publish\win-x64'
$pfxPath     = Join-Path $packaging 'EasyPDF-Dev.pfx'
$certSubject = 'CN=EasyPDF Dev'

if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'publish' }
$msixPath = Join-Path $OutDir 'EasyPDF.msix'

# ---- Helper: find a file under the Windows SDK ------------------------------
function Find-SdkTool {
    param([string] $ToolName)

    $sdkRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    )
    foreach ($root in $sdkRoots) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem $root -Recurse -Filter $ToolName -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -like '*x64*' } |
               Sort-Object FullName -Descending |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

# ---- 1. Locate SDK tools ----------------------------------------------------
Write-Host "`n[1/6] Locating Windows SDK tools..." -ForegroundColor Cyan

$makeAppx = Find-SdkTool 'MakeAppx.exe'
$signTool = Find-SdkTool 'SignTool.exe'

if (-not $makeAppx) {
    Write-Error "MakeAppx.exe not found. Install the Windows SDK (winget install Microsoft.WindowsSDK.10.0.26100)."
}
if (-not $signTool) {
    Write-Error "SignTool.exe not found. Install the Windows SDK."
}

Write-Host "  MakeAppx : $makeAppx"
Write-Host "  SignTool : $signTool"

# ---- 2. Dev certificate -----------------------------------------------------
Write-Host "`n[2/6] Ensuring dev signing certificate ($certSubject)..." -ForegroundColor Cyan

$cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $certSubject -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

if (-not $cert) {
    Write-Host "  Creating new self-signed certificate..."
    $cert = New-SelfSignedCertificate `
        -Subject           $certSubject `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -Type              CodeSigningCert `
        -NotAfter          (Get-Date).AddYears(5) `
        -HashAlgorithm     SHA256
    Write-Host "  Created: $($cert.Thumbprint)"
} else {
    Write-Host "  Reusing existing cert: $($cert.Thumbprint)  (expires $($cert.NotAfter.ToString('yyyy-MM-dd')))"
}

# Export PFX (password: EasyPDFDev) so CI / teammates can sign builds.
# packaging/*.pfx is in .gitignore -- never commit it.
$pfxPassword = ConvertTo-SecureString 'EasyPDFDev' -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pfxPassword | Out-Null
Write-Host "  PFX exported -> $pfxPath  (password: EasyPDFDev)"

if ($InstallCert) {
    Write-Host "  Installing certificate into Cert:\LocalMachine\TrustedPeople..."
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        'TrustedPeople', 'LocalMachine')
    $store.Open('ReadWrite')
    $store.Add($cert)
    $store.Close()
    Write-Host "  Done -- MSIX can now be installed on this machine."
}

# ---- 3. dotnet publish ------------------------------------------------------
if ($SkipBuild) {
    Write-Host "`n[3/6] Skipping build (-SkipBuild)." -ForegroundColor DarkGray
} else {
    Write-Host "`n[3/6] Publishing EasyPDF.UI ($Configuration, win-x64, self-contained)..." -ForegroundColor Cyan

    & dotnet publish $projectFile `
        --configuration $Configuration `
        --runtime       win-x64 `
        --self-contained true `
        --output        $publishDir `
        /p:PublishSingleFile=false `
        /p:IncludeNativeLibrariesForSelfExtract=true

    if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed (exit $LASTEXITCODE)." }
    Write-Host "  Published -> $publishDir"
}

# ---- 4. Inject manifest + assets --------------------------------------------
Write-Host "`n[4/6] Copying manifest and assets..." -ForegroundColor Cyan

Copy-Item (Join-Path $packaging 'Package.appxmanifest') `
          (Join-Path $publishDir 'AppxManifest.xml') -Force

$assetsDir = Join-Path $publishDir 'Assets'
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory $assetsDir | Out-Null }

$srcAssets = Join-Path $packaging 'Assets'
if (Test-Path $srcAssets) {
    Copy-Item "$srcAssets\*" $assetsDir -Force
    Write-Host "  Assets copied from $srcAssets"
} else {
    Write-Warning "  packaging\Assets\ not found -- run scripts\create-assets.ps1 first."
}

# ---- 5. MakeAppx pack -------------------------------------------------------
Write-Host "`n[5/6] Packing MSIX..." -ForegroundColor Cyan

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory $OutDir | Out-Null }
if (Test-Path $msixPath) { Remove-Item $msixPath -Force }

& $makeAppx pack /d $publishDir /p $msixPath /nv
if ($LASTEXITCODE -ne 0) { Write-Error "MakeAppx failed (exit $LASTEXITCODE)." }
Write-Host "  Packed -> $msixPath"

# ---- 6. SignTool sign -------------------------------------------------------
Write-Host "`n[6/6] Signing MSIX..." -ForegroundColor Cyan

& $signTool sign `
    /fd   SHA256 `
    /sha1 $cert.Thumbprint `
    /tr   http://timestamp.digicert.com `
    /td   SHA256 `
    $msixPath

if ($LASTEXITCODE -ne 0) { Write-Error "SignTool failed (exit $LASTEXITCODE)." }
Write-Host "  Signed."

# ---- Summary ----------------------------------------------------------------
$size = (Get-Item $msixPath).Length / 1MB
Write-Host "`n=====================================================" -ForegroundColor Green
Write-Host "  EasyPDF.msix built successfully"                       -ForegroundColor Green
Write-Host "  Path : $msixPath"                                      -ForegroundColor Green
Write-Host "  Size : $([Math]::Round($size, 1)) MB"                  -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
Write-Host ""
Write-Host "To install on this machine:" -ForegroundColor Yellow
Write-Host "  1. Run with -InstallCert (once, elevated) to trust the dev cert."
Write-Host "  2. Double-click EasyPDF.msix  -or-"
Write-Host "     Add-AppxPackage `"$msixPath`""
Write-Host ""
