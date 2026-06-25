# Strip game DLLs to reference-only assemblies for CI builds.
# Run once locally after a game update, then commit game-refs/.
#
# Usage: powershell -File tools/strip-game-refs.ps1 [-GameDir <path>]

param(
  [string]$GameDir = "P:\Steam\steamapps\common\Cairn"
)

$repoRoot = "$PSScriptRoot\.."
$outDir   = "$repoRoot\game-refs"

$refs = @(
  "MelonLoader\net6\MelonLoader.dll",
  "MelonLoader\net6\0Harmony.dll",
  "MelonLoader\net6\Il2CppInterop.Runtime.dll",
  "MelonLoader\net6\Il2CppInterop.Common.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppDOTween.dll",
  "MelonLoader\Il2CppAssemblies\Il2Cppmscorlib.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppObi.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppSystem.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.Cairn.Global.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.Cairn.Input.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.Cairn.Tweakables.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.Cairn.Utilities.SingletonBase.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.TGBTools.Common.Runtime.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.TGBTools.InputImage.Runtime.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.TGBTools.Localization.Runtime.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.TGBTools.PhotoMode.Runtime.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.TGBTools.StringId.Generated.Runtime.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.TGBTools.StringId.Runtime.dll",
  "MelonLoader\Il2CppAssemblies\Il2CppTheGameBakers.TGBTools.Tweakables.Runtime.dll",
  "MelonLoader\Il2CppAssemblies\Unity.Cinemachine.dll",
  "MelonLoader\Il2CppAssemblies\Unity.InputSystem.dll",
  "MelonLoader\Il2CppAssemblies\Unity.Mathematics.dll",
  "MelonLoader\Il2CppAssemblies\Unity.Splines.dll",
  "MelonLoader\Il2CppAssemblies\Unity.TextMeshPro.dll",
  "MelonLoader\Il2CppAssemblies\UnityEngine.AnimationModule.dll",
  "MelonLoader\Il2CppAssemblies\UnityEngine.CoreModule.dll",
  "MelonLoader\Il2CppAssemblies\UnityEngine.ImageConversionModule.dll",
  "MelonLoader\Il2CppAssemblies\UnityEngine.IMGUIModule.dll",
  "MelonLoader\Il2CppAssemblies\UnityEngine.InputLegacyModule.dll",
  "MelonLoader\Il2CppAssemblies\UnityEngine.PhysicsModule.dll",
  "MelonLoader\Il2CppAssemblies\UnityEngine.TextRenderingModule.dll",
  "MelonLoader\Il2CppAssemblies\UnityEngine.UI.dll",
  "MelonLoader\Il2CppAssemblies\UnityEngine.UIModule.dll",
  "Mods\CrossMenuLib.dll",
  "UserLibs\Newtonsoft.Json.dll"
)

$missing = $refs | Where-Object { -not (Test-Path "$GameDir\$_") }
if ($missing) {
  Write-Error "Missing from GameDir:`n$($missing -join "`n")"
  exit 1
}

Write-Host "Stripping $($refs.Count) assemblies into game-refs/ ..."

foreach ($rel in $refs) {
  $src    = "$GameDir\$rel"
  $dstDir = "$outDir\$(Split-Path $rel -Parent)"
  New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
  # -f writes as the exact input filename (no -publicized suffix)
  assembly-publicizer --strip-only -f -o $dstDir $src 2>&1 | Where-Object { $_ -match "Done|Error|strip" }
}

Write-Host "Done. Commit game-refs/ to enable CI builds."
