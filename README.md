# TarkovHelper

[![en](https://img.shields.io/badge/lang-English-blue.svg)](README.md)
[![ko](https://img.shields.io/badge/lang-한국어-red.svg)](README.ko.md)
[![ja](https://img.shields.io/badge/lang-日本語-green.svg)](README.ja.md)
[![Latest release](https://img.shields.io/github/v/release/josephjang/TarkovHelper)](https://github.com/josephjang/TarkovHelper/releases/latest)

A Windows desktop companion for Escape from Tarkov that tracks your quest, hideout, and item progress, keeping everything in sync automatically by watching the game's own log files.

> **Note**: This is an independently maintained fork of [Zeliper/Tarkov-Item-Helper](https://github.com/Zeliper/Tarkov-Item-Helper). It ships its own releases, versioned CalVer (`YYYY.M.N`) starting at **v2026.7.0**, and continues to add features.

![Quest tracking in Tarkov Helper](screenshots/quests.png)

## Download

Get **TarkovHelper.zip** from the [latest release](https://github.com/josephjang/TarkovHelper/releases/latest), extract it anywhere, and run `TarkovHelper.exe`.

- **Windows** with the [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- The app asks for **administrator elevation** on launch; [How it works & safety](#how-it-works--safety) explains why

Once installed, the app keeps both itself and its game data up to date automatically.

## Features

- **Quests**: browse and search every quest; filter by status, trader, map, Kappa, and faction; see objectives, prerequisites, and follow-ups; get recommendations for what to play next
- **Hideout**: track station levels and see the items, traders, skills, and other stations each upgrade requires
- **Items**: one aggregated list of everything your quests and hideout upgrades still need, with FIR (Found in Raid) and non-FIR tracked separately against what you own
- **Collector**: a dedicated checklist for the Collector quest's items, and what still locks it
- **Map**: interactive maps with quest markers and extracts, including in-raid position tracking
- **Overlay minimap**: an always-on-top minimap for use while playing, controlled by global hotkeys
- **Game-log sync**: quest started/completed/failed states and game mode are picked up automatically from the game's log files
- **PvP/PvE profiles**: separate progress per mode, switched automatically to match the mode you're playing
- **Automatic updates**: the app updates itself and its game database in the background
- **Three languages**: English, 한국어, and 日本語, switchable in-app

## How it works & safety

Tarkov Helper obtains all game state **passively, by reading files the game itself writes**:

- **Log files**: quest and raid events and game mode come from the game's own logs
- **Screenshot filenames**: in-raid position comes from the game's screenshot feature, which encodes your coordinates in the filename

It does **not** read game memory, inject code, or modify any game files. The overlay minimap is an ordinary always-on-top window, and its global hotkeys use a system-wide keyboard hook running in Tarkov Helper's own process. That hook, together with log-file access, is why the app requests administrator elevation at launch.

No third-party tool can make promises on Battlestate Games' behalf, so use it at your own discretion.

## Getting started

### Game-log sync

Sync works out of the box: the app auto-detects your Tarkov installation (BSG launcher and Steam) and starts watching its logs. If your install isn't found, open **Settings** → **Tarkov Log Folder** and use **Auto Detect** or **Browse...** to point it at the game's `Logs` folder.

### Map position tracking

To update your position on the **Map** tab during a raid, press the game's screenshot key. The app reads the coordinates from the new screenshot's filename; nothing updates on its own without a screenshot.

### Where your progress is stored

Your progress lives in a `Config` folder next to `TarkovHelper.exe`. Each install location keeps its own data, so if you move to a new copy of the app and your progress looks empty, use **Settings** → **Data Migration** to import it from the previous location. Game data (quests, items, hideout) ships with the app and updates automatically; there is nothing to fetch manually.

## More screenshots

![Required items aggregation](screenshots/items.png)
![Hideout upgrade tracking](screenshots/hideout.png)

## Build from source

Requires the [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/josephjang/TarkovHelper.git
cd TarkovHelper
dotnet build TarkovHelper/TarkovHelper.csproj -c Release
```

Then launch `TarkovHelper\bin\Release\net8.0-windows\TarkovHelper.exe` and approve the elevation prompt. (`dotnet run` does not work from a non-elevated terminal, because the app's manifest requires administrator elevation.)

## License

[MIT License](LICENSE)

The bundled fonts under `TarkovHelper/Fonts/` are third-party works and are
**not** covered by the MIT grant: Play and Noto Sans CJK KR are licensed under
the SIL Open Font License 1.1, and Bender ships under the provenance notice in
`TarkovHelper/Fonts/LICENSE-Bender.txt`. See the `Fonts/LICENSE-*.txt` files
for the governing terms.

## Credits

- Original project: [Zeliper/Tarkov-Item-Helper](https://github.com/Zeliper/Tarkov-Item-Helper)
- Game data: [tarkov.dev](https://tarkov.dev)
- Escape from Tarkov is a trademark of Battlestate Games.
- Fonts: Bender (Jovanny Lemonad / TypeType), Play (OFL), Noto Sans CJK KR (OFL)
