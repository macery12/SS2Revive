[CmdletBinding()]
param(
    [string]$SteamUsername,
    [string]$InstallDirectory,
    [string]$InstallerDirectory,
    [switch]$RedownloadTool,
    [switch]$NoFolderPicker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$InstallerVersion = '3.1'

# Resolve all folders only after parameters have been parsed. This intentionally
# avoids using $PSScriptRoot inside the param block, which caused the old error.
if ([string]::IsNullOrWhiteSpace($InstallerDirectory)) {
    $scriptFile = $MyInvocation.MyCommand.Path

    if (-not [string]::IsNullOrWhiteSpace($scriptFile)) {
        $InstallerDirectory = Split-Path -Parent $scriptFile
    }
}

if ([string]::IsNullOrWhiteSpace($InstallerDirectory)) {
    $InstallerDirectory = (Get-Location).Path
}

$InstallerDirectory = [System.IO.Path]::GetFullPath($InstallerDirectory)

$BuildFolderName = 'Surgeon Simulator 2 - 1.3.7'

$DefaultInstallDirectory = [System.IO.Path]::Combine($InstallerDirectory, $BuildFolderName)

function Read-InstallDirectory {
    param([Parameter(Mandatory = $true)][string]$DefaultPath)

    Write-Host "Press Enter to accept: $DefaultPath" -ForegroundColor DarkGray

    $answer = Read-Host 'Install folder'

    if ([string]::IsNullOrWhiteSpace($answer)) {
        return $DefaultPath
    }

    # Explorer's "Copy as path" hands over a quoted string.
    return $answer.Trim().Trim('"')
}

function Select-InstallDirectory {
    param([Parameter(Mandatory = $true)][string]$DefaultPath)

    Write-Host ''
    Write-Host 'Where should Surgeon Simulator 2 1.3.7 be installed?' -ForegroundColor Cyan

    if ($NoFolderPicker) {
        return Read-InstallDirectory -DefaultPath $DefaultPath
    }

    # A folder picker needs a single-threaded apartment and a desktop to draw on.
    # Neither is guaranteed, so every failure here falls back to typing a path
    # rather than taking the installer down.
    try {
        if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
            throw 'not running in a single-threaded apartment'
        }

        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    }
    catch {
        Write-Host "Folder picker unavailable ($($_.Exception.Message)), type a path instead." -ForegroundColor DarkGray
        return Read-InstallDirectory -DefaultPath $DefaultPath
    }

    Write-Host 'Pick the folder to create the install in. Cancel to use the default.' -ForegroundColor DarkGray

    $owner  = $null
    $dialog = $null

    try {
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description         = "Choose where to create the '$BuildFolderName' folder."
        $dialog.ShowNewFolderButton = $true

        $startIn = Split-Path -Parent $DefaultPath
        if ($startIn -and (Test-Path -LiteralPath $startIn)) {
            $dialog.SelectedPath = $startIn
        }

        # Owned by a topmost window, otherwise the dialog can open behind the console
        # and look like the installer has hung.
        $owner = New-Object System.Windows.Forms.Form
        $owner.TopMost       = $true
        $owner.ShowInTaskbar = $false

        $result = $dialog.ShowDialog($owner)

        if ($result -ne [System.Windows.Forms.DialogResult]::OK -or
            [string]::IsNullOrWhiteSpace($dialog.SelectedPath)) {
            Write-Host "No folder chosen, using the default." -ForegroundColor DarkGray
            return $DefaultPath
        }

        $chosen = $dialog.SelectedPath

        # Picking the build folder itself is the obvious mistake to make here, and
        # appending blindly would bury the game one level deeper than intended.
        if ((Split-Path -Leaf $chosen) -eq $BuildFolderName) {
            return $chosen
        }

        return [System.IO.Path]::Combine($chosen, $BuildFolderName)
    }
    catch {
        Write-Host "Folder picker failed ($($_.Exception.Message)), type a path instead." -ForegroundColor DarkGray
        return Read-InstallDirectory -DefaultPath $DefaultPath
    }
    finally {
        if ($owner)  { $owner.Dispose() }
        if ($dialog) { $dialog.Dispose() }
    }
}

# -InstallDirectory still wins, so an unattended run never stops here.
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Select-InstallDirectory -DefaultPath $DefaultInstallDirectory
}

$InstallDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)

# Exact Surgeon Simulator 2 Steam depot build.
$AppId    = '774791'
$DepotId  = '774793'
$Manifest = '5729349529999704019'
$GameExe  = 'Surgeon Simulator 2.exe'

$ToolRoot = [System.IO.Path]::Combine($InstallerDirectory, '_tools', 'DepotDownloader')
$ToolExe  = [System.IO.Path]::Combine($ToolRoot, 'DepotDownloader.exe')

# The x64 build, pinned. The 32-bit one installs without complaining and then loads
# nothing at all, which is a miserable thing to debug.
$BepInExVersion = '5.4.23.2'
$BepInExUrl     = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Install-DepotDownloader {
    if ($RedownloadTool -and (Test-Path -LiteralPath $ToolRoot)) {
        Remove-Item -LiteralPath $ToolRoot -Recurse -Force
    }

    if (Test-Path -LiteralPath $ToolExe) {
        return
    }

    Write-Step 'Downloading the latest official DepotDownloader release'
    New-Item -ItemType Directory -Path $ToolRoot -Force | Out-Null

    $headers = @{
        'User-Agent' = 'SS2-1.3.7-Installer-V3'
        'Accept'     = 'application/vnd.github+json'
    }

    $release = Invoke-RestMethod `
        -Uri 'https://api.github.com/repos/SteamRE/DepotDownloader/releases/latest' `
        -Headers $headers

    $asset = $release.assets |
        Where-Object {
            $_.name -match '(?i)(windows|win)[-_.]?x64.*\.zip$' -or
            $_.name -match '(?i)depotdownloader.*\.zip$'
        } |
        Select-Object -First 1

    if (-not $asset) {
        throw 'Could not locate a compatible DepotDownloader ZIP in the latest GitHub release.'
    }

    $zipPath = [System.IO.Path]::Combine($env:TEMP, $asset.name)

    Invoke-WebRequest `
        -Uri $asset.browser_download_url `
        -OutFile $zipPath `
        -Headers $headers

    Expand-Archive -LiteralPath $zipPath -DestinationPath $ToolRoot -Force
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path -LiteralPath $ToolExe)) {
        $foundExe = Get-ChildItem -LiteralPath $ToolRoot -Filter 'DepotDownloader.exe' -Recurse -File |
            Select-Object -First 1

        if ($foundExe) {
            Copy-Item -LiteralPath $foundExe.FullName -Destination $ToolExe -Force

            # Copy adjacent runtime files when the executable was nested.
            Get-ChildItem -LiteralPath $foundExe.Directory.FullName -File | ForEach-Object {
                $destination = [System.IO.Path]::Combine($ToolRoot, $_.Name)
                if (-not (Test-Path -LiteralPath $destination)) {
                    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
                }
            }
        }
    }

    if (-not (Test-Path -LiteralPath $ToolExe)) {
        throw "DepotDownloader.exe was not found after extracting '$($asset.name)'."
    }
}

function Write-SteamAppId {
    param([Parameter(Mandatory = $true)][string]$GameFolder)

    $appIdPath = [System.IO.Path]::Combine($GameFolder, 'steam_appid.txt')

    # Written once and then left alone. Without it SteamAPI.Init() has no app id to
    # resolve outside a Steam launch, fails, and the game stops at "Authentication
    # Failed: Platform Authentication Error".
    if (Test-Path -LiteralPath $appIdPath) {
        Write-Host "steam_appid.txt already present, leaving it alone." -ForegroundColor DarkGray
        return
    }

    Write-Step 'Writing steam_appid.txt'
    [System.IO.File]::WriteAllText($appIdPath, $AppId)
    Write-Host "Wrote $appIdPath ($AppId)" -ForegroundColor Green
}

function Install-BepInEx {
    param([Parameter(Mandatory = $true)][string]$GameFolder)

    Write-Step "Installing BepInEx $BepInExVersion (x64)"

    $stagingRoot = [System.IO.Path]::Combine($env:TEMP, "ss2-bepinex-$([System.Guid]::NewGuid().ToString('N'))")
    $zipPath     = [System.IO.Path]::Combine($stagingRoot, "BepInEx_win_x64_$BepInExVersion.zip")
    $extractRoot = [System.IO.Path]::Combine($stagingRoot, 'extracted')

    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

    try {
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $zipPath -Headers @{
            'User-Agent' = "SS2-1.3.7-Installer-V$InstallerVersion"
        }

        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force

        # The zip has its payload at the root. Cope with a single wrapper folder anyway,
        # so a repackaged release does not silently install nothing.
        $payloadRoot = $extractRoot
        if (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($payloadRoot, 'winhttp.dll')))) {
            $nested = Get-ChildItem -LiteralPath $extractRoot -Filter 'winhttp.dll' -Recurse -File |
                Select-Object -First 1

            if (-not $nested) {
                throw 'winhttp.dll was not found in the BepInEx archive.'
            }

            $payloadRoot = $nested.Directory.FullName
        }

        Get-ChildItem -LiteralPath $payloadRoot -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $GameFolder -Recurse -Force
        }
    }
    finally {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    $loaderPath = [System.IO.Path]::Combine($GameFolder, 'winhttp.dll')
    if (-not (Test-Path -LiteralPath $loaderPath)) {
        throw 'BepInEx extracted, but winhttp.dll is not next to the game executable.'
    }

    # BepInEx only creates this on its first run. Making it now means the mod can be
    # dropped in before the game has ever been launched.
    $pluginFolder = [System.IO.Path]::Combine($GameFolder, 'BepInEx', 'plugins', 'SS2Revive')
    New-Item -ItemType Directory -Path $pluginFolder -Force | Out-Null

    Write-Host "BepInEx $BepInExVersion installed into $GameFolder" -ForegroundColor Green

    return $pluginFolder
}

try {
    Write-Host "Surgeon Simulator 2 1.3.7 Installer V$InstallerVersion" -ForegroundColor Green
    Write-Host "App: $AppId | Depot: $DepotId | Manifest: $Manifest"
    Write-Host "Installer folder: $InstallerDirectory"
    Write-Host "Install folder:   $InstallDirectory"

    Install-DepotDownloader

    if ([string]::IsNullOrWhiteSpace($SteamUsername)) {
        $SteamUsername = Read-Host 'Enter the Steam account login name that owns Surgeon Simulator 2'
    }

    if ([string]::IsNullOrWhiteSpace($SteamUsername)) {
        throw 'A Steam account login name is required.'
    }

    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null

    Write-Step "Downloading Surgeon Simulator 2 version 1.3.7"
    Write-Host 'DepotDownloader may prompt for your Steam password and Steam Guard.' -ForegroundColor Yellow
    Write-Host 'Your password is not saved in this installer script.' -ForegroundColor Yellow

    $downloadArguments = @(
        '-app', $AppId,
        '-depot', $DepotId,
        '-manifest', $Manifest,
        '-username', $SteamUsername,
        '-remember-password',
        '-dir', $InstallDirectory,
        '-validate'
    )

    & $ToolExe @downloadArguments
    $depotExitCode = $LASTEXITCODE

    if ($depotExitCode -ne 0) {
        throw "DepotDownloader exited with code $depotExitCode."
    }

    $installedExe = [System.IO.Path]::Combine($InstallDirectory, $GameExe)

    if (-not (Test-Path -LiteralPath $installedExe)) {
        $foundGameExe = Get-ChildItem -LiteralPath $InstallDirectory -Filter $GameExe -Recurse -File |
            Select-Object -First 1

        if ($foundGameExe) {
            $installedExe = $foundGameExe.FullName
        }
        else {
            throw "The depot download completed, but '$GameExe' was not found."
        }
    }

    $gameFolder = Split-Path -Parent $installedExe

    Write-SteamAppId -GameFolder $gameFolder
    $pluginFolder = Install-BepInEx -GameFolder $gameFolder

    $launcherPath = [System.IO.Path]::Combine($InstallDirectory, 'Launch Surgeon Simulator 2 - 1.3.7.cmd')

    $launcherLines = @(
        '@echo off',
        'cd /d "' + $gameFolder + '"',
        'start "" "' + $installedExe + '"'
    )
    $launcherLines | Set-Content -LiteralPath $launcherPath -Encoding ASCII

    $versionPath = [System.IO.Path]::Combine($InstallDirectory, 'BuildVersion.txt')
    $versionText = if (Test-Path -LiteralPath $versionPath) {
        (Get-Content -LiteralPath $versionPath -Raw).Trim()
    }
    else {
        'BuildVersion.txt was not present'
    }

    Write-Step 'Installation complete'
    Write-Host "Installed folder: $InstallDirectory" -ForegroundColor Green
    Write-Host "Game executable:  $installedExe" -ForegroundColor Green
    Write-Host "Reported build:   $versionText" -ForegroundColor Green
    Write-Host "Launcher:         $launcherPath" -ForegroundColor Green
    Write-Host ''
    Write-Host "The game and BepInEx $BepInExVersion are both installed. The mod is not." -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'To finish, put SS2Revive.dll, SS2Revive_Data.dll and the newsfeed folder in:' -ForegroundColor Cyan
    Write-Host "  $pluginFolder" -ForegroundColor White
    Write-Host ''
    Write-Host 'Both DLLs must sit side by side in that one folder. Then run the launcher above.' -ForegroundColor Cyan
    Write-Host 'To check it loaded, open BepInEx\LogOutput.log and look for [Info   :SS2 Revive].' -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'Steam must be running and signed in. Keep this folder separate from the' -ForegroundColor Yellow
    Write-Host 'normal Steam installation.' -ForegroundColor Yellow
}
catch {
    Write-Host "`nINSTALL FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    Write-Host "The first line should say: Surgeon Simulator 2 1.3.7 Installer V$InstallerVersion" -ForegroundColor Yellow
    Write-Host 'If it does not, Windows is still launching an older installer file.' -ForegroundColor Yellow
    exit 1
}
