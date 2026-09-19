# stop-tests.ps1 — Claude Code Stop hook for NovaFaction.
#
# Runs when Claude Code wants to finish a turn. If the test suite fails, the
# hook exits with code 2, which tells Claude Code it is not allowed to stop yet
# and hands it the failing output. If nothing under the watched paths has
# changed since the last passing run, it exits immediately.
#
# Exit 0 = fine to stop.   Exit 2 = tests failing; keep working.

$ErrorActionPreference = "Continue"

$root = $env:CLAUDE_PROJECT_DIR
if (-not $root) { $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
Set-Location $root

# Claude Code passes the hook input as JSON on stdin. Read it so the pipe is
# drained; the fields are not needed for this hook's decision.
try { $null = [Console]::In.ReadToEnd() } catch {}

# Fingerprint the files that can change the test result.
$watched = @("sim","content","tools") | Where-Object { Test-Path $_ }
$stamp = (Get-ChildItem -Path $watched -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '__pycache__|\.egg-info|\\bin\\|\\obj\\|\\out\\|\\TestResults\\' } |
    Sort-Object FullName |
    ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n"
$sha = [System.Security.Cryptography.SHA1]::Create()
$hash = ([System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($stamp)))) -replace '-', ''

$marker = Join-Path $PSScriptRoot ".last-pass"
if ((Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $hash)) {
    exit 0
}

$output = dotnet test sim\NovaFaction.Sim.Tests --nologo -v q 2>&1 | ForEach-Object { "$_" }
$code = $LASTEXITCODE

if ($code -eq 0) {
    Set-Content -Path $marker -Value $hash
    exit 0
}

$tail = ($output | Select-Object -Last 40) -join "`n"
[Console]::Error.WriteLine("Tests are failing (exit code $code). Fix them before finishing.`n$tail")
exit 2
