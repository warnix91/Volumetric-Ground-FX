<#
.SYNOPSIS
    Construit la livraison : build Release + tests, puis Release/VGFX-<version>.zip (contient uniquement GameData/GroundBlastFx).
.DESCRIPTION
    Depuis un clone propre : aucune autre étape nécessaire (les bundles de shaders sont versionnés).
    Exclus du zip : réglages et fichiers du joueur (Settings.cfg, LaunchSites_user.cfg, GroundBlastFx.log),
    fichiers de développement (DevAutomation.cfg, Captures/), .gitkeep, .pdb.
    Exemple : powershell -ExecutionPolicy Bypass -File Tools/package.ps1
#>
[CmdletBinding()]
param(
    [string]$KspRoot = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# 1. Build Release (+ tests unitaires du modèle)
$buildArgs = @{ Configuration = 'Release' }
if ($KspRoot) { $buildArgs.KspRoot = $KspRoot }
if (-not $SkipTests) { $buildArgs.RunTests = $true }
& (Join-Path $PSScriptRoot 'build_dll.ps1') @buildArgs

# 2. Version depuis GroundBlastFx.version
$src = Join-Path $repo 'GameData\GroundBlastFx'
$ver = Get-Content -Raw -LiteralPath (Join-Path $src 'GroundBlastFx.version') | ConvertFrom-Json
$version = "$($ver.VERSION.MAJOR).$($ver.VERSION.MINOR).$($ver.VERSION.PATCH)"

# 3. Contrôles
$bundles = @(Get-ChildItem -LiteralPath (Join-Path $src 'Shaders') -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne '.gitkeep' })
if ($bundles.Count -eq 0) {
    Write-Warning 'Aucun bundle de shaders dans GameData/GroundBlastFx/Shaders : le mod fonctionnera avec le NullRenderer (aucun effet visuel).'
}

# 4. Zip
$excludeNames = @('.gitkeep', 'Settings.cfg', 'LaunchSites_user.cfg', 'GroundBlastFx.log', 'DevAutomation.cfg')
$files = @(Get-ChildItem -LiteralPath $src -Recurse -File | Where-Object {
    $rel = $_.FullName.Substring($src.Length + 1)
    -not ($excludeNames -contains $_.Name) -and $_.Extension -ne '.pdb' -and -not $rel.StartsWith('PluginData\Captures', [StringComparison]::OrdinalIgnoreCase)
})
$outDir = Join-Path $repo 'Release'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zip = Join-Path $outDir "VGFX-$version.zip"
if (Test-Path -LiteralPath $zip) {
    $old = Join-Path $outDir ("VGFX-$version.previous-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')
    Move-Item -LiteralPath $zip -Destination $old
    Write-Host "[package] Ancienne archive conservée : $old"
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        $entry = 'GameData/GroundBlastFx/' + $f.FullName.Substring($src.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $archive.Dispose()
}
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Write-Host "[package] $zip — $($files.Count) fichier(s), version $version"
Write-Host "[package] SHA256 $hash"
