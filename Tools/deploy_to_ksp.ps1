<#
.SYNOPSIS
    Copie GameData/Volumetric Ground FX du dépôt dans l'installation KSP, sans rien toucher d'autre.
.DESCRIPTION
    
    - Refuse de tourner si KSP (cette installation) est ouvert.
    - Ne touche qu'à <KSP>/GameData/Volumetric Ground FX. Les réglages du joueur (PluginData/Settings.cfg,
      PluginData/LaunchSites_user.cfg) et le log ne sont jamais écrasés.
    - Les fichiers présents dans l'installation mais absents du dépôt (anciennes versions) sont déplacés
      dans <KSP>/_mods_desactives/GroundBlastFx_anciens/<horodatage>/ (jamais supprimés).
    - Vérifie chaque copie par SHA-256.
    Exemple : powershell -ExecutionPolicy Bypass -File Tools/deploy_to_ksp.ps1
#>
[CmdletBinding()]
param(
    [string]$KspRoot = '',
    # Compile d'abord (Tools/build_dll.ps1) avec cette configuration.
    [ValidateSet('', 'Debug', 'Release')]
    [string]$Build = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $KspRoot) { $KspRoot = $env:KSP_ROOT }
if (-not $KspRoot) { throw 'Indiquez -KspRoot ou la variable KSP_ROOT (installation de KSP 1.12).' }
$KspRoot = (Resolve-Path -LiteralPath $KspRoot).Path.TrimEnd('\')
$exe = Join-Path $KspRoot 'KSP_x64.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Pas une installation KSP : $KspRoot" }

foreach ($p in @(Get-Process -Name KSP_x64 -ErrorAction SilentlyContinue)) {
    if (-not $p.Path -or [string]::Equals($p.Path, $exe, [StringComparison]::OrdinalIgnoreCase)) {
        throw "KSP est ouvert (PID $($p.Id)). Fermez le jeu puis relancez le déploiement. Aucun fichier modifié."
    }
}

if ($Build) {
    & (Join-Path $PSScriptRoot 'build_dll.ps1') -Configuration $Build -KspRoot $KspRoot
}

$src = Join-Path $repo 'GameData\Volumetric Ground FX'
$dll = Join-Path $src 'Plugins\GroundBlastFx.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'GroundBlastFx.dll absente : lancez Tools/build_dll.ps1 (ou -Build Release).' }

$dst = Join-Path $KspRoot 'GameData\Volumetric Ground FX'
if ((Test-Path -LiteralPath $dst) -and ((Get-Item -LiteralPath $dst).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "Refus : $dst est un lien/jonction."
}
New-Item -ItemType Directory -Force -Path $dst | Out-Null

# Ancien nom du mod (GroundEffects, jusqu'à la 1.9.2) : son dossier est sorti de GameData (sinon KSP chargerait les deux
# DLL et chaque effet serait dessiné deux fois). Réglages du joueur repris, avec les nouveaux noms de nœuds.
$legacy = Join-Path $KspRoot 'GameData\GroundEffects'
if (Test-Path -LiteralPath $legacy) {
    $legacyStamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $legacyBackup = Join-Path $KspRoot "_mods_desactives\GroundEffects_ancien_nom_$legacyStamp"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $legacyBackup) | Out-Null
    Move-Item -LiteralPath $legacy -Destination $legacyBackup
    foreach ($name in @('Settings.cfg', 'LaunchSites_user.cfg')) {
        $oldFile = Join-Path $legacyBackup "PluginData\$name"
        $newFile = Join-Path $dst "PluginData\$name"
        if ((Test-Path -LiteralPath $oldFile) -and -not (Test-Path -LiteralPath $newFile)) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $newFile) | Out-Null
            $text = [IO.File]::ReadAllText($oldFile).Replace('GROUNDEFFECTS_', 'GROUNDBLASTFX_')
            [IO.File]::WriteAllText($newFile, $text, (New-Object Text.UTF8Encoding($false)))
        }
    }
    Write-Host "[deploy] Ancien dossier GameData\GroundEffects déplacé dans $legacyBackup (réglages repris)"
}

# Fichiers propres au joueur ou produits par le jeu : jamais écrasés, jamais déplacés.
$userFiles = @('PluginData\Settings.cfg', 'PluginData\LaunchSites_user.cfg', 'PluginData\GroundBlastFx.log', 'PluginData\DevAutomation.cfg')
function Test-UserFile([string]$rel) { return ($userFiles -contains $rel) -or $rel.StartsWith('PluginData\Captures\', [StringComparison]::OrdinalIgnoreCase) }

$srcFiles = @(Get-ChildItem -LiteralPath $src -Recurse -File | Where-Object { $_.Name -ne '.gitkeep' })
$wanted = @{}
$copied = 0
foreach ($f in $srcFiles) {
    $rel = $f.FullName.Substring($src.Length + 1)
    $wanted[$rel.ToLowerInvariant()] = $true
    $target = Join-Path $dst $rel
    if ((Test-UserFile $rel) -and (Test-Path -LiteralPath $target)) { continue }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $f.FullName -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash) {
        throw "Copie corrompue : $rel"
    }
    $copied++
}

# Fichiers obsolètes : déplacés hors de GameData (KSP charge tout ce qui est sous GameData).
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$moved = 0
foreach ($f in @(Get-ChildItem -LiteralPath $dst -Recurse -File)) {
    $rel = $f.FullName.Substring($dst.Length + 1)
    if ($wanted.ContainsKey($rel.ToLowerInvariant()) -or (Test-UserFile $rel)) { continue }
    $old = Join-Path $KspRoot "_mods_desactives\GroundBlastFx_anciens\$stamp\$rel"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $old) | Out-Null
    Move-Item -LiteralPath $f.FullName -Destination $old
    $moved++
}

$hash = (Get-FileHash -LiteralPath (Join-Path $dst 'Plugins\GroundBlastFx.dll') -Algorithm SHA256).Hash
Write-Host "[deploy] $copied fichier(s) copié(s) vers $dst"
if ($moved) { Write-Host "[deploy] $moved fichier(s) obsolète(s) déplacé(s) dans _mods_desactives\GroundBlastFx_anciens\$stamp" }
Write-Host "[deploy] GroundBlastFx.dll SHA256 $hash"
