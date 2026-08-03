<#
.SYNOPSIS
    Installs Surgeon Simulator 2 build 1.3.7, BepInEx, and SS2 Revive.

.DESCRIPTION
    Steam serves 1.5.x, which SS2 Revive cannot work on - that build removed the netcode the mod
    restores. This downloads 1.3.7 from Steam's depot for an account that owns the game, into a
    self-contained folder that leaves your Steam copy alone.

    DepotDownloader prompts for the password and Steam Guard code itself; nothing about your
    credentials passes through this script.

    The mod is fetched from the latest GitHub release. If there is not one yet, or GitHub cannot be
    reached, the game and BepInEx are still installed and the script says what to do by hand.

.PARAMETER SteamUsername
    Steam login name. Prompted for when not given.

.PARAMETER InstallDirectory
    Where to install. Prompted for when not given.

.PARAMETER NoPause
    Do not wait for a keypress at the end. For unattended runs.

.EXAMPLE
    .\installCurrentVersion.ps1
#>
[CmdletBinding()]
param(
    [string]$SteamUsername,
    [string]$InstallDirectory,
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$InstallerVersion = '4.0'

# Exact Surgeon Simulator 2 Steam depot build.
$AppId    = '774791'
$DepotId  = '774793'
$Manifest = '5729349529999704019'
$GameExe  = 'Surgeon Simulator 2.exe'

$BuildFolderName = 'Surgeon Simulator 2 - 1.3.7'

# The x64 build, pinned. The 32-bit one installs without complaining and then loads nothing at
# all, which is a miserable thing to debug.
$BepInExVersion = '5.4.23.2'
$BepInExUrl     = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"

$ModRepo = 'macery12/SS2Revive'

$UserAgent = @{ 'User-Agent' = "SS2Revive-Installer/$InstallerVersion" }

$InstallerDirectory = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$ToolRoot = Join-Path $InstallerDirectory '_tools\DepotDownloader'
$ToolExe  = Join-Path $ToolRoot 'DepotDownloader.exe'

# ---------------------------------------------------------------------------- helpers

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Wait-ForClose {
    if ($NoPause) { return }
    Write-Host ''
    # Guarded: a redirected or closed stdin must not turn "finished" into a crash.
    try { Read-Host 'Press Enter to close' | Out-Null } catch { }
}

<#
    Downloads a zip and copies its payload into $Destination.

    All three things this script installs arrive the same way, and the only thing that differs is
    a file that proves the extraction worked. $Marker is that file: it is looked for at the root
    first, then anywhere below it, so a release that grew or lost a wrapper folder still installs
    rather than silently copying nothing.
#>
function Install-RemoteZip {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Marker,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $staging = Join-Path $env:TEMP ("ss2revive-" + [System.Guid]::NewGuid().ToString('N'))
    $zip     = Join-Path $staging 'download.zip'
    $extract = Join-Path $staging 'extracted'

    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    try {
        Invoke-WebRequest -Uri $Uri -OutFile $zip -Headers $UserAgent
        Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force

        $payload = $extract
        if (-not (Test-Path -LiteralPath (Join-Path $payload $Marker))) {
            $found = Get-ChildItem -LiteralPath $extract -Filter $Marker -Recurse -File |
                Select-Object -First 1

            if (-not $found) { throw "$Marker was not found in the archive from $Uri" }
            $payload = $found.Directory.FullName
        }

        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        Get-ChildItem -LiteralPath $payload -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
        }
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

<#
    Finds the download URL of one asset on a repository's newest release.

    GitHub's releases/latest is the newest published release, and it skips drafts and
    pre-releases - so this always lands on the current version without the version being named
    anywhere in this script.

    $Prefer is not a nicety. DepotDownloader publishes one zip per platform and the first one
    alphabetically is a Linux build, so "any zip" would install something that cannot run here.
    $AllowAnyZip relaxes that for our own releases, which carry a single file.
#>
function Get-LatestReleaseZipUrl {
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][string]$Prefer,
        [switch]$AllowAnyZip
    )

    $headers = $UserAgent + @{ 'Accept' = 'application/vnd.github+json' }
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers

    $zips = @(@($release.assets) | Where-Object { $_.name -like '*.zip' })
    if ($zips.Count -eq 0) { throw "release $($release.tag_name) has no .zip attached" }

    $asset = $zips | Where-Object { $_.name -like $Prefer } | Select-Object -First 1
    if (-not $asset -and $AllowAnyZip) { $asset = $zips[0] }
    if (-not $asset) {
        throw "release $($release.tag_name) has no asset matching '$Prefer' (found: " +
              (($zips | ForEach-Object { $_.name }) -join ', ') + ")"
    }

    return [pscustomobject]@{
        Tag  = $release.tag_name
        Name = $asset.name
        Url  = $asset.browser_download_url
    }
}

# ---------------------------------------------------------------------------- install

try {
    Write-Host "SS2 Revive installer V$InstallerVersion" -ForegroundColor Green
    Write-Host "Surgeon Simulator 2 build 1.3.7  (app $AppId, depot $DepotId)"

    if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
        $default = Join-Path $InstallerDirectory $BuildFolderName
        Write-Host ''
        Write-Host 'Where should the game be installed?' -ForegroundColor Cyan
        Write-Host "Press Enter for: $default" -ForegroundColor DarkGray

        $answer = Read-Host 'Install folder'

        # Explorer's "Copy as path" hands over a quoted string.
        $InstallDirectory = if ([string]::IsNullOrWhiteSpace($answer)) {
            $default
        }
        else {
            $answer.Trim().Trim('"')
        }
    }

    $InstallDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
    Write-Host "Installing to: $InstallDirectory"

    if ([string]::IsNullOrWhiteSpace($SteamUsername)) {
        $SteamUsername = Read-Host 'Steam login name of the account that owns Surgeon Simulator 2'
    }
    if ([string]::IsNullOrWhiteSpace($SteamUsername)) {
        throw 'A Steam account login name is required.'
    }

    if (-not (Test-Path -LiteralPath $ToolExe)) {
        Write-Step 'Downloading DepotDownloader'
        # The self-contained Windows x64 build, so there is no .NET runtime to install first.
        $tool = Get-LatestReleaseZipUrl -Repo 'SteamRE/DepotDownloader' -Prefer '*windows-x64*'
        Write-Host "$($tool.Name) from $($tool.Tag)" -ForegroundColor DarkGray
        Install-RemoteZip -Uri $tool.Url -Marker 'DepotDownloader.exe' -Destination $ToolRoot
    }

    Write-Step 'Downloading Surgeon Simulator 2 1.3.7'
    Write-Host 'DepotDownloader will ask for your Steam password and Steam Guard code.' -ForegroundColor Yellow
    Write-Host 'They are handled by that tool and are not saved by this script.' -ForegroundColor Yellow

    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null

    & $ToolExe -app $AppId -depot $DepotId -manifest $Manifest `
               -username $SteamUsername -remember-password `
               -dir $InstallDirectory -validate
    if ($LASTEXITCODE -ne 0) { throw "DepotDownloader exited with code $LASTEXITCODE." }

    $exe = Get-ChildItem -LiteralPath $InstallDirectory -Filter $GameExe -Recurse -File |
        Select-Object -First 1
    if (-not $exe) { throw "The download finished, but '$GameExe' was not found." }

    $gameFolder = $exe.Directory.FullName

    # Without this, SteamAPI.Init has no app id to resolve outside a Steam launch, fails, and the
    # game stops at "Authentication Failed: Platform Authentication Error". Written once.
    $appIdFile = Join-Path $gameFolder 'steam_appid.txt'
    if (-not (Test-Path -LiteralPath $appIdFile)) {
        [System.IO.File]::WriteAllText($appIdFile, $AppId)
    }

    Write-Step "Installing BepInEx $BepInExVersion (x64)"
    Install-RemoteZip -Uri $BepInExUrl -Marker 'winhttp.dll' -Destination $gameFolder

    $pluginFolder = Join-Path $gameFolder 'BepInEx\plugins\SS2Revive'

    Write-Step 'Installing SS2 Revive'
    $modInstalled = $false
    try {
        # Whatever the newest release carries. The zip is named for its version, so the pattern
        # matches the shape rather than a number this script would otherwise have to be kept in
        # step with; -AllowAnyZip covers a release that named it something else entirely.
        $mod = Get-LatestReleaseZipUrl -Repo $ModRepo -Prefer 'SS2Revive-*.zip' -AllowAnyZip
        Install-RemoteZip -Uri $mod.Url -Marker 'SS2Revive.dll' -Destination $pluginFolder
        $modInstalled = $true
        Write-Host "Installed SS2 Revive $($mod.Tag) ($($mod.Name))." -ForegroundColor Green
    }
    catch {
        # Not fatal. A playable 1.3.7 with BepInEx on it is most of the work, and the mod is three
        # files the player can drop in themselves once a release exists.
        New-Item -ItemType Directory -Path $pluginFolder -Force | Out-Null
        Write-Host "Could not fetch a release from github.com/$ModRepo" -ForegroundColor Yellow
        Write-Host "  $($_.Exception.Message)" -ForegroundColor DarkGray
        Write-Host 'The game and BepInEx are installed; only the mod is missing.' -ForegroundColor Yellow
    }

    $launcher = Join-Path $InstallDirectory 'Launch Surgeon Simulator 2 - 1.3.7.cmd'
    @(
        '@echo off',
        'cd /d "' + $gameFolder + '"',
        'start "" "' + $exe.FullName + '"'
    ) | Set-Content -LiteralPath $launcher -Encoding ASCII

    Write-Step 'Done'
    Write-Host "Game:     $gameFolder" -ForegroundColor Green
    Write-Host "Launcher: $launcher" -ForegroundColor Green

    if ($modInstalled) {
        Write-Host ''
        Write-Host 'Run the launcher to play. Steam must be running and signed in.' -ForegroundColor Cyan
        Write-Host 'To check the mod loaded, open BepInEx\LogOutput.log and look for' -ForegroundColor Cyan
        Write-Host '[Info   :SS2 Revive].' -ForegroundColor Cyan
    }
    else {
        Write-Host ''
        Write-Host 'To finish, put SS2Revive.dll, SS2Revive_Data.dll and the newsfeed folder in:' -ForegroundColor Cyan
        Write-Host "  $pluginFolder" -ForegroundColor White
        Write-Host 'Both DLLs must sit side by side in that one folder.' -ForegroundColor Cyan
    }

    Write-Host ''
    Write-Host 'This install is separate from Steam. Steam still has 1.5.x and will not' -ForegroundColor Yellow
    Write-Host 'touch or update this copy.' -ForegroundColor Yellow

    Wait-ForClose
}
catch {
    Write-Host "`nINSTALL FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Wait-ForClose
    exit 1
}
