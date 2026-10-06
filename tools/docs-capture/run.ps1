# The documentation's screenshots and animations, taken of the demo in Windows Sandbox rather than on anyone's own
# desktop: no browser window opens here, and nothing is clicked on this machine.
#
#   pwsh tools/docs-capture/run.ps1 [-Image talkwatch:ci]
#
# Starts the demo from the image (scripts/ci.sh builds talkwatch:ci) with a throwaway database, stages Node and
# Playwright for the Sandbox, which drives its own Edge headless, waits for it to finish, makes the GIFs here, and
# removes the demo. Results land in the staging folder's out\: review them before copying into docs/images.
# -Only call-detail takes that one picture again rather than the whole set; -Only guides takes the guides' pictures
# (the Analytics panels, the editors, the Configure pages, the scenario bar and the alert pop-up) in a few minutes.
param([string] $Image = 'talkwatch:ci', [int] $Port = 8088, [string] $Only)
$ErrorActionPreference = 'Stop'
$tool = $PSScriptRoot
$stage = Join-Path ([IO.Path]::GetTempPath()) 'talkwatch-docs-capture'
if (Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) { throw 'Windows Sandbox is already running; close it first' }

Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\node", "$stage\out" | Out-Null
Copy-Item (Get-Command node).Source "$stage\node\"
Copy-Item "$tool\package.json", "$tool\capture.mjs", "$tool\encode.mjs", "$tool\sandbox.ps1" $stage
"$Port" | Set-Content "$stage\port.txt"
if ($Only) { $Only | Set-Content "$stage\only.txt" }
Push-Location $stage
try { npm install --no-audit --no-fund --loglevel=error | Out-Host } finally { Pop-Location }

# The demo, given a few minutes' head start while the Sandbox boots: it makes a call a minute.
$env:TALKWATCH_IMAGE = $Image; $env:CAPTURE_PORT = "$Port"
docker compose -f "$tool\compose.yaml" up -d --wait | Out-Host
try {
    @"
<Configuration>
  <Networking>Enable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$stage</HostFolder>
      <SandboxFolder>C:\tw</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\tw\sandbox.ps1</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath "$stage\capture.wsb"

    # Straight after one has closed, Windows Sandbox can quietly fail to start, or start without running the logon
    # command; either leaves out\ empty. Check within a few minutes, and try once more after letting it settle.
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Start-Process "$stage\capture.wsb"
        $until = (Get-Date).AddMinutes(4)
        while (-not (Test-Path "$stage\out\sandbox.log") -and (Get-Date) -lt $until) { Start-Sleep -Seconds 5 }
        if (Test-Path "$stage\out\sandbox.log") { break }
        "Windows Sandbox did not start the capture (attempt $attempt)"
        Get-Process WindowsSandboxRemoteSession, WindowsSandboxServer -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 45
    }
    if (-not (Test-Path "$stage\out\sandbox.log")) { throw 'Windows Sandbox did not start twice running; close any Sandbox and run this again' }
    $deadline = (Get-Date).AddMinutes(30)
    while (-not (Test-Path "$stage\out\all.done")) {
        if ((Get-Date) -gt $deadline) { throw "no result after 30 minutes; see $stage\out\sandbox.log" }
        Start-Sleep -Seconds 5
    }
    Get-Content "$stage\out\sandbox.log", "$stage\out\capture.log" -ErrorAction SilentlyContinue
} finally {
    # The Sandbox is this script's own, and left open it stops the next run from starting.
    Get-Process WindowsSandboxRemoteSession, WindowsSandboxServer -ErrorAction SilentlyContinue | Stop-Process -Force
    docker compose -f "$tool\compose.yaml" down -v | Out-Host
}

if (Test-Path "$stage\out\frames") {
    Push-Location $stage
    try { node encode.mjs "$stage\out\frames" "$stage\out\gifs" } finally { Pop-Location }
}
"Results in $stage\out"
