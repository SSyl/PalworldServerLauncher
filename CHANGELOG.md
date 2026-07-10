# Changelog

Notable, user-facing changes to the Palworld Server Launcher. Headline features and fixes, not every commit.

## [Unreleased]

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
