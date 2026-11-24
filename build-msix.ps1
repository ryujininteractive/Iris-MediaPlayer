<#
.SYNOPSIS
    Builds the Microsoft Store package of Iris (MSIX).

.DESCRIPTION
    Builds Iris as an MSIX package (-p:IrisMsix=true) with the same code as the setup (.exe).
    The result lands in artifacts\msix:
    - Iris-MediaPlayer_<version>_x64.msixupload: the file to submit in Partner Center.
    - The .msix inside its folder: for a local test (see -Install).

    The package isn't signed here: the Store signs it. An unsigned package can only be installed
    on a PC with Developer Mode on, which is what -Install does.

    The version comes from <Version> in Iris-MediaPlayer\Iris-MediaPlayer.csproj. The Store needs
    four parts ending in 0, so 1.3.1 becomes 1.3.1.0. Package.appxmanifest is changed for the
    build and put back afterwards.

.PARAMETER Platform
    x64 (default) or ARM64.

.PARAMETER Install
    After building, install the package on this PC for testing (needs Developer Mode). It
    installs next to the setup version; remove it from Settings › Apps like any app.

.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -Install
#>
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',
    [switch] $Install
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$appProject = Join-Path $root 'Iris-MediaPlayer\Iris-MediaPlayer.csproj'
$manifestPath = Join-Path $root 'Iris-MediaPlayer\Package.appxmanifest'
$runtime = "win-$($Platform.ToLowerInvariant())"
$outDir = Join-Path $root 'artifacts\msix\'

# MSBuild from Visual Studio builds the WinUI app (the dotnet CLI can't compile its XAML).
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio with the WinUI application development workload.' }

[xml] $csproj = Get-Content $appProject
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "<Version> '$version' must look like 1.2.3." }
$packageVersion = "$version.0"

# Put the version into the manifest for this build only.
$originalManifest = [IO.File]::ReadAllText($manifestPath)
$versioned = [regex]::Replace($originalManifest, '(<Identity[^>]*?\sVersion=")[^"]*(")', "`${1}$packageVersion`${2}")
if ($versioned -eq $originalManifest -and $originalManifest -notmatch "Version=`"$([regex]::Escape($packageVersion))`"") {
    throw 'Could not find the Identity Version in Package.appxmanifest.'
}

try {
    [IO.File]::WriteAllText($manifestPath, $versioned, [Text.UTF8Encoding]::new($false))

    Write-Host "Building the Store package of Iris $packageVersion for $Platform..." -ForegroundColor Cyan
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    # Start from nothing: an incremental build can reuse an old upload package (with an old
    # identity or version) from bin\...\Upload.
    foreach ($folder in "bin\$Platform\Release", "obj\$Platform\Release") {
        $path = Join-Path $root "Iris-MediaPlayer\$folder"
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    & $msbuild $appProject -restore -v:minimal -nologo `
        -p:Configuration=Release -p:Platform=$Platform -p:RuntimeIdentifier=$runtime `
        -p:IrisMsix=true `
        -p:GenerateAppxPackageOnBuild=true `
        -p:UapAppxPackageBuildMode=StoreUpload `
        -p:AppxBundle=Never `
        -p:AppxPackageSigningEnabled=false `
        "-p:AppxPackageDir=$outDir"
    if ($LASTEXITCODE -ne 0) { throw 'Building the Store package failed.' }
}
finally {
    [IO.File]::WriteAllText($manifestPath, $originalManifest, [Text.UTF8Encoding]::new($false))
}

$upload = Get-ChildItem $outDir -Recurse -Filter *.msixupload | Select-Object -First 1
$msix = Get-ChildItem $outDir -Recurse -Filter *.msix | Select-Object -First 1
if (-not $upload -or -not $msix) { throw "No package found in $outDir." }

# Check the package inside the upload really carries this build's identity and version.
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Read-PackageIdentity([string] $msixPath) {
    $zip = [IO.Compression.ZipFile]::OpenRead($msixPath)
    try {
        $reader = [IO.StreamReader]::new(($zip.Entries | Where-Object FullName -eq 'AppxManifest.xml').Open())
        try { ([xml] $reader.ReadToEnd()).Package.Identity } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}
$expected = ([xml] $originalManifest).Package.Identity
$check = Join-Path $outDir 'upload-check'
$uploadZip = [IO.Compression.ZipFile]::OpenRead($upload.FullName)
try {
    New-Item -ItemType Directory -Force $check | Out-Null
    $inner = $uploadZip.Entries | Where-Object Name -like '*.msix' | Select-Object -First 1
    [IO.Compression.ZipFileExtensions]::ExtractToFile($inner, (Join-Path $check $inner.Name), $true)
}
finally { $uploadZip.Dispose() }
foreach ($package in (Join-Path $check $inner.Name), $msix.FullName) {
    $identity = Read-PackageIdentity $package
    if ($identity.Name -ne $expected.Name -or $identity.Publisher -ne $expected.Publisher -or $identity.Version -ne $packageVersion) {
        throw "$package has $($identity.Name) / $($identity.Publisher) / $($identity.Version); expected $($expected.Name) / $($expected.Publisher) / $packageVersion."
    }
}
Remove-Item $check -Recurse -Force
Write-Host ''
Write-Host "Submit this in Partner Center: $($upload.FullName)" -ForegroundColor Green
Write-Host "Package for testing:           $($msix.FullName)"

if ($Install) {
    # Developer Mode lets Windows register an unsigned package from its unpacked files. Unpack the
    # real .msix (not the build output folder, which lacks the icons) so the test copy is exactly
    # what the Store gets. Remove an earlier test copy first: its files are about to be replaced.
    $layout = Join-Path $root 'artifacts\msix-test-install'
    Get-AppxPackage -Name (([xml]$originalManifest).Package.Identity.Name) | Remove-AppxPackage
    if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($msix.FullName, $layout)
    # Package bookkeeping that a registered folder must not have.
    foreach ($name in 'AppxBlockMap.xml', 'AppxSignature.p7x', '[Content_Types].xml') {
        Remove-Item -LiteralPath (Join-Path $layout $name) -ErrorAction SilentlyContinue
    }
    Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceApplicationShutdown
    Write-Host 'Installed for testing. Find "Iris" in the Start menu.' -ForegroundColor Green
}
