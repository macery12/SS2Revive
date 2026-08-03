<#
.SYNOPSIS
    Builds SS2Revive and assembles the release zip.

.DESCRIPTION
    Produces dist\SS2Revive-<version>.zip holding exactly what the README tells a player to copy
    into BepInEx\plugins\SS2Revive: both DLLs and the newsfeed folder.

    installCurrentVersion.ps1 finds it by asking GitHub for the latest release and taking the
    SS2Revive-*.zip attached to it, so the version in the filename does not have to be known
    ahead of time - only the shape of the name.

    The check that matters here is which SS2Revive_Data.dll goes in. That project multi-targets,
    and the netstandard2.0 output is the wrong one - it carries a reference to the netstandard
    facade, which the game does not ship. Mono then fails to resolve it, and because
    Telemetry.Service.ConfigureListeners walks GetExportedTypes() over every loaded assembly on
    the startup path, that failure takes down the whole shell: the game boots to a black screen
    with no main menu and a ReflectionTypeLoadException naming no types.

    The two files are byte-identical apart from that reference, so the mistake is invisible until
    someone launches the game. Hence the hash comparison below rather than a comment asking the
    next person to be careful.

.PARAMETER Configuration
    Build configuration. Release unless you have a reason.

.PARAMETER SkipBuild
    Package whatever is already in bin\. Only useful when iterating on this script.

.EXAMPLE
    .\pack.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

# The version lives in exactly one place; read it rather than taking it as a parameter, so a
# release cannot be built with a number that is not in the assemblies.
function Get-ModVersion {
    $propsPath = Join-Path $root 'Directory.Build.props'
    $props = [xml](Get-Content -LiteralPath $propsPath -Raw)

    $node = $props.SelectSingleNode('//SS2ReviveVersion')
    if (-not $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "SS2ReviveVersion is not set in $propsPath."
    }

    return $node.InnerText.Trim()
}

function Assert-SameFile {
    param(
        [Parameter(Mandatory = $true)][string]$Actual,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Because
    )

    if (-not (Test-Path -LiteralPath $Expected)) {
        throw "Expected build output is missing: $Expected"
    }

    $actualHash   = (Get-FileHash -LiteralPath $Actual   -Algorithm SHA256).Hash
    $expectedHash = (Get-FileHash -LiteralPath $Expected -Algorithm SHA256).Hash

    if ($actualHash -ne $expectedHash) {
        throw "$Because`n  packaged: $Actual`n  expected: $Expected"
    }
}

try {
    $version = Get-ModVersion
    Write-Host "SS2Revive packager - version $version, configuration $Configuration" -ForegroundColor Green

    if (-not $SkipBuild) {
        Write-Step 'Building'
        & dotnet build (Join-Path $root 'SS2Revive.sln') -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "dotnet build exited with code $LASTEXITCODE." }

        Write-Step 'Running the self-check'
        & dotnet run --project (Join-Path $root 'tests\DataTests') -c $Configuration --no-build
        if ($LASTEXITCODE -ne 0) { throw "The self-check failed (exit code $LASTEXITCODE). Not packaging." }
    }

    $pluginBin = Join-Path $root "src\SS2Revive\bin\$Configuration"
    $dataBin   = Join-Path $root "src\SS2Revive_Data\bin\$Configuration"

    $pluginDll = Join-Path $pluginBin 'SS2Revive.dll'
    $dataDll   = Join-Path $pluginBin 'SS2Revive_Data.dll'

    foreach ($required in @($pluginDll, $dataDll)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Missing build output: $required. Build first, or drop -SkipBuild."
        }
    }

    Write-Step 'Checking the packaged SS2Revive_Data.dll is the net472 build'
    Assert-SameFile `
        -Actual   $dataDll `
        -Expected (Join-Path $dataBin 'net472\SS2Revive_Data.dll') `
        -Because  'The SS2Revive_Data.dll about to be packaged is not the net472 build. Shipping the netstandard2.0 one boots the game to a black screen with no main menu.'
    Write-Host 'net472 confirmed.' -ForegroundColor Green

    $newsFeedSource = Join-Path $root 'assets\newsfeed'
    if (-not (Test-Path -LiteralPath (Join-Path $newsFeedSource 'NewsFeed.json'))) {
        throw "assets\newsfeed\NewsFeed.json is missing; the release would ship blank menu tiles."
    }

    Write-Step 'Assembling'
    $dist    = Join-Path $root 'dist'
    $staging = Join-Path $dist "SS2Revive-$version"
    $zip     = Join-Path $dist "SS2Revive-$version.zip"

    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    if (Test-Path -LiteralPath $zip)     { Remove-Item -LiteralPath $zip -Force }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    Copy-Item -LiteralPath $pluginDll -Destination $staging
    Copy-Item -LiteralPath $dataDll   -Destination $staging

    # The images are not in the repository - they are the game's own artwork, seeded at runtime
    # from the player's install - so only the authored JSON travels, plus the folder to drop
    # replacements into.
    $newsFeedTarget = Join-Path $staging 'newsfeed'
    New-Item -ItemType Directory -Path (Join-Path $newsFeedTarget 'images') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $newsFeedSource 'NewsFeed.json') -Destination $newsFeedTarget

    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

    Write-Step 'Done'
    Get-ChildItem -LiteralPath $staging -Recurse -File |
        ForEach-Object { Write-Host ("  " + $_.FullName.Substring($staging.Length + 1)) -ForegroundColor DarkGray }

    $size = [math]::Round((Get-Item -LiteralPath $zip).Length / 1KB, 1)
    Write-Host ""
    Write-Host "$zip ($size KB)" -ForegroundColor Green
    Write-Host "Its contents go in BepInEx\plugins\SS2Revive\." -ForegroundColor Cyan
    Write-Host "Attach it to a v$version release as-is; installCurrentVersion.ps1 finds it by" -ForegroundColor Cyan
    Write-Host "asking GitHub for the newest release and taking its SS2Revive-*.zip." -ForegroundColor Cyan
}
catch {
    Write-Host "`nPACKAGING FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
