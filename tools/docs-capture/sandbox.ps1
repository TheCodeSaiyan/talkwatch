# Runs inside Windows Sandbox, as its logon command: finds the host (the Sandbox's gateway), runs capture.mjs with the
# Node that run.ps1 put in the mapped folder, and says it is finished. Refuses to run anywhere but in the Sandbox, so
# it can never drive a browser on someone's own desktop.
$ErrorActionPreference = 'Stop'
$kit = 'C:\tw'
$out = Join-Path $kit 'out'
New-Item -ItemType Directory -Force $out | Out-Null
"started $(Get-Date -Format o)" | Set-Content (Join-Path $out 'sandbox.log')
if ($env:USERNAME -ne 'WDAGUtilityAccount') { 'not in Windows Sandbox; refusing' | Add-Content (Join-Path $out 'sandbox.log'); exit 1 }
try {
    $gateway = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop
    $port = Get-Content (Join-Path $kit 'port.txt')
    $env:TW_URL = "http://${gateway}:$port"
    $env:TW_OUT = $out
    if (Test-Path (Join-Path $kit 'only.txt')) { $env:TW_ONLY = (Get-Content (Join-Path $kit 'only.txt')).Trim() }
    "demo at $env:TW_URL" | Add-Content (Join-Path $out 'sandbox.log')
    & (Join-Path $kit 'node\node.exe') (Join-Path $kit 'capture.mjs') *>> (Join-Path $out 'sandbox.log')
} catch {
    $_ | Out-String | Add-Content (Join-Path $out 'sandbox.log')
} finally {
    New-Item -ItemType File (Join-Path $out 'all.done') -Force | Out-Null
}
