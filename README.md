# SS2 Revive

SS2 Revive restores multiplayer, progression, Creation Mode, and community-made levels to **Surgeon Simulator 2** after the original online services shut down.

It replaces retired service calls locally, carries multiplayer over Steam, and reconnects the in-game level browser to an independently operated community catalogue. Your progression and levels remain on your computer, and this repository distributes no original game code or game assets.

> **Compatibility:** Windows x64 and Surgeon Simulator 2 build **1.3.7.3054** are required. Version 1.5.x removed the networking implementation this project restores and is not supported.

> **Unofficial fan project:** SS2 Revive is not affiliated with Bossa Studios or Curve Games. See the [license and disclaimer](#license-and-disclaimer).

## Features

- Restores sign-in and bypasses retired version, maintenance, and connection checks.
- Restores parties, Steam **Invite to Game**, **Join Game**, and peer-to-peer multiplayer.
- Restores voice chat and party text chat through Steam while retaining the in-game controls.
- Saves campaign grades, daily challenges, progression, and cosmetics locally.
- Restores Creation Mode for building, editing, saving, and playtesting levels.
- Adds public community-map browsing and downloads directly to Discover.
- Adds protected in-game publishing for every Steam-authenticated creator.
- Adds creator attribution, reporting, and owner controls for published maps.
- Restores the legacy maps preserved in the supported game's local archive.
- Supports direct `.ss2level` export and import for offline sharing.
- Replaces the retired news service with an editable local news feed.

## Installation

### Recommended setup

[**Download the latest SS2 Revive installer**](https://github.com/macery12/SS2Revive/releases/latest), then run `SS2Revive-Setup-<version>.exe`.

Setup asks for a separate installation folder and the Steam login name of an account that owns Surgeon Simulator 2. DepotDownloader handles the password and Steam Guard prompts in its own window; SS2 Revive Setup does not read or store those credentials. It then installs the supported game build, x64 BepInEx, and the latest SS2 Revive release without modifying your normal Steam installation.

The setup executable is currently unsigned, so Windows may display a SmartScreen warning. Keep Steam running when you play and start the installed game with the launcher created by Setup.

### Manual installation

Use this route if build 1.3.7.3054 is already installed or Setup could not fetch the mod release.

1. Install [BepInEx 5.4.23.2 x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.2) into the folder containing `Surgeon Simulator 2.exe`.
2. Launch the game once, then quit, so BepInEx creates its directories.
3. Download the mod ZIP from the [latest release](https://github.com/macery12/SS2Revive/releases/latest).

Extract the ZIP contents into:

```text
Surgeon Simulator 2\BepInEx\plugins\SS2Revive\
```

4. Confirm that `SS2Revive.dll`, `SS2Revive_Data.dll`, and the `newsfeed` folder are together in that directory.
5. Launch the game and check `BepInEx\LogOutput.log` for messages tagged `[Info   :SS2 Revive]`.

The x86 BepInEx build can install without reporting an error, but it will not load this 64-bit game.

## Common tasks

### Play multiplayer

Keep Steam running, then create or join a party through the normal in-game menus. Invite a friend with Steam's normal **Invite to Game** action, or have them select **Join Game** from the Steam Friends list.

Gameplay traffic travels peer-to-peer through Steam instead of the retired Bossa relay services.

Voice chat and party text chat also travel through Steam instead of the retired Vivox service. The existing **Off**, **Always On**, and **Push to Talk** modes, push-to-talk binding, volume sliders, speaking indicators, and per-player mute controls remain available in the normal game UI. Steam and Windows select the active microphone.

### Browse community maps

Open the terminal and browse Discover. Published community maps are loaded from `community.m12labs.net` and marked **ONLINE** with a community icon. Browsing and downloading public maps does not require a Steam website login.

Opening a community map downloads it before play. Bundles and thumbnails are size-bounded, checksum-verified, cached locally, and installed only after validation. If the community service is unavailable, local maps continue working and the last valid catalogue remains available.

### Publish and manage a map

Every Steam-authenticated player can publish. Steam login verifies that the browser account matches the account running the game; it does not require maintainer approval or placement on an upload allowlist.

1. Save a level that you created in Creation Mode.
2. Open its details and choose **PUBLISH**.
3. Review the local preflight result.
4. Complete Steam authentication in the system browser when prompted.
5. Return to the game while the bundle is quarantined, validated, and published.

The game never collects your Steam password. Republishing an owned map updates its existing community entry instead of creating a duplicate map identity. The service currently permits up to 10 upload reservations per UTC day and 25 community map identities per account.

Choose **YOUR MAPS** in Discover to view your currently published maps. **ONLINE OPTIONS** lets you unpublish a map or remove it from the public catalogue without deleting your editable local copy. Publishing that owned map again restores its existing community entry. You can also report another community map for maintainer review.

Use the **LOG IN** or **LOG OUT** button in Creation Mode to manage the linked community session. Steam persona names appear in the details for published maps.

### Export or import a map directly

Direct file sharing works without the community service.

1. Open one of your local maps and choose **EXPORT**.
2. Find the generated `.ss2level` bundle in:

   ```text
   %LOCALAPPDATA%\Bossa Studios\Surgeon Simulator 2\SS2Revive\export
   ```

3. Send the bundle and its copied 22-character share code to the recipient.
4. The recipient places the bundle in the adjacent `import` folder and opens Creation Mode.

Accepted files move to `import\imported`; rejected files move to `import\rejected`, with the reason written to the game log. A newer revision updates the same imported map in place. Older or conflicting data cannot silently overwrite it.

### Play bundled legacy maps

Choose **LEGACY MAPS** in Discover to browse levels preserved in the game's own `StreamingAssets\LegacyLevels` archive. These maps stay local and are never uploaded to the community service.

Legacy formats are accepted only when a file matches the archive shipped with the supported game installation. Arbitrary external maps in those older formats remain blocked.

These archived levels are preserved as-is. Some are prototypes or abandoned tests and may contain bugs, unfinished objectives, placeholder objects, unusual geometry, loading failures, or a black screen. SS2 Revive can expose and safely read the files already shipped with the game, but it cannot repair their original content.

## Saves and local files

The default SS2 Revive data directory is:

```text
%LOCALAPPDATA%\Bossa Studios\Surgeon Simulator 2\SS2Revive
```

It contains:

- `progress.json` and `progress.json.bak` for progression
- `levels\` for locally authored and installed levels
- `export\` and `import\` for direct map sharing
- cached community catalogue objects and protected authentication-session data

Progress is written atomically, and the previous version is retained as `progress.json.bak`. These files live outside the game and BepInEx directories, so verifying the game or reinstalling the mod does not remove them.

## Configuration

The configuration file is created after the first run:

```text
Surgeon Simulator 2\BepInEx\config\dev.ss2revive.core.cfg
```

Defaults provide the intended restored experience. The most useful settings are:

### Local data and Creation Mode

| Setting | Default | Purpose |
|---|---:|---|
| `Security.HardenLevelReader` | `true` | Bounds level allocations and restricts untrusted old formats. Leave enabled. |
| `Backend.Mode` | `Local` | Answers retired progression and inventory calls inside the mod. `Off` is diagnostic only. |
| `Backend.GrantAllCosmetics` | `true` | Unlocks catalogued cosmetics. Disable it to earn them through progression. |
| `Progression.SetLevelTo50OnNextLaunch` | `false` | One-shot restore that raises the signed-in local account to level 50 on the next launch, never lowers existing progress, then resets itself to `false`. Requires `Backend.Mode = Local`. |
| `Backend.SaveDirectory` | *(empty)* | Overrides the default SS2 Revive data directory. |
| `CreationMode.Enabled` | `true` | Enables local Creation Mode saves. |
| `CreationMode.LevelSharing` | `true` | Enables direct export/import and online publishing controls. |
| `CommunityMaps.ApiCatalogUrl` | `https://community.m12labs.net/v1/catalog` | Selects the public community catalogue. Leave empty for a fully local library. |

### Multiplayer and interface

| Setting | Default | Purpose |
|---|---:|---|
| `FreeForAll.Enabled` | `true` | Builds the Free-for-all queue from available levels. |
| `FreeForAll.IncludeGameLevels` | `true` | Uses bundled game levels when no suitable custom maps are installed. |
| `Party.SteamP2PTransport` | `true` | Carries multiplayer traffic through Steam P2P. |
| `VoiceChat.SteamReplacement` | `true` | Restores voice and party text chat through Steam while keeping the game's existing voice settings and controls. |
| `NewsFeed.Enabled` | `true` | Uses the local replacement news feed. |
| `Diagnostics.ProbeKey` | `F9` | Writes a diagnostic state dump to the BepInEx log. |

## Editing the news feed

Edit `BepInEx\plugins\SS2Revive\newsfeed\NewsFeed.json`, then restart the game.

- Each tile defines a title, subtitle, image filename, and optional `ClickUrl`.
- Images belong in `newsfeed\images\` as 512×289 PNG files.
- The game copies its installed artwork into the folder on first run; those assets are not distributed by this repository.
- Source builds should edit `assets\newsfeed\NewsFeed.json` instead.

## Troubleshooting and bug reports

When reporting a problem:

1. Reproduce it once on the latest SS2 Revive build.
2. Press **F9** for party, progression, cosmetic, or level-state problems.
3. Attach `Surgeon Simulator 2\BepInEx\LogOutput.log` to the GitHub issue.

The log contains your SteamID64. It identifies a Steam profile but cannot be used to sign in as you; redact it before posting if you prefer.

Common checks:

- A missing SS2 Revive section in the log usually means the wrong BepInEx architecture or plugin directory was used.
- A 1.5.x game installation remains unsupported even when BepInEx loads successfully.
- Both mod DLLs must remain beside each other in `BepInEx\plugins\SS2Revive`.

Report authentication, upload, or backend security vulnerabilities privately to `contact@macery12.xyz` instead of opening a public issue.

## Uninstallation

- To remove only the mod, delete `BepInEx\plugins\SS2Revive\`.
- To remove BepInEx too, also delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and the `BepInEx` directory.

SS2 Revive does not replace the normal Steam installation. Its local saves remain in the application-data directory unless you remove them separately.

## Contributing

Bug reports, compatibility findings, documentation corrections, and focused code changes are welcome.

- Open an issue before beginning a large behavioral or architectural change.
- Keep original game assemblies, decompiled source, credentials, and Bossa-owned assets out of commits.
- Follow [BUILDING.md](BUILDING.md) before submitting a pull request.
- Include reproduction steps and a log for runtime bugs.

## License and disclaimer

SS2 Revive source code is available under the [MIT License](LICENSE).

This is an unofficial, non-commercial fan modification. It is not affiliated with, endorsed by, sponsored by, or connected to Bossa Studios, Curve Games, or anyone else involved in Surgeon Simulator 2. All related trademarks, copyrighted game assets, and game code belong to their respective owners.

The repository contains no original game code and no game assets. The mod reads data from a copy of the game you own and exists to keep purchased functionality usable after the original online services were shut down.
