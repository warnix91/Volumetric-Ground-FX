[CmdletBinding()]
param([string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.18f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'sync_unity_shared.ps1')
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'UnityProject'
if (-not (Test-Path -LiteralPath $Unity)) { throw "Unity 2019.4.18f1 absent : $Unity" }
if (-not (Test-Path -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt'))) { throw 'Projet Unity absent' }
$log = Join-Path $repo 'UnityProject\Build\unity-bundle-build.log'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null
$args = @('-batchmode','-nographics','-quit',"-projectPath `"$project`"",'-executeMethod','GroundBlastFxBundleBuilder.Build',"-logFile `"$log`"")
$buildStarted = Get-Date
$process = Start-Process -FilePath $Unity -ArgumentList $args -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(900000)) { throw "Unity ne s'est pas terminé sous 900 s (PID $($process.Id)) ; voir $log" }
if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 80 }
    throw "Build Unity en échec ($($process.ExitCode))"
}
$bundle = Join-Path $repo 'GameData\Volumetric Ground FX\Shaders\GroundBlastFx.unity3d'
if (-not (Test-Path -LiteralPath $bundle)) { Get-Content -LiteralPath $log -Tail 80; throw 'Bundle manquant' }
if (-not (Select-String -LiteralPath $log -SimpleMatch '[GroundBlastFx] Bundle compilé :' -Quiet) -or
    (Get-Item -LiteralPath $bundle).LastWriteTime -lt $buildStarted.AddSeconds(-2)) {
    Get-Content -LiteralPath $log -Tail 50
    throw 'Unity a quitté sans compiler le bundle (vérifier la licence et le journal)'
}
Write-Host "[bundles] OK $bundle"
Write-Host "[bundles] SHA256 $((Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash)"
