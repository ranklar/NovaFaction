#Requires -Version 5.1
<#
.SYNOPSIS
    Builds NovaFaction.Sim and copies it, with all content JSON, into the Unity client.

.DESCRIPTION
    Run this after ANY change to sim/ or content/. The Unity client does not compile the sim; it
    uses a prebuilt DLL, and it reads content from Resources, so both have to be pushed across.

    The script:
      1. builds sim/NovaFaction.Sim in Release,
      2. copies NovaFaction.Sim.dll into client/Assets/Plugins/NovaFaction/,
      3. mirrors content/**/*.json into client/Assets/Resources/content/, keeping the folder
         structure, and deletes files there that no longer exist in content/.

    It is idempotent. A file whose bytes already match is left alone, so Unity does not reimport
    it, and every copy, removal and skip is printed.

.PARAMETER Configuration
    Build configuration. Release (the default) is what ships; Debug is for a debugging session.

.PARAMETER SkipBuild
    Copy the DLL that is already in bin/ instead of building first.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\sync-to-unity.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$repo       = Split-Path -Parent $PSScriptRoot
$simProject = Join-Path $repo 'sim\NovaFaction.Sim\NovaFaction.Sim.csproj'
$pluginDir  = Join-Path $repo 'client\Assets\Plugins\NovaFaction'
$contentSrc = Join-Path $repo 'content'
$contentDst = Join-Path $repo 'client\Assets\Resources\content'

if (-not (Test-Path -LiteralPath $simProject)) {
    throw "Could not find $simProject. Run this script from inside the NovaFaction repo."
}
if (-not (Test-Path -LiteralPath $contentSrc)) {
    throw "Could not find $contentSrc."
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Copy-IfDifferent {
    <# Copies Source over Destination only when the bytes differ. Returns $true if it copied. #>
    param([string]$Source, [string]$Destination)

    $dir = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    if (Test-Path -LiteralPath $Destination) {
        $from = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
        $to   = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        if ($from -eq $to) { return $false }
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    return $true
}

function Remove-WithMeta {
    <# Deletes a file in the Unity project and the .meta Unity generated beside it. #>
    param([string]$Path)

    Remove-Item -LiteralPath $Path -Force
    $meta = $Path + '.meta'
    if (Test-Path -LiteralPath $meta) { Remove-Item -LiteralPath $meta -Force }
}

# ---------------------------------------------------------------------------
# 1. Build the sim
# ---------------------------------------------------------------------------
if ($SkipBuild) {
    Write-Host "Build   : skipped (-SkipBuild)."
} else {
    Write-Host "Building NovaFaction.Sim ($Configuration)..."
    & dotnet build $simProject -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

# ---------------------------------------------------------------------------
# 2. Copy the DLL into Assets/Plugins/NovaFaction
# ---------------------------------------------------------------------------
$dllSource = Join-Path $repo "sim\NovaFaction.Sim\bin\$Configuration\netstandard2.1\NovaFaction.Sim.dll"
if (-not (Test-Path -LiteralPath $dllSource)) {
    throw "No DLL at $dllSource. Build the sim first (drop -SkipBuild)."
}

$dllDest = Join-Path $pluginDir 'NovaFaction.Sim.dll'
if (Copy-IfDifferent $dllSource $dllDest) {
    $size = [math]::Round((Get-Item -LiteralPath $dllDest).Length / 1KB, 1)
    Write-Host "DLL     : copied NovaFaction.Sim.dll ($size KB) -> Assets/Plugins/NovaFaction/"
} else {
    Write-Host "DLL     : unchanged (Assets/Plugins/NovaFaction/NovaFaction.Sim.dll)."
}

# ---------------------------------------------------------------------------
# 3. Mirror content/**/*.json into Assets/Resources/content
# ---------------------------------------------------------------------------
$copied    = New-Object System.Collections.Generic.List[string]
$unchanged = 0
$wanted    = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)

$sourceFiles = @(Get-ChildItem -LiteralPath $contentSrc -Recurse -File -Filter *.json | Sort-Object FullName)
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($contentSrc.Length).TrimStart('\', '/')
    [void]$wanted.Add($relative)
    if (Copy-IfDifferent $file.FullName (Join-Path $contentDst $relative)) {
        $copied.Add($relative)
    } else {
        $unchanged++
    }
}

# Anything left in the destination that content/ no longer has is stale.
$removed = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $contentDst) {
    $destFiles = @(Get-ChildItem -LiteralPath $contentDst -Recurse -File -Filter *.json | Sort-Object FullName)
    foreach ($file in $destFiles) {
        $relative = $file.FullName.Substring($contentDst.Length).TrimStart('\', '/')
        if (-not $wanted.Contains($relative)) {
            Remove-WithMeta $file.FullName
            $removed.Add($relative)
        }
    }
}

Write-Host "Content : $($sourceFiles.Count) JSON file(s) in content/ -> Assets/Resources/content/"
foreach ($name in $copied)  { Write-Host "          copied   $name" }
foreach ($name in $removed) { Write-Host "          removed  $name (no longer in content/)" }
if ($unchanged -gt 0) { Write-Host "          unchanged $unchanged file(s)" }

Write-Host ""
Write-Host "Sync complete. In Unity, let the editor reimport (it will generate the .meta files)."
