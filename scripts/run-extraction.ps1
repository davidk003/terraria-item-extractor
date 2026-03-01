<#
.SYNOPSIS
    Builds and runs the 5-phase Terraria data extractor, then optionally validates the output.

.DESCRIPTION
    Automates the build, extraction, and (optionally) validation steps described in
    README.md. Extraction covers items, shimmer, recipes, npc_shops, and sprites.
    Each stage prints a clear banner so you can
    follow progress. Exits non-zero on build failure or extraction failure.

.PARAMETER TerrariaExe
    Required. Full path to Terraria.exe.
    Example: "C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe"

.PARAMETER OutputDir
    Optional. Directory where extractor outputs are written (JSON, CSV, and sprites/ PNGs).
    Default: StandaloneExtractor/Output (relative to the repo root).

.PARAMETER Validate
    Switch. When set, runs the Python validator after extraction and exits non-zero
    if validation reports FAIL.

.PARAMETER ValidateAfterExtraction
    Switch. Alias for -Validate. Runs validation immediately after extraction.

.PARAMETER ValidationJsonOut
    Optional. Path for the machine-readable validation report (JSON).
    Default: validation/validation-report.json

.PARAMETER ValidationMdOut
    Optional. Path for the human-readable validation report (Markdown).
    Default: validation/validation-report.md

.EXAMPLE
    Basic run:
    .\scripts\run-extraction.ps1 -TerrariaExe "C:\...\Terraria.exe"

.EXAMPLE
    Custom output directory:
    .\scripts\run-extraction.ps1 -TerrariaExe "C:\...\Terraria.exe" -OutputDir "C:\MyData\terraria"

.EXAMPLE
    With validation:
    .\scripts\run-extraction.ps1 -TerrariaExe "C:\...\Terraria.exe" -Validate

.EXAMPLE
    With validation (explicit option name):
    .\scripts\run-extraction.ps1 -TerrariaExe "C:\...\Terraria.exe" -ValidateAfterExtraction

.NOTES
    Exit codes:
      0 - All five phases passed (and validation passed, if -Validate or -ValidateAfterExtraction was used)
      1 - Build failed, extraction failed, or validation reported FAIL
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TerrariaExe,

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "",

    [Parameter(Mandatory = $false)]
    [switch]$Validate,

    [Parameter(Mandatory = $false)]
    [switch]$ValidateAfterExtraction,

    [Parameter(Mandatory = $false)]
    [string]$ValidationJsonOut = "",

    [Parameter(Mandatory = $false)]
    [string]$ValidationMdOut = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Resolve paths relative to the repository root. This makes the script work
# whether you run it from the repo root or from the scripts folder.
# ---------------------------------------------------------------------------

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Walk up from the script directory to find the repo root (the folder that
# contains StandaloneExtractor/).
function Find-RepoRoot {
    param([string]$StartDir)
    $current = $StartDir
    while ($current -ne "") {
        if (Test-Path (Join-Path $current "StandaloneExtractor")) {
            return $current
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $null
}

$RepoRoot = Find-RepoRoot -StartDir $ScriptDir
if (-not $RepoRoot) {
    Write-Error "Could not locate repo root (the folder containing StandaloneExtractor/). Run this script from inside the repository."
    exit 1
}

$ProjectFile = Join-Path $RepoRoot "StandaloneExtractor\StandaloneExtractor.csproj"
$ValidationScript = Join-Path $RepoRoot "validation\run_validation.py"

if ($OutputDir -eq "") {
    $OutputDir = Join-Path $RepoRoot "StandaloneExtractor\Output"
}

if ($ValidationJsonOut -eq "") {
    $ValidationJsonOut = Join-Path $RepoRoot "validation\validation-report.json"
}

if ($ValidationMdOut -eq "") {
    $ValidationMdOut = Join-Path $RepoRoot "validation\validation-report.md"
}

$ShouldValidate = $Validate -or $ValidateAfterExtraction

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Banner {
    param([string]$Message)
    $line = "=" * 50
    Write-Host ""
    Write-Host $line
    Write-Host "  $Message"
    Write-Host $line
    Write-Host ""
}

function Write-Step {
    param([string]$Message)
    Write-Host ">> $Message"
}

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------

Write-Banner "Terraria Data Extractor"
Write-Step "Phases: items, shimmer, recipes, npc_shops, sprites"

Write-Step "Checking Terraria.exe..."
if (-not (Test-Path $TerrariaExe)) {
    Write-Error "Terraria.exe not found at: $TerrariaExe`nPass the correct path with -TerrariaExe."
    exit 1
}
Write-Host "   Found: $TerrariaExe"

Write-Step "Checking project file..."
if (-not (Test-Path $ProjectFile)) {
    Write-Error "Project file not found: $ProjectFile`nMake sure you are running this from inside the repository."
    exit 1
}
Write-Host "   Found: $ProjectFile"

# ---------------------------------------------------------------------------
# Stage 1: Build
# ---------------------------------------------------------------------------

Write-Banner "Stage 1 of 3: Build"

Write-Step "Running: dotnet build"
dotnet build "$ProjectFile"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit code $LASTEXITCODE). Fix build errors before running extraction."
    exit 1
}

Write-Host ""
Write-Host "Build succeeded."

# ---------------------------------------------------------------------------
# Stage 2: Extract
# ---------------------------------------------------------------------------

Write-Banner "Stage 2 of 3: Extract"

Write-Step "Output directory: $OutputDir"
Write-Step "Running extractor..."

dotnet run --project "$ProjectFile" -- `
    --terraria "$TerrariaExe" `
    --output "$OutputDir"

$ExtractionExitCode = $LASTEXITCODE
if ($ExtractionExitCode -ne 0) {
    Write-Error "Extraction finished with exit code $ExtractionExitCode. One or more phases failed."
    Write-Host ""
    Write-Host "Check the phase logs for details:"
    Write-Host "  $OutputDir\_runtime\phase-results\"
    Write-Host ""
    Write-Host "See README.md section 7 (Troubleshooting) for help."
    exit 1
}

Write-Host ""
Write-Host "Extraction complete. Output files are in: $OutputDir"
Write-Host "  - Primary datasets: items/recipes/shimmer/npc_shops/sprite_manifest (JSON + CSV)"
Write-Host "  - Sprite PNGs: $OutputDir\sprites\items\ and $OutputDir\sprites\npcs\"

# ---------------------------------------------------------------------------
# Stage 3 (optional): Validate
# ---------------------------------------------------------------------------

if (-not $ShouldValidate) {
    Write-Banner "Done"
    Write-Host "All five phases passed."
    Write-Host ""
    Write-Host "To validate the output, re-run with -Validate (or -ValidateAfterExtraction), or run manually:"
    Write-Host "  python validation/run_validation.py ^"
    Write-Host "    --output-dir `"$OutputDir`" ^"
    Write-Host "    --json-out `"$ValidationJsonOut`" ^"
    Write-Host "    --md-out `"$ValidationMdOut`""
    Write-Host ""
    exit 0
}

Write-Banner "Stage 3 of 3: Validate"

Write-Step "Checking Python..."
$PythonCmd = $null
foreach ($candidate in @("python", "python3", "py")) {
    try {
        $version = & $candidate --version 2>&1
        if ($LASTEXITCODE -eq 0) {
            $PythonCmd = $candidate
            Write-Host "   Using: $candidate ($version)"
            break
        }
    } catch {
        # not found, try next
    }
}

if (-not $PythonCmd) {
    Write-Error "Python not found. Install Python 3.x and make sure it is on your PATH."
    exit 1
}

Write-Step "Running validation..."

& $PythonCmd "$ValidationScript" `
    --output-dir "$OutputDir" `
    --json-out "$ValidationJsonOut" `
    --md-out "$ValidationMdOut"

$ValidationExitCode = $LASTEXITCODE

Write-Host ""
if ($ValidationExitCode -eq 0) {
    Write-Banner "Done - PASS"
    Write-Host "Validation passed."
    Write-Host "Reports written to:"
    Write-Host "  $ValidationJsonOut"
    Write-Host "  $ValidationMdOut"
    Write-Host ""
    exit 0
} else {
    Write-Error "Validation reported FAIL (exit code $ValidationExitCode)."
    Write-Host ""
    Write-Host "Review the report for details:"
    Write-Host "  $ValidationMdOut"
    Write-Host ""
    exit 1
}
