# Clean eval client for the CairnDevTools console.
#
# The dev console exposes  POST http://127.0.0.1:<port>/cmd?q=eval  with the C# in the request BODY,
# so there is NO URL-encoding of the code (the whole source of the curl/query-string lockups). This
# wrapper takes a port and a C# snippet (as a string or from a file) and POSTs it.
#
# Usage:
#   pwsh tools/eval.ps1 -Port 14200 -Code 'return 1+1;'
#   pwsh tools/eval.ps1 -Port 14201 -File scratch.cs
#   'return UnityEngine.Time.timeScale;' | pwsh tools/eval.ps1 -Port 14200
#
# The snippet is plain C# (Roslyn). End with an expression or `return <expr>;` to get a value back.
# Reference Cairn types as Il2Cpp.<Type>, Unity as UnityEngine.<Type>.

param(
    [Parameter(Mandatory = $true)][int]$Port,
    [string]$Code,
    [string]$File,
    [int]$TimeoutSec = 12,
    [string]$Cmd = "eval"
)

if ($File) { $Code = Get-Content -Raw -Path $File }
if (-not $Code) {
    # allow piped input
    $Code = [Console]::In.ReadToEnd()
}
if (-not $Code) { Write-Error "no code (use -Code, -File, or pipe)"; exit 1 }

$uri = "http://127.0.0.1:$Port/cmd?q=$Cmd"
try {
    $resp = Invoke-WebRequest -UseBasicParsing -Method Post -Uri $uri `
        -Body $Code -ContentType "text/plain; charset=utf-8" -TimeoutSec $TimeoutSec
    Write-Output $resp.Content
}
catch {
    Write-Output ("eval-client error: " + $_.Exception.Message)
    exit 1
}
