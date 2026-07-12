# Changelog

Notable, user-facing changes to the Palworld Server Launcher. Headline features and fixes, not every commit.

## [Unreleased]

### Added
- **Live server commands.** A Server Commands panel lets you announce a message, kick, ban, or unban a player,
  and save the world, all while the server is running, right from the launcher.
- **Force Shutdown.** A button that immediately stops a server that's wedged or stuck shutting down. It stays
  hidden until a start, stop, or restart has been dragging for a while, so it's there when you need it and out
  of the way the rest of the time.
- **Discord control commands.** Your control bot can now announce, kick, ban, and unban from Discord, with a
  checklist of exactly which commands it's allowed to run. The ones that stop the server or remove players stay
  off until you turn them on.
- **Port Check.** See whether your server's ports are actually reachable from the internet.
- **CPU usage** now sits alongside the other live stats (FPS, players, memory, and so on).
- **Compact view.** Click the little arrow above the Restarts / Backups / Misc sections to fold them away for a
  smaller, log-focused window. Your choice is remembered next time.

### Changed
- **The Stop button now asks how you want to shut down:** right away, or on a timer that shows players an
  in-game countdown first. If the REST API is off, it explains that it can only force-stop.
- **Live stats moved to a status bar** along the bottom of the window, under the log.
- **Launch Arguments** are now a tab inside the Server Settings window instead of a separate button.
- **Server commands show up in the Server Log.** Announcing, kicking, banning, unbanning, and saving each
  leave a line so you can see what happened.
- Some layout tidying: the settings buttons were rearranged, and Status and Update now share a row with your
  public IP.

### Fixed
- **A timed shutdown now keeps its timer even when nobody's online.** It used to shut down instantly on an
  empty server.
- **The Server Log is much quieter.** The launcher's own health checks no longer flood it with "REST accessed
  endpoint" lines every few seconds.
- **A server you stop now stays stopped.** Fixed some timing cases where an automatic restart or recovery could
  bring it right back after you deliberately shut it down.
- The **"Working..." button** no longer changes width as its animated dots come and go.

## [0.2.0] - 2026-07-10

### Added
- **Palworld 1.0 support.** The settings editor now covers every 1.0 setting, including the new voice-chat,
  dropped-item-physics, and guild-ownership-transfer options.
- **Difficulty presets.** One click applies a Casual, Normal, Hard, or Hardcore set of values in the World
  Settings tab. It previews exactly what will change first, and switching presets always lands on a clean
  configuration.
- **Save confirmation.** Before writing `PalWorldSettings.ini`, the editor shows exactly which settings will
  change, and records each change in the log.
- **Undocumented Settings tab.** Settings the official docs don't cover are grouped here with the launcher's
  best guess, so the main tabs stay trustworthy. Anything a future game update adds shows up here too.

### Changed
- **One tabbed Server Settings window.** The separate Game, Admin, and New Settings buttons are now a single
  Server Settings dialog with World Settings, Admin, and Undocumented tabs.
- **Settings use the game's own wording.** Labels match what you see in-game under Edit World Settings, and
  the real in-file name is in each tooltip.
- **Start / Stop / Restart are icons now** (a play triangle, a square, and a circular arrow), so the action
  buttons take less space.
- The **Difficulty** setting now warns that it has no effect on a dedicated server (it is a client /
  single-player setting).

### Fixed
- The **REST API enabled** setting no longer shows a misleading "reset to default" next to it while it is on,
  and a bulk "Reset to defaults" no longer turns it off.

## [0.1.0]

First public pre-release.

- Installs and auto-updates the dedicated server via SteamCMD.
- Scheduled restarts with in-game warnings, plus crash and zombie recovery.
- Scheduled and on-demand world backups.
- Live health and player monitoring over Palworld's REST API.
- A settings editor for `PalWorldSettings.ini` and the launch arguments.
- Optional Discord webhook notifications and a slash-command control bot.
