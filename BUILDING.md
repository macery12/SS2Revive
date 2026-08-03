# Building SS2 Revive from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (10 or newer) and a copy of the game
installed. BepInEx and HarmonyX come from NuGet, so there is nothing else to fetch.

```powershell
dotnet build SS2Revive.sln -c Release
```

The build finds the game through Steam. If it cannot, because your library lives on another drive
or you run the game outside Steam, copy `Directory.Build.user.props.example` to
`Directory.Build.user.props` and set `GameDir` in it. That filename is gitignored. You can also
pass the path directly:

```powershell
dotnet build SS2Revive.sln -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Surgeon Simulator 2"
```

A successful build copies both DLLs and the news feed straight into your `BepInEx\plugins\SS2Revive`
folder, so the edit-build-launch loop needs no copying by hand.

There is a self-check that runs the backend against your installed game files:

```powershell
dotnet run --project tests\DataTests
```

It prints a line per check and exits non-zero if any fail. Checks that need `Inventory.dat` report
`SKIP` rather than failing when no install is found.

## Making a release

```powershell
.\pack.ps1
```

Builds, runs the self-check, and writes `dist\SS2Revive-<version>.zip` containing exactly what the
install step in the README tells you to copy. It refuses to package anything if the self-check
fails.

Attach that file to the release as it is. `installCurrentVersion.ps1` asks GitHub for the newest
release and takes the `SS2Revive-*.zip` on it, so the version in the name never has to be told to
anything - but the `SS2Revive-` prefix does have to stay.

It also verifies that the `SS2Revive_Data.dll` going into the zip is the **net472** build. That
project multi-targets, the two outputs look interchangeable, and shipping the netstandard2.0 one
boots the game to a black screen with no main menu - the reason is at the top of
`src/SS2Revive_Data/SS2Revive_Data.csproj`. It is not a mistake you would catch by looking.

The version comes from `SS2ReviveVersion` in `Directory.Build.props`, which is the only place it
is written down. Both assemblies and the `[BepInPlugin]` attribute are generated from it, so the
number the log prints and the number on the zip cannot drift apart.

## Layout

| Path | What it is |
|---|---|
| `src/SS2Revive` | The plugin. Harmony patches, Steam lobbies, Steam P2P transport, news feed. |
| `src/SS2Revive_Data` | The backend, as a plain library. Request router, save file, challenge catalogue, JSON.|
| `tests/DataTests` | A console runner for the library. No test framework, no packages. |
| `assets/newsfeed` | The authored news feed that ships with a release. |
| `installCurrentVersion.ps1` | Installs build 1.3.7, BepInEx and the mod, into a folder of its own. |
| `pack.ps1` | Builds, self-checks and assembles the release zip. |
