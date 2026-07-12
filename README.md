# Palworld Server Launcher

[![Latest release](https://img.shields.io/github/v/release/SSyl/PalworldServerLauncher?include_prereleases)](https://github.com/SSyl/PalworldServerLauncher/releases/latest) [![License: GPLv3](https://img.shields.io/github/license/SSyl/PalworldServerLauncher)](LICENSE)

A Windows app for running a **Palworld dedicated server**. It installs the server, keeps it up to date,
restarts and backs it up on whatever schedule you like, and watches its health, all through Palworld's REST
API. It's a native Windows program written in C# / WPF, and the launcher itself is a single `.exe`. Heavily 
inspired by [Conan Exile's Dediciated Server Launcher](https://forums.funcom.com/t/introducing-the-conan-exiles-dedicated-server-app/21699).

> [!NOTE]
> **Status:** pre-release and still a work in progress. Not affiliated with Pocketpair.

![The Palworld Server Launcher main window](docs/images/app-screenshots/main-window.png)

---

## What it does

### Install and update
- Installs SteamCMD and the dedicated server for you. When you hit **Start** it checks for a server update
  first and then launches (you can turn that off).
- **Auto-updates when a new build drops.** It watches for a new server build, and when one releases it
  restarts the server gracefully to apply it. It doesn't care which game version you're on, so it keeps
  working across 0.x, 1.0, and whatever comes next.
- A read-only **Check for Update** button that's safe to use while the server is running, plus a **Validate
  Files** button that has SteamCMD re-verify the whole install.

### Restarts and recovery
- **Scheduled restarts** at the times of day you choose. The restart lands right on the scheduled time,
  whether or not anyone is online. A server that only just came up won't get bounced (there's a
  minimum-uptime guard).
- **Players get a heads-up.** Before a scheduled or update restart, the server warns players in-game at the
  marks you set, say 15, 5, and 1 minutes out. Those warnings are just for players, so they're skipped when
  nobody's online.
- **Crash recovery.** If the server crashes, the launcher brings it right back. It also catches a server
  that's technically still running but wedged (the REST API has stopped answering, or the world has stopped
  advancing) and recovers that too. And if the server just keeps dying, a safety cutoff steps in so the
  launcher doesn't restart it forever.
- **Stop** and **Restart** happen the moment you click them.

### Backups
- Zips up your world save and server config with a timestamp on the filename. It can do this on startup, on
  shutdown, on a schedule, or any time you click **Backup Now**.
- When the server is running with the REST API on, it triggers a fresh in-game save first so the backup is
  actually current. Old automatic backups get tidied up after a set number of days, but anything you made by
  hand (or dropped into the folder yourself) is left alone.

### Keeping an eye on things
- Live tiles show FPS, players online, uptime, memory, version, and when the next restart and backup are
  due, all read from the REST API.
- Players joining and leaving show up in the log as they happen.

### Settings
- A full editor for Palworld's `PalWorldSettings.ini`, opened as one **Server Settings** window with tabs:
  **World Settings** (gameplay and balance), **Admin** (server management), and **Undocumented**. Every
  setting is there, labeled with the game's own in-game wording where it has one, and each tooltip shows the
  real in-file name plus a short note. The launcher only lets you change settings while the server is stopped,
  only writes the ones you actually touched, and shows you exactly what will change before it saves.
- **Difficulty presets.** One click applies a Casual, Normal, Hard, or Hardcore set of values (as of Palworld
  1.0). It previews what will change first, and switching presets always lands on a clean configuration.
- **Passwords** are hidden behind a show/hide button, so the admin password the launcher generates for you is
  still there to read when you need it.
- The **Undocumented** tab holds the settings the official docs don't cover (with the launcher's best guess in
  each tooltip), plus anything a future game update adds that the launcher doesn't recognize yet.
- A **Launch Arguments** editor with a live preview of the exact command line the server will start with.
- An **Advanced** section for low-level process tuning. Set the server's Windows **priority** and pin it to
  specific **CPU cores** (Unreal resets the affinity on launch, so the launcher quietly re-applies yours).
  Handy if you like to squeeze out performance.
- The launcher never slips in a third-party `Engine.ini`. Your server runs on its own defaults.

### Staying out of the way, and keeping records
- The launcher runs the server quietly in the background, with no extra console window cluttering your
  screen. If you close the launcher, or it crashes, the server just keeps running, and the launcher picks it
  back up the next time you open it. If a server is already running when you start the launcher, or still
  running when you go to close it, the launcher asks what you'd like to do rather than guessing.
- Everything that happens, from the launcher, from SteamCMD, and from the server's own output, shows up in
  log tabs inside the app and is saved to a timestamped log file, so there's always a record when something
  goes sideways. It keeps the last ten. If you'd rather run it from a command line, there are options for
  more detailed logs or for watching them live in a terminal (see [Command-line options](#command-line-options)).

### Discord (optional)
- Point it at a channel **webhook** to get a message whenever the server comes up, goes down, updates, or
  crashes, and when players come and go.
- There's also a **control bot.** From a locked-down channel, and/or a specific role, you and your admins can
  run `/status`, `/players`, `/save`, `/backup`, `/update`, `/start`, `/restart`, and `/stop` straight from
  Discord. Restart and stop ask you to confirm first. There's a step-by-step guide in
  [docs/discord-bot-setup.md](docs/discord-bot-setup.md).

---

## Screenshots

**The settings editor.** One tabbed window for every `PalWorldSettings.ini` value, labeled with the game's own wording.

![Game settings editor](docs/images/app-screenshots/game-settings.png)

**Difficulty presets.** Apply a Casual / Normal / Hard / Hardcore set of values, with a preview of exactly what changes.

![Difficulty presets](docs/images/app-screenshots/difficulty-preset.png)

**Launch arguments**, with a live preview of the exact command line.

![Launch arguments](docs/images/app-screenshots/launcher-args.png)

**Picking restart and backup times.**

![Restart and backup time picker](docs/images/app-screenshots/schedules.png)

**Customizable in-game restart announcements.**

![Announcements editor](docs/images/app-screenshots/announcements.png)

---

## Requirements

- **Windows 10 or 11 (64-bit).**
- To build it yourself: the **.NET 10 SDK**.
- Room and bandwidth for the server install. The first SteamCMD download is a few GB.

> [!TIP]
> The released `.exe` is self-contained, so you don't need to install .NET or any other runtime to run it.
> The .NET 10 SDK above is only for building from source.

## Quick start

> [!NOTE]
> The first time you run it, Windows may show a blue "Windows protected your PC" box, because the app isn't
> code-signed. Click **More info**, then **Run anyway**. That's normal for small unsigned apps.

1. Run `PalworldServerLauncher.exe`.
2. Click **Install** to grab SteamCMD and the server. You only need this the first time.
3. Click **Start**. The very first launch creates the server's config files.
4. When the launcher offers, turn on the **REST API**. It can set a secure random admin password for you.
   The REST API is what makes the stats, graceful restarts, backups, and health checks work. Without it the
   server still runs, but the launcher can do a lot less, and it has to hard-stop the server instead of
   shutting it down cleanly.
5. Optional: turn on **Scheduled restart** and pick your times, set up **Backups**, and connect **Discord**.

## Where things live

- The launcher's own settings sit in `launcher.json`, inside a `PalworldServerLauncher` folder next to the
  exe. That folder also holds the server install, your backups, and the logs. You edit these settings right
  in the app.
- The game's settings live in Palworld's `PalWorldSettings.ini`. Edit them from the launcher (Server Settings
  and Launch Arguments) or by hand in the file.

## Running more than one server

Each copy of the exe runs one server, installed in the `PalworldServerLauncher` folder next to it. Want to
run several on the same machine? Drop a copy of the exe into its own folder for each server. They stay
completely separate, with their own settings, logs, install, and backups, and neither one ever touches the
other's server. Just give each server its own ports:

- **Listen port** (`-port`, default 8211), set under **Launch Arguments**.
- **REST API port** (default 8212) and **RCON port** (default 25575, if you turn it on), set in that
  server's `PalWorldSettings.ini`.

The Steam query port sorts itself out automatically by picking the first free one.

## Security

> [!WARNING]
> Palworld's REST API and RCON aren't built to face the internet. Keep those ports (8212 and 25575) on your
> local network or behind a firewall, and only forward the game ports your players actually need.

The launcher only ever talks to the REST API on `127.0.0.1`, your own machine. Your Discord bot token is
stored locally in `launcher.json` and is never written to the logs. Lock the control bot down to a private
channel and/or an admin-only role.

## Privacy

The launcher runs on your machine and does not collect, transmit, or phone home any of your data. There is no
telemetry and no analytics. It makes network connections only to:

- your own server, over `127.0.0.1` (your local machine),
- Steam, to download SteamCMD and to install or update the server,
- your own Discord webhook and bot, if you choose to set them up,
- when you use the **Port Check** feature, and only then, and only after it warns you first: check-host.cc, a
  free external probe service, which is told your public IP and the ports you choose to test so it can check
  them from the internet, and a public-IP lookup service (ipify) used to show your External IP.

Your settings, logs, backups, and any tokens stay on your PC in the launcher's folder. The only features that
reach an outside service are ones you choose to run (like the port check above), and they are listed here.

## Command-line options

You can double-click the launcher, or start it from a terminal with a couple of extra options:

- `--debug` (or `--verbose`): write more detailed logs.
- `--console`: mirror the launcher's logs into the terminal you started it from, handy for keeping an eye on
  a server from the command line.

```powershell
PalworldServerLauncher.exe --console --debug
```

## Building

From the repository root:

```powershell
dotnet build
dotnet test
dotnet run --project src\PalServerLauncher              # run it
dotnet publish src\PalServerLauncher -c Release         # build a single self-contained .exe
```

Pass launcher options after `--`, for example `dotnet run --project src\PalServerLauncher -- --console`.

## Still to come

- Mod support (Steam Workshop and maybe Nexusmods)
- A headless mode you can drive entirely from the command line (start, stop, status, no window).
- A system-tray icon, a status bar, and a copy-the-connection-info button.
- Port/Online Connectivity Checker

## Not affiliated

Not affiliated with or endorsed by Pocketpair. "Palworld" is a trademark of its respective owner.
