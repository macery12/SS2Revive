# SS2 Revive

Restores multiplayer, progression, and Creation Mode to **Surgeon Simulator 2** after Bossa's servers went offline.

> **Not affiliated with Bossa Studios or Curve Games.** Unofficial fan mod. Ships no game code, no game assets. See [Disclaimer](#disclaimer).

- **Requires build:** 1.3.7.3054, Windows 64-bit
- **1.5.x will NOT work** — the offline patch removed the netcode this mod restores. If Steam is serving you 1.5.x, see [Getting Build 1.3.7](#getting-build-137).

---

## Quick Start

**New install:**
```powershell
.\installCurrentVersion.ps1
```
Enter your Steam login when prompted — [DepotDownloader](https://github.com/SteamRE/DepotDownloader) handles the password/Guard code and fetches the game itself. This creates a **separate folder**; your normal Steam copy (1.5.x) is untouched. Launch with the generated `Launch Surgeon Simulator 2 - 1.3.7.cmd`.

**Already have 1.3.7 installed?** Skip to [Install](#install), step 3.

---

## What It Restores

- **Sign-in & version check** — no longer blocks startup
- **Parties & invites** — via Steam lobbies
- **Multiplayer traffic** — peer-to-peer over Steam, not Bossa's relay
- **Progression** — daily challenges, campaign grades, cosmetics all served & saved locally
- **Creation Mode** — build, save, and playtest levels to your own disk
- **Main menu news tiles** — reads from a local, editable file

---

## Requirements

- Surgeon Simulator 2 on Steam, Windows 64-bit, **build 1.3.7**
- BepInEx 5.4.23.2, **x64** — [direct download](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip) ([all files](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.2))
- Steam running and signed in

> ⚠️ Get the **x64** build specifically — the 32-bit one installs without complaining, then loads nothing.

---

## Getting Build 1.3.7

Steam serves 1.5.x by default, which won't work with this mod.

- **New install** → run `installCurrentVersion.ps1` (see [Quick Start](#quick-start))
- **Already have 1.3.7** → skip to [Install](#install), step 3

<details>
<summary>Why 1.5.x doesn't work / how the installer works</summary>

1.5.x removed the netcode the mod attaches to. The mod will detect this, log it, and stop rather than half-working.

`installCurrentVersion.ps1` does the whole install — game, BepInEx, and the mod. It asks where to install, then for the Steam login of the account that owns the game, and hands that off to DepotDownloader, which prompts for the password and Steam Guard code itself. **Nothing about your credentials is stored by this script** — it only fetches a build of a game the account already owns.

The result is a separate, self-contained folder, not a change to your Steam copy. Steam keeps serving 1.5.x to your library and is unaware of this folder. The installer also writes `steam_appid.txt` — without it, `SteamAPI.Init` has no app ID to resolve outside a Steam launch, and the game stops at "Authentication Failed: Platform Authentication Error."

Steam still needs to be running and signed in when you play — parties, invites, and P2P traffic all go through it.

</details>

---

## Install

*Only needed if you skipped the installer, or it couldn't fetch a release (in which case steps 1–2 are already done).*

1. **Install BepInEx.** Extract the zip into your game folder (the one with the `.exe` — usually `steamapps\common\Surgeon Simulator 2`; right-click the game in Steam → Manage → Browse local files). You should end up with `winhttp.dll`, `doorstop_config.ini`, and a `BepInEx\` folder next to the exe.
2. **Launch the game once through Steam, then quit.** This lets BepInEx create its `config`/`plugins` folders and a first `LogOutput.log`.
3. **Install the plugin.** Download the latest release and copy its contents into:
   ```
   Surgeon Simulator 2\BepInEx\plugins\SS2Revive\
   ```
   You need `SS2Revive.dll`, `SS2Revive_Data.dll`, and the `newsfeed` folder — all together in that one folder. Both DLLs must sit side by side (BepInEx resolves a plugin's dependencies from its own folder).
4. **Launch the game.** Confirm it loaded by checking `BepInEx\LogOutput.log` for lines tagged `[Info   :SS2 Revive]`.

---

## Uninstall

- **Mod only:** delete `BepInEx\plugins\SS2Revive\`
- **Mod + BepInEx:** also delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and the `BepInEx` folder

Nothing in the game's own files is ever modified — a Steam file verification won't undo this and won't complain about it either.

---

## Reporting a Bug

- Open an issue and attach `Surgeon Simulator 2\BepInEx\LogOutput.log` from the run where it happened. Almost nothing can be diagnosed without it.
- Press **F9** in-game to write a state dump to that same log — do this for anything involving parties, progression, or cosmetics.
- The log includes your **SteamID64** (identifies your profile, can't be used to sign in as you). Redact it if you'd rather not have it public.

---

## Where Your Progress Is Saved

```
%LOCALAPPDATA%\Bossa Studios\Surgeon Simulator 2\SS2Revive\progress.json
```

- Sits outside both the game and BepInEx directories, so verifying game files or reinstalling the mod can't delete it
- Plain JSON — override the location with `SaveDirectory` in the config
- Each save replaces the file in one atomic step and keeps the previous version as `progress.json.bak` — a power loss mid-write costs nothing. If `progress.json` gets damaged, delete it and rename `.bak` over it
- Creation Mode levels live in the `levels` folder next to it, one folder per level (metadata + revisions as readable JSON). Copy the folder to move a level to another machine

---

## Configuration

Config file appears after first run at `BepInEx\config\dev.ss2revive.core.cfg`. Defaults are the intended experience — the ones worth knowing about:

| Setting | Default | What it does |
|---|---|---|
| `Bypass.ConnectionCheck` | `true` | Skips asking the shut-down server for permission to start. Disabling this on 1.3.7+ leaves you stuck at the "requires an active internet connection" box. |
| `Backend.Mode` | `Local` | Where the game's dead HTTP calls get answered. `Local` answers in-process. `Off` is diagnostic-only — disables progression/challenges/cosmetics rather than restoring them. |
| `Backend.GrantAllCosmetics` | `true` | Unlocks every cosmetic set. Set `false` to earn them via the reward track. |
| `Backend.SaveDirectory` | *(empty)* | Overrides where `progress.json` and levels are written. |
| `CreationMode.Enabled` | `true` | Saves built levels to this machine. Off = Creation Mode loads into a black screen (game won't open a level until it's "uploaded"). |
| `FreeForAll.Enabled` | `true` | Fills the Free-for-all queue from levels on this machine. Off = empty queue (Bossa served this from published community levels). |
| `FreeForAll.IncludeGameLevels` | `true` | Lets Free-for-all fall back to campaign levels when your library has nothing that fits. No FFA level ships with the game. |
| `Party.SteamP2PTransport` | `true` | Sends gameplay traffic over Steam peer-to-peer. |
| `Party.InviteKey` | `F10` | Opens the Steam invite overlay. |
| `Party.ShareLevelOverSteam` | `true` | Publishes your season level to lobby members/friends. |
| `NewsFeed.Enabled` | `true` | Points the main menu tiles at the local feed. |
| `Diagnostics.ProbeKey` | `F9` | Dumps current state to the log. |
| `Diagnostics.Verbose` | `true` | Includes live session/patient state in that dump — leave on for bug reports. |

---

## Editing the News Feed

Edit `BepInEx\plugins\SS2Revive\newsfeed\NewsFeed.json` and restart the game.

- Each tile: title, subtitle, image filename, optional `ClickUrl`
- Images go in `newsfeed\images\`, **512×289 PNG**
- The game's own artwork is copied in on first run, so tiles are never blank to start (those images belong to Bossa and aren't distributed here)
- Building from source? Edit `assets\newsfeed\NewsFeed.json` instead — see [BUILDING.md](BUILDING.md)

---

## Building From Source

Covered in [BUILDING.md](BUILDING.md).

---

## Disclaimer

SS2 Revive is an unofficial, non-commercial fan modification. It is not affiliated with, endorsed by, sponsored by, or connected to Bossa Studios, Curve Games, or anyone else involved in making or publishing Surgeon Simulator 2. All trademarks and copyrights belong to their respective owners.

The repository contains no game code and no game assets. The mod reads data from the copy of the game you already own, on your own machine, and modifies nothing the game installed. It exists so that a game people paid for keeps working after its servers were switched off.
