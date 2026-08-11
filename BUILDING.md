# Building SS2 Revive from source

## Requirements

- Windows with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer
- Surgeon Simulator 2 build 1.3.7.3054 installed locally
- PowerShell for release packaging

BepInEx and Harmony dependencies restore from NuGet. Do not add game assemblies, extracted game data, credentials, or local installation paths to the repository.

## Configure the game path

The build first checks the Steam installation recorded in the Windows registry and the default Steam library. If the game is elsewhere, copy the local property template:

```powershell
Copy-Item Directory.Build.user.props.example Directory.Build.user.props
```

Edit `Directory.Build.user.props` so `GameDir` points to the folder containing `Surgeon Simulator 2.exe`. The local file is ignored by Git.

You can also provide the path for one build:

```powershell
dotnet build SS2Revive.sln -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Surgeon Simulator 2"
```

## Build the mod

From the repository root:

```powershell
dotnet build SS2Revive.sln -c Release
```

A successful build produces `SS2Revive.dll` and `SS2Revive_Data.dll`. If the selected game directory already contains BepInEx, the build also copies both assemblies and the news feed into `BepInEx\plugins\SS2Revive` for local testing.

## Run the data tests

```powershell
dotnet run --project tests\DataTests\DataTests.csproj -c Release
```

The runner exits non-zero when a check fails. Checks that require unavailable installed-game data report `SKIP` instead of failing on a clean development environment.

## Build and test Setup

Build the Windows installer project:

```powershell
dotnet build tools\Setup\SS2Revive.Setup.csproj -c Release
```

To verify the same self-contained, single-file output produced by the release workflow:

```powershell
dotnet publish tools\Setup\SS2Revive.Setup.csproj -c Release -r win-x64
.\tools\Setup\bin\Release\net8.0-windows\win-x64\publish\SS2Revive.Setup.exe --self-test
```

## Package a mod release

```powershell
.\pack.ps1
```

The packer builds the solution, runs the data tests, verifies that the game-compatible `net472` data assembly is selected, and writes `dist\SS2Revive-<version>.zip` with both DLLs and the news feed.

The version is defined once as `SS2ReviveVersion` in `Directory.Build.props`. Exact `vMAJOR.MINOR.PATCH` tags trigger the draft-release workflow, which requires the tag and project version to match.

## Repository layout

| Path | Purpose |
|---|---|
| `src/SS2Revive` | BepInEx plugin, Harmony patches, Steam transport, community UI, and game integration |
| `src/SS2Revive_Data` | Local data backend, UGC storage, bundle parsing, and persistence |
| `tests/DataTests` | Standalone data and installed-game compatibility checks |
| `tools/Setup` | Windows setup application |
| `assets/newsfeed` | Authored local news-feed configuration |
| `backend` | Independently operated community service source |
| `pack.ps1` | Verified mod ZIP packaging |

Backend deployment and production credentials are intentionally maintainer-managed and are not documented here.
