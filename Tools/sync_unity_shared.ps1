<#
.SYNOPSIS
    Copie dans UnityProject/Assets/Editor/Shared les sources C# communes au jeu et au harnais Unity :
    contrat (Contracts.cs), modèle physique (Physics/*.cs) et cœur de rendu (Rendering/RenderCore.cs).
    Appelé automatiquement par build_bundles.ps1 et render-tests/run.ps1. Dossier généré, non versionné.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'Source\GroundBlastFx'
$dst = Join-Path $repo 'UnityProject\Assets\Editor\Shared'
New-Item -ItemType Directory -Force -Path $dst | Out-Null
$files = @(
    (Join-Path $src 'Contracts\Contracts.cs'),
    (Join-Path $src 'Contracts\VisualTuning.cs'),
    (Join-Path $src 'Rendering\RenderCore.cs'),
    (Join-Path $src 'Rendering\ThrustPulse.cs'),
    (Join-Path $src 'Rendering\PadIgnitionImpulse.cs'),
    (Join-Path $src 'Rendering\PadFlowLog.cs')
) + @(Get-ChildItem -LiteralPath (Join-Path $src 'Physics') -Filter '*.cs' | ForEach-Object FullName)
$wanted = @{}
foreach ($f in $files) {
    $name = Split-Path -Leaf $f
    $wanted[$name] = $true
    $target = Join-Path $dst $name
    if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $f).Hash) {
        Copy-Item -LiteralPath $f -Destination $target -Force
    }
}
# Fichiers partagés disparus des sources : retirés de la copie générée (et leur .meta).
foreach ($old in @(Get-ChildItem -LiteralPath $dst -Filter '*.cs')) {
    if (-not $wanted.ContainsKey($old.Name)) {
        Remove-Item -LiteralPath $old.FullName -Force
        if (Test-Path -LiteralPath ($old.FullName + '.meta')) { Remove-Item -LiteralPath ($old.FullName + '.meta') -Force }
    }
}
Write-Host "[unity-shared] $($files.Count) source(s) synchronisée(s) dans $dst"
