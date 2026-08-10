# Palworld Server Launcher

[![Latest release](https://img.shields.io/github/v/release/SSyl/PalworldServerLauncher?include_prereleases&style=for-the-badge&color=green)](https://github.com/SSyl/PalworldServerLauncher/releases/latest) [![License: GPLv3](https://img.shields.io/github/license/SSyl/PalworldServerLauncher?color=blue&style=for-the-badge)](LICENSE) [![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/Z8X4237D8A)

A Windows app for running a **Palworld dedicated server**: installs it, keeps it updated, handles scheduled
restarts and backups, and watches its health, all through Palworld's REST API. Native C# / WPF, single `.exe`.
Inspired by [Conan Exiles' Dedicated Server Launcher](https://forums.funcom.com/t/introducing-the-conan-exiles-dedicated-server-app/21699).

**[Download the latest release](https://github.com/SSyl/PalworldServerLauncher/releases/latest)** to get started.

Other languages are available: German, Spanish, French, Brazilian Portuguese, Russian, Japanese, Simplified
Chinese, Traditional Chinese, and Korean. See [Languages](#languages).

![The Palworld Server Launcher main window](docs/images/app-screenshots/main-window.png)

---

## Contents

- [Features](#features)
- [Getting started](#getting-started)
- [FAQ and troubleshooting](#faq-and-troubleshooting)
- [Advanced usage](#advanced-usage)
- [Privacy and security](#privacy-and-security)

---

## Features

### Install and update
- Installs SteamCMD and the server for you, and **Start** checks for an update first (can be turned off).
- **Auto-updates when a new build drops** and restarts gracefully to apply it, with no version to configure.
- **Pin the server to its current build** to pause updates when a game update breaks something.
- **Check for Update** (safe while running) and **Validate Files** buttons.
- **Import an existing server** from elsewhere, copied in so the launcher can manage it, original left alone.

### Restarts and recovery
- **Scheduled restarts** at times you set, with a minimum-uptime guard so a fresh server isn't bounced.
- **In-game warnings** before a restart, at marks you choose, skipped when nobody is online.
- **Crash and hang recovery**, catching a server that runs but stops responding, with a cutoff to stop loops.
- Crash lines report how long the server was up, the exit code, and Palworld's own crash reason.

### Backups
- Zips the world save and config, timestamped, on startup, shutdown, a schedule, or on demand.
- Saves the world in-game first when the REST API is on, so the backup is actually current.
- Automatic backups age out after a set number of days. Manual ones are left alone.
- Keep them next to the launcher or in a folder of your choice.

### Keeping an eye on things
- Live tiles for FPS, players, uptime, memory, version, and the next restart and backup.
- Player joins and leaves appear in the log as they happen.
- **Port Check** tests whether your game port is reachable from outside, and warns if REST or RCON are exposed.

### Settings
- A tabbed **Server Settings** editor for `PalWorldSettings.ini`, in the game's own wording where it has one.
- **Search** filters the settings as you type, by name, label, or description.
- Writes only the keys you changed, and previews them before saving.
- **Difficulty presets**: Casual, Normal, Hard, or Hardcore in one click, previewed first.
- A **Launch Arguments** editor with a live preview of the exact command line.
- Set process priority and pin the server to CPU cores, re-applied because Unreal resets affinity on launch.

### Background and logs
- Runs the server hidden, with no console window, and it survives the launcher closing or crashing.
- Offers to pick a running server back up next launch, or does it automatically if you turn that on.
- **Start at login** (optional) opens the launcher and starts the server when you sign into Windows.
- Launcher, SteamCMD, and server logs appear in-app and in a rotating log file (last ten kept).
- `--debug` and `--console` add detail, see [Command-line options](docs/advanced-usage.md#command-line-options).

### Discord (optional)
- **Webhook** notifications for the server coming up, going down, updating, crashing, and players coming and going.
- A **control bot**: `/status`, `/players`, `/save`, `/backup`, `/update`, `/start`, `/restart`, `/stop`.
- The bot is locked to a channel and/or role, and restart and stop confirm first.
- Setup guide: [docs/discord-bot-setup.md](docs/discord-bot-setup.md).

### Languages
- Ten languages: English, Deutsch, Español, Français, Português (Brasil), Русский, 日本語, 简体中文, 繁體中文, 한국어.
- Pick one on first run or from Launcher Settings (the gear icon, top-right), then it restarts to apply.
- Everything but English is machine-translated, so corrections via an issue or pull request are welcome.

---

## Getting started

You'll need **Windows 10 or 11 (64-bit)**, plus room and bandwidth for the server install (a full install with
no mods sits a bit under 6 GB as of Palworld 1.0). The launcher itself is light (around 100 MB of RAM), but the
Palworld dedicated server it runs is RAM-hungry, so check Palworld's
[official requirements](https://docs.palworldgame.com/getting-started/requirements/) before hosting.

**Download** the latest `PalworldServerLauncher.exe` from the
[releases page](https://github.com/SSyl/PalworldServerLauncher/releases/latest). It's a single file with no
installer, so drop it wherever you'd like the server to live.

> [!NOTE]
> The first time you run it, Windows may show a blue "Windows protected your PC" box, because the app isn't
> code-signed. Click **More info**, then **Run anyway**. Some antivirus tools may flag it for the same reason
> (an unsigned, self-contained build). If that worries you, the full source is here and you can
> [build it yourself](#building-from-source).

1. Run `PalworldServerLauncher.exe`.
2. Click **Install** to grab SteamCMD and the server. You only need this the first time.
3. Click **Start**. The very first launch creates the server's config files.

> [!TIP]
> Windows Firewall may ask whether to allow the Palworld server through. Click **Allow access**, otherwise
> the server won't be reachable over the network and players won't be able to connect.

4. When offered, turn on the **REST API** (it can generate a secure admin password for you). It's what powers
   the stats, graceful restarts, backups, and health checks. Without it the server still runs, but the
   launcher has to force-stop it instead of shutting it down cleanly.
5. Optional: turn on **Scheduled restart** and pick your times, set up **Backups**, and connect **Discord**.

---

## Screenshots

|  |  |
| :-: | :-: |
| ![Game settings editor](docs/images/app-screenshots/game-settings.png) | ![Difficulty presets](docs/images/app-screenshots/difficulty-preset.png) |
| ![Launch arguments](docs/images/app-screenshots/launch-args.png) | ![CPU affinity and priority](docs/images/app-screenshots/cpu-affinity-priority.png) |
| ![Mods](docs/images/app-screenshots/mods-window.png) | ![Port accessibility check](docs/images/app-screenshots/port-check-window.png) |
| ![Live server commands](docs/images/app-screenshots/server-rest-commands.png) | ![Discord settings](docs/images/app-screenshots/discord-bot-settings.png) |
| ![Restart and backup times](docs/images/app-screenshots/schedules.png) | ![In-game announcements](docs/images/app-screenshots/announcements.png) |

**Available in ten languages.** English, German, Spanish, French, Brazilian Portuguese, Russian, Japanese,
Simplified Chinese (shown here), Traditional Chinese, and Korean.

![The main window in Simplified Chinese](docs/images/app-screenshots/main-window-simplified-chinese.png)

---

## FAQ and troubleshooting

Common hosting questions, how you and your friends connect, why a connection fails (firewall, port
forwarding, CGNAT), how to get listed in the community server browser, updating and pinning versions,
importing an existing server, and where your files live, are all answered in
**[docs/FAQ.md](docs/FAQ.md)**.

## Advanced usage

Running more than one server on the same machine, and the launcher's command-line options (`--console`,
`--start-server`, `--install-server`, `--stop-server`, and more), are covered in
**[docs/advanced-usage.md](docs/advanced-usage.md)**.

## Upcoming features

- A system-tray icon.
- A fuller headless / command-line mode. There's already `--install-server`, `--start-server`, and
  `--stop-server` (see [Command-line options](docs/advanced-usage.md#command-line-options)), and you can run
  the launcher hidden with a small PowerShell script if you want it out of the way.

## Building from source

Most people don't need this, just grab a pre-built `.exe` from the
[releases page](https://github.com/SSyl/PalworldServerLauncher/releases/latest). If you'd rather build it
yourself (you'll need the **.NET 10 SDK**), see **[docs/building.md](docs/building.md)**.

## Privacy and security

> [!WARNING]
> Palworld's REST API and RCON aren't built to face the internet. Keep those ports (8212 and 25575) on your
> local network or behind a firewall, and only forward the game ports your players actually need.

The launcher runs on your machine and does not collect, transmit, or phone home any of your data. There is no
telemetry and no analytics. It makes network connections only to:

- your own server, over `127.0.0.1` (your local machine),
- Steam, to download SteamCMD and to install or update the server,
- your own Discord webhook and bot, if you choose to set them up,
- the **Port Check** feature, only if you use it, and only after it warns you first: it sends your public IP
  and the ports you're testing to check-host.cc, a free external probe service, and uses a separate lookup
  service to show your Public IP field.

Your settings, logs, backups, and any tokens stay on your PC in the launcher's folder, and your Discord bot
token is never written to the logs. Lock the control bot down to a private channel and/or an admin-only role.

---

*Not affiliated with or endorsed by Pocketpair. "Palworld" is a trademark of its respective owner.*
