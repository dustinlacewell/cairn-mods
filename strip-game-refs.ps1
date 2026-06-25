param([string]$GameDir = "P:\Steam\steamapps\common\Cairn")
powershell -File "$PSScriptRoot\tools\strip-game-refs.ps1" -GameDir $GameDir
