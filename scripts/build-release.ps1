<#
.SYNOPSIS
  Builds the release artifacts: the portable zip, and (when a signing certificate is
  given) the MSIX package the Store and winget installs use.

.DESCRIPTION
  Two artifacts, two audiences:

    Portable zip  - unzip and run. No install, no trust prompts, no runtime setup.
                    This is what a user can use today.

    MSIX package  - the native Windows install (double-click, App Installer, Start menu
                    entry, automatic updates). It MUST be signed by a certificate the
                    operating system already trusts - a code-signing certificate from a
                    public CA, or the Microsoft Store's own signature after submission.
                    A self-signed certificate is for local development only: Windows
                    refuses to deploy such a package on a normal machine.

.PARAMETER Output
  Where the artifacts land. Defaults to artifacts\release inside the repository.

.PARAMETER CertificateThumbprint
  Thumbprint of a code-signing certificate in the current user's certificate store.
  Without it, only the portable zip is built.

.EXAMPLE
  .\scripts\build-release.ps1
  .\scripts\build-release.ps1 -CertificateThumbprint ABC123...
#>
[CmdletBinding()]
param(
    [string]$Output,
    [string]$CertificateThumbprint,
    [ValidateSet('x64', 'arm64')]
    [string]$Runtime = 'x64'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repo 'artifacts\release' }
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) { throw 'dotnet not found on PATH.' }

Write-Host "==> Publishing $Runtime self-contained"
$publishArgs = @(
    'publish', 'src\Koda\Koda.csproj',
    '-c', 'Release', '-r', "win-$Runtime", '--self-contained', 'true',
    ('-p:Platform=' + $Runtime), '--nologo', '-v', 'minimal'
)
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

$publish = Join-Path $repo "src\Koda\bin\$Runtime\Release\net10.0-windows10.0.26100.0\win-$Runtime\publish"
if (-not (Test-Path $publish)) { throw "publish folder not found: $publish" }

$manifestPath = Join-Path $repo 'src\Koda\Package.appxmanifest'
$version = ([xml](Get-Content $manifestPath -Raw)).Package.Identity.Version
$zip = Join-Path $Output "Koda-$version-win-$Runtime-portable.zip"
Write-Host "==> Packaging $zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("    {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))

if ($CertificateThumbprint) {
    Write-Host "==> Packaging the MSIX (signed with $CertificateThumbprint)"
    $msixArgs = @(
        'publish', 'src\Koda\Koda.csproj',
        '-c', 'Release', '-r', "win-$Runtime", '--self-contained', 'true',
        ('-p:Platform=' + $Runtime), '--nologo', '-v', 'minimal',
        '-p:EnableMsixTooling=true',
        '-p:WindowsPackageType=MSIX',
        '-p:PublishAppxPackage=true',
        '-p:AppxPackageSigningEnabled=true',
        "-p:PackageCertificateThumbprint=$CertificateThumbprint",
        '-p:AppxBundle=Never',
        "-p:AppxPackageDir=$(Join-Path $Output 'msix')\"
    )
    & $dotnet @msixArgs
    if ($LASTEXITCODE -ne 0) { throw "MSIX packaging failed ($LASTEXITCODE)" }
    Write-Host "    $Output\msix"
} else {
    Write-Host "==> No certificate given: skipping the MSIX. Pass -CertificateThumbprint to build it."
}

Write-Host ''
Write-Host 'Artifacts:'
Get-ChildItem $Output -Recurse -File | Where-Object { $_.Extension -in '.zip', '.msix' } |
    ForEach-Object { '    {0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
