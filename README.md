# SS2 Revive

**SS2 Revive brings Surgeon Simulator 2 back online.** It restores multiplayer, voice and text chat, progression, Creation Mode, and community-made levels after the original online services shut down.

The mod answers the retired service calls locally, carries multiplayer traffic over Steam, and reconnects the in-game level browser to an independently operated community catalogue. Your progression and levels stay on your own computer. This repository distributes no original game code and no game assets.

| | |
|---|---|
| **Current release** | 1.2.1 — [download the installer](https://github.com/macery12/SS2Revive/releases/latest) |
| **Supported game build** | Surgeon Simulator 2 **1.3.7.3054** |
| **Platform** | Windows x64, with Steam running |
| **Not supported** | Game version 1.5.x, which removed the networking implementation this project restores |

> **Unofficial fan project.** SS2 Revive is not affiliated with Bossa Studios or Curve Games. See [License and disclaimer](#license-and-disclaimer).

## What SS2 Revive restores

**Multiplayer**

- Sign-in, with the retired version, maintenance, and connection checks bypassed.
- Parties, Steam **Invite to Game** and **Join Game**, and peer-to-peer play over Steam.
- Voice chat and party text chat through Steam, using the game's existing voice controls.
- Free-for-all, with its queue built from the levels on your machine.

**Progression**

- Campaign grades, daily challenges, progression, and cosmetics, saved locally.
- A local replacement for the retired news feed on the main menu.

**Creation Mode and community maps**

- Creation Mode for building, editing, saving, and playtesting levels.
- Community map browsing and downloads in the in-game **Discover** terminal.
- In-game publishing for any Steam-authenticated creator, with attribution, reporting, and owner controls.
- Direct `.ss2level` export and import for sharing maps without the community service.
- The legacy maps preserved in the supported build's own local archive.

## Install

### Before you start

You need Windows x64, a Steam account that owns Surgeon Simulator 2, and Steam running while you play. Setup installs build 1.3.7.3054 into a folder of its own and leaves your normal Steam installation untouched.

### Option 1: Setup (recommended)

1. Download `SS2Revive-Setup-<version>.exe` from the [latest release](https://github.com/macery12/SS2Revive/releases/latest), then run it.
2. Choose an installation folder and enter the Steam login name of an account that owns the game.
3. Complete the password and Steam Guard prompts in DepotDownloader's own window.
4. Start the game with the `Launch Surgeon Simulator 2 - 1.3.7.cmd` launcher Setup creates in that folder.

Setup installs the supported game build, x64 BepInEx, and the latest SS2 Revive release. DepotDownloader handles Steam credentials in its own window; SS2 Revive Setup never reads or stores them.

The setup executable is currently unsigned, so Windows may show a SmartScreen warning.

### Option 2: Manual install

Use this route if build 1.3.7.3054 is already installed, or if Setup could not fetch the mod release.

1. Install [BepInEx 5.4.23.2 x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.2) into the folder containing `Surgeon Simulator 2.exe`.
2. Launch the game once, then quit, so BepInEx creates its directories.
3. Download the mod ZIP from the [latest release](https://github.com/macery12/SS2Revive/releases/latest).
4. Extract the ZIP contents into `Surgeon Simulator 2\BepInEx\plugins\SS2Revive\`.
5. Confirm that `SS2Revive.dll`, `SS2Revive_Data.dll`, and the `newsfeed` folder sit together in that directory.

The x86 BepInEx build installs without reporting an error, but it will never load this 64-bit game.

### Verify the install

Launch the game, then open `Surgeon Simulator 2\BepInEx\LogOutput.log` and look for a line like:

```text
[Info   :SS2 Revive] SS2 Revive 1.2.1 starting.
```

If no such line appears, the mod did not load. See [Troubleshooting](#troubleshooting).

## Play multiplayer

Keep Steam running, then create or join a party through the normal in-game menus. Invite people from your Steam Friends list: right-click a friend and choose **Invite to Game**, or have them choose **Join Game** on your profile.

Gameplay traffic travels peer-to-peer through Steam instead of the retired Bossa relay services. Voice chat and party text chat travel through Steam instead of the retired Vivox service.

The **Off**, **Always On**, and **Push to Talk** modes, the push-to-talk binding, volume sliders, speaking indicators, and per-player mute controls all remain in the normal game UI. Steam and Windows choose the active microphone.

## Community maps

### Browse and download maps

Open the in-game terminal and browse **Discover**. Published community maps load from the public community server hosted by the maintainer and are marked **ONLINE** with a community icon. Browsing and downloading public maps needs no Steam website login.

Opening a community map downloads it first. Bundles and thumbnails are size-bounded, checksum-verified, cached locally, and installed only after validation. If the community service is unavailable, local maps keep working and the last valid catalogue stays available.

### Publish and manage your maps

Every Steam-authenticated player can publish. Steam login only proves that the browser account matches the account running the game; it needs no maintainer approval and no upload allowlist.

1. Save a level you created in Creation Mode.
2. Open its details and choose **PUBLISH**.
3. Wait for local packaging and the checksum preflight to finish.
4. Complete Steam authentication in the system browser when prompted.
5. Return to the game while the bundle is quarantined, validated, and published.

The game never collects your Steam password. Republishing a map you own updates its existing community entry instead of creating a duplicate map identity. The service currently allows up to 10 upload reservations per UTC day and 25 community map identities per account.

Choose **YOUR MAPS** in Discover to see what you have published. **ONLINE OPTIONS** unpublishes a map or removes it from the public catalogue without deleting your editable local copy; publishing that map again restores its existing community entry. You can also report another creator's map for maintainer review.

Use **LOG IN** or **LOG OUT** in Creation Mode to manage the linked community session. Steam persona names appear in the details for published maps.

### Share a map as a file

Direct file sharing works without the community service.

1. Open one of your local maps and choose **EXPORT**.
2. Find the generated `.ss2level` bundle in:

   ```text
   %LOCALAPPDATA%\Bossa Studios\Surgeon Simulator 2\SS2Revive\export
   ```

3. Send the bundle and its copied 22-character share code to the recipient.
4. The recipient drops the bundle into the adjacent `import` folder and opens Creation Mode.

Accepted files move to `import\imported`; rejected files move to `import\rejected`, with the reason written to the game log. A newer revision updates the same imported map in place, and older or conflicting data can never silently overwrite it.

### Play the bundled legacy maps

Choose **LEGACY MAPS** in Discover to browse levels preserved in the game's own `StreamingAssets\LegacyLevels` archive. These maps stay local and are never uploaded to the community service.

A legacy-format file is accepted only when it matches the archive shipped with the supported game installation. Arbitrary external maps in those older formats stay blocked.

These levels are preserved exactly as they shipped. Some are prototypes or abandoned tests and may contain bugs, unfinished objectives, placeholder objects, unusual geometry, loading failures, or a black screen. SS2 Revive can expose and safely read the files already in your game installation, but it cannot repair their original content.

## Where your files live

The default SS2 Revive data directory is:

```text
%LOCALAPPDATA%\Bossa Studios\Surgeon Simulator 2\SS2Revive
```

It contains:

- `progress.json` and `progress.json.bak` for progression
- `levels\` for locally authored and installed levels
- `export\` and `import\` for direct map sharing
- cached community catalogue objects and protected authentication-session data

Progress is written atomically, and the previous version is kept as `progress.json.bak`. These files live outside the game and BepInEx directories, so verifying the game or reinstalling the mod does not remove them.

## Configuration

The configuration file appears after the first run:

```text
Surgeon Simulator 2\BepInEx\config\dev.ss2revive.core.cfg
```

The defaults give the intended restored experience, so you do not need to change anything. Every setting carries its own explanation in the file. The settings most worth knowing:

### Local data and Creation Mode

| Setting | Default | Purpose |
|---|---:|---|
| `Security.HardenLevelReader` | `true` | Bounds level allocations and restricts untrusted old formats. Leave enabled. |
| `Backend.Mode` | `Local` | Answers retired progression and inventory calls inside the mod. `Off` is diagnostic only. |
| `Backend.GrantAllCosmetics` | `true` | Reports catalogued cosmetics as owned. Disable it to earn them through progression instead. |
| `Backend.SaveDirectory` | *(empty)* | Overrides the default SS2 Revive data directory. Do not point it inside the game or BepInEx folder. |
| `Progression.SetLevelTo50OnNextLaunch` | `false` | One-shot restore that raises the signed-in local account to level 50 on the next launch, never lowers existing progress, then resets itself to `false`. Requires `Backend.Mode = Local`. |
| `CreationMode.Enabled` | `true` | Enables local Creation Mode saves. |
| `CreationMode.LevelSharing` | `true` | Enables direct export/import and the online publishing controls. |
| `CommunityMaps.ApiCatalogUrl` | *(preset)* | Points at the public community server hosted by the maintainer. Leave the default alone, or clear it for a fully local library. |

### Multiplayer, news feed, and diagnostics

| Setting | Default | Purpose |
|---|---:|---|
| `Party.SteamP2PTransport` | `true` | Carries multiplayer traffic through Steam P2P. |
| `Party.ShareLevelOverSteam` | `true` | Publishes your season level through Steam so party members see the real number. |
| `VoiceChat.SteamReplacement` | `true` | Restores voice and party text chat through Steam, keeping the game's own voice settings and controls. |
| `FreeForAll.Enabled` | `true` | Builds the Free-for-all queue from available levels. |
| `FreeForAll.IncludeGameLevels` | `true` | Falls back to bundled game levels when no suitable custom maps are installed. |
| `NewsFeed.Enabled` | `true` | Uses the local replacement news feed. |
| `NewsFeed.Url` | *(empty)* | Serves the feed from an `https://` URL instead of the local `newsfeed` folder. |
| `Diagnostics.ProbeKey` | `F9` | Writes a diagnostic state dump to the BepInEx log. |
| `Diagnostics.Verbose` | `true` | Includes live session and patient state in that dump. Leave it on for bug reports. |

### Edit the news feed

Edit `BepInEx\plugins\SS2Revive\newsfeed\NewsFeed.json`, then restart the game.

- Each tile defines a title, subtitle, image filename, and optional `ClickUrl`.
- Images belong in `newsfeed\images\` as 512×289 PNG files.
- The game copies its installed artwork into that folder on first run. This repository does not distribute those assets.
- If you build from source, edit `assets\newsfeed\NewsFeed.json` instead.

## Troubleshooting

### Common problems

| Symptom | Likely cause | Fix |
|---|---|---|
| No `[Info   :SS2 Revive]` lines in `LogOutput.log` | x86 BepInEx, or the plugin is in the wrong folder | Install BepInEx 5.4.23.2 **x64** and put both DLLs in `BepInEx\plugins\SS2Revive` |
| BepInEx loads, but nothing is restored | The installation is game version 1.5.x | Only build 1.3.7.3054 is supported |
| The mod loads, then fails around levels or saves | Only one of the two DLLs is present | `SS2Revive.dll` and `SS2Revive_Data.dll` must sit beside each other |
| Stuck on "requires an active internet connection" | `Bypass.ConnectionCheck` was turned off | Set it back to `true`. On 1.3.7 the game never reaches the rest of the plugin without it |
| Creation Mode hangs on a black screen | `CreationMode.Enabled` is `false` | Set it to `true`, so levels save locally instead of to the retired UGC service |
| Free-for-all drops you back to the lobby | The queue is empty | Enable `FreeForAll.Enabled` and `FreeForAll.IncludeGameLevels`, or install some maps |
| Discover shows no **ONLINE** maps | `CommunityMaps.ApiCatalogUrl` is empty, or the service is unreachable | Restore the default URL. If the service is down, local maps and the cached catalogue keep working |
| A legacy map opens to a black screen | The archived level is an unfinished prototype | Expected. Legacy content is preserved as shipped and the mod cannot repair it |

### Report a bug

1. Reproduce the problem once on the latest SS2 Revive build.
2. Press **F9** for party, progression, cosmetic, or level-state problems.
3. Attach `Surgeon Simulator 2\BepInEx\LogOutput.log` to a [GitHub issue](https://github.com/macery12/SS2Revive/issues).

The log contains your SteamID64, on the line reading `Local backend is serving STEAM-...`. It identifies a Steam profile but cannot be used to sign in as you; redact it if you prefer.

### Report a security issue

Report authentication, upload, or backend security vulnerabilities privately to `contact@macery12.xyz` instead of opening a public issue.

## Uninstall

- To remove only the mod, delete `BepInEx\plugins\SS2Revive\`.
- To remove BepInEx too, also delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and the `BepInEx` directory.

SS2 Revive does not replace your normal Steam installation. Its local saves stay in the application-data directory unless you delete them separately.

## Build from source

- [BUILDING.md](BUILDING.md) covers building the mod, running the data tests, and packaging a release.
- [backend/README.md](backend/README.md) covers the community service workspace, its local setup, and its security boundary.

## Contributing

Bug reports, compatibility findings, documentation corrections, and focused code changes are welcome.

- Open an issue before starting a large behavioral or architectural change.
- Keep original game assemblies, decompiled source, credentials, and Bossa-owned assets out of commits.
- Read [BUILDING.md](BUILDING.md) before submitting a pull request.
- Include reproduction steps and a log for runtime bugs.

## License and disclaimer

SS2 Revive source code is available under the [MIT License](LICENSE).

This is an unofficial, non-commercial fan modification. It is not affiliated with, endorsed by, sponsored by, or connected to Bossa Studios, Curve Games, or anyone else involved in Surgeon Simulator 2. All related trademarks, copyrighted game assets, and game code belong to their respective owners.

The repository contains no original game code and no game assets. The mod reads data from a copy of the game you own, and exists to keep purchased functionality usable after the original online services were shut down.
