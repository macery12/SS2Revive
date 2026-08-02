# SS2 Revive

Built against **Surgeon Simulator 2 build 1.3.1.276** on Windows. Other builds are untested.

> **Not affiliated with Bossa Studios or Curve Games.** This is an unofficial fan modification,
> made to keep a game working after its servers were retired. It ships no game code and no game
> assets. See the disclaimer at the bottom.

## What it restores

Sign-in and the version check no longer block startup. Parties, invites and joining run through
Steam lobbies, and in-game traffic goes peer to peer over Steam instead of through Bossa's relay.
Progression, daily challenges, campaign grades and the cosmetic inventory are served locally and
saved to your own machine. Creation Mode works again: levels are built, saved, playtested and kept
on your own disk rather than uploaded. The main menu news tiles read from a file you can edit.

## Requirements

- Surgeon Simulator 2 on Steam, Windows 64-bit
- BepInEx 5.4.23.2, x64. Direct download:
  [BepInEx_win_x64_5.4.23.2.zip](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip)
  ([all files for that release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.2))
- Steam running and signed in

BepInEx is not included here. Take the **x64** build: the 32-bit one installs without complaining
and then loads nothing at all.

## Install

**1. Install BepInEx.** Extract the zip into your game folder, the one containing the .exe. It is
usually at `steamapps\common\Surgeon Simulator 2`, and Steam will take you there with right click
on the game, then Manage, then Browse local files. When you are done, `winhttp.dll`,
`doorstop_config.ini` and a `BepInEx\` folder should be sitting next to the exe.

**2. Launch the game once through Steam, then quit.** This lets BepInEx create its `config` and
`plugins` folders and write a first `LogOutput.log`.

**3. Install the plugin.** Download the latest release and copy its contents into:

```
Surgeon Simulator 2\BepInEx\plugins\SS2Revive\
```

You want `SS2Revive.dll`, `SS2Revive_Data.dll` and the `newsfeed` folder, all in that one folder.
Both DLLs must sit side by side, because BepInEx resolves a plugin's dependencies from the folder
the plugin is in.

**4. Launch the game.** To confirm it loaded, open `BepInEx\LogOutput.log` and look for lines
tagged `[Info   :SS2 Revive]`.

## Uninstall

Delete `BepInEx\plugins\SS2Revive\` to remove the mod. To take BepInEx out as well, delete
`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder.

Nothing in the game's own files is ever modified, so a Steam file verification will not undo any
of this and will not complain about it either.

## Reporting a bug

Open an issue and attach `Surgeon Simulator 2\BepInEx\LogOutput.log` from a run where the problem
happened. Almost nothing here can be diagnosed without it. Pressing F9 in game writes a state dump
to that same log, which is worth doing for anything involving parties, progression or cosmetics.

The log includes your SteamID64. It identifies your Steam profile and cannot be used to sign in as
you, but replace the digits if you would rather not have it public.

## Where your progress is saved

```
%LOCALAPPDATA%\Bossa Studios\Surgeon Simulator 2\SS2Revive\progress.json
```

That sits beside the folders the game already writes to, and outside both the game directory and
the BepInEx directory, so verifying game files or reinstalling the mod cannot delete it. It is
plain JSON. Set `SaveDirectory` in the config if you want it somewhere else.

Levels you build in Creation Mode go in the `levels` folder next to it, one folder per level, each
holding the level's metadata as readable JSON alongside its saved revisions. Copying a level to
another machine means copying its folder.

## Configuration

The config file appears after the first run at `BepInEx\config\dev.ss2revive.core.cfg`. The
defaults are meant to be the right answer, but the ones worth knowing about:

| Setting | Default | What it does |
|---|---|---|
| `Backend.Mode` | `Local` | Where the game's dead HTTP calls get answered. `Local` answers them in process, which needs nothing running. `Off` leaves them failing the way they do without the mod. |
| `Backend.GrantAllCosmetics` | `true` | Unlocks every cosmetic set. Set to `false` to earn them through the reward track instead. |
| `Backend.SaveDirectory` | *(empty)* | Overrides where `progress.json` and your levels are written. |
| `CreationMode.Enabled` | `true` | Saves levels you build to this machine. Turning it off puts Creation Mode back to loading into a black screen, because the game will not open a new level until it has uploaded it. |
| `Party.SteamP2PTransport` | `true` | Sends gameplay traffic over Steam peer to peer. |
| `Party.InviteKey` | `F10` | Opens the Steam invite overlay. |
| `Party.ShareLevelOverSteam` | `true` | Publishes your season level to lobby members and friends. |
| `NewsFeed.Enabled` | `true` | Points the main menu tiles at the local feed. |
| `Diagnostics.ProbeKey` | `F9` | Dumps current state to the log. |

## Editing the news feed

The three tiles on the main menu are read from `BepInEx\plugins\SS2Revive\newsfeed\NewsFeed.json`.
Edit it and restart the game. Each tile takes a title, a subtitle, an image filename and an
optional `ClickUrl` that opens when the tile is clicked. Images go in `newsfeed\images\` and should
be 512x289 PNG.

The game's own tile artwork is copied in the first time the mod runs, from your installation, so
the tiles are never blank to start with. Those images belong to Bossa and are not distributed here.

If you are building from source, edit `assets\newsfeed\NewsFeed.json` instead. The build copies it
into place, so that copy is the one that ends up in a release.

## Building from source

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

### Layout

| Path | What it is |
|---|---|
| `src/SS2Revive` | The plugin. Harmony patches, Steam lobbies, Steam P2P transport, news feed. |
| `src/SS2Revive_Data` | The backend, as a plain library. Request router, save file, challenge catalogue, JSON.|
| `tests/DataTests` | A console runner for the library. No test framework, no packages. |
| `assets/newsfeed` | The authored news feed that ships with a release. |

## Disclaimer

SS2 Revive is an unofficial, non-commercial fan modification. It is not affiliated with,
endorsed by, sponsored by or connected to Bossa Studios, Curve Games, or anyone else involved in
making or publishing Surgeon Simulator 2. All trademarks and copyrights belong to their respective
owners.

The repository contains no game code and no game assets. The mod reads data from the copy of the
game you already own, on your own machine, and modifies nothing the game installed. It exists so
that a game people paid for keeps working after its servers were switched off.

