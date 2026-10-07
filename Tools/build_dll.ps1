<#
.SYNOPSIS
    Compile GroundBlastFx.dll et la copie dans GameData/Volumetric Ground FX/Plugins/.
.DESCRIPTION
    Le build doit rester vert.
    Exemples :
        powershell -ExecutionPolicy Bypass -File Tools/build_dll.ps1
        powershell -ExecutionPolicy Bypass -File Tools/build_dll.ps1 -Configuration Release -RunTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    # Installation KSP dont on référence les DLL (-KspRoot ou $env:KSP_ROOT).
    [string]$KspRoot = '',
    # Lance aussi les tests unitaires du modèle physique (Tests/GroundBlastFx.Tests).
    [switch]$RunTests
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $KspRoot) { $KspRoot = $env:KSP_ROOT }
if (-not $KspRoot) { throw 'Indiquez -KspRoot ou la variable KSP_ROOT (installation de KSP 1.12).' }
if (-not (Test-Path -LiteralPath (Join-Path $KspRoot 'KSP_x64_Data\Managed\Assembly-CSharp.dll'))) {
    throw "Installation KSP introuvable ou incomplète : $KspRoot"
}

$proj = Join-Path $repo 'Source\GroundBlastFx\GroundBlastFx.csproj'
$outDir = Join-Path $repo "Source\GroundBlastFx\bin\$Configuration"
Write-Host "[build] $Configuration contre $KspRoot"
& dotnet build $proj -c $Configuration -nologo -v minimal "-p:KSPRoot=$KspRoot"
if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation (code $LASTEXITCODE)" }

$plugins = Join-Path $repo 'GameData\Volumetric Ground FX\Plugins'
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Copy-Item -LiteralPath (Join-Path $outDir 'GroundBlastFx.dll') -Destination $plugins -Force
$pdbTarget = Join-Path $plugins 'GroundBlastFx.pdb'
if ($Configuration -eq 'Debug') {
    Copy-Item -LiteralPath (Join-Path $outDir 'GroundBlastFx.pdb') -Destination $plugins -Force
} elseif (Test-Path -LiteralPath $pdbTarget) {
    Remove-Item -LiteralPath $pdbTarget -Force   # fichier produit par un build Debug précédent, non versionné
}
$hash = (Get-FileHash -LiteralPath (Join-Path $plugins 'GroundBlastFx.dll') -Algorithm SHA256).Hash
Write-Host "[build] OK -> GameData\Volumetric Ground FX\Plugins\GroundBlastFx.dll"
Write-Host "[build] SHA256 $hash"

if ($RunTests) {
    $tests = Join-Path $repo 'Tests\GroundBlastFx.Tests\GroundBlastFx.Tests.csproj'
    if (Test-Path -LiteralPath $tests) {
        Write-Host '[tests] Tests unitaires du Core'
        & dotnet run --project $tests -c Release -nologo
        if ($LASTEXITCODE -ne 0) { throw "Tests en échec (code $LASTEXITCODE)" }
    } else {
        Write-Host '[tests] Aucun projet de tests trouvé.'
    }
}
