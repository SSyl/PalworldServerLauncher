# Changelog

Notable, user-facing changes to the Palworld Server Launcher. Headline features and fixes, not every commit.

## [0.6.0] - 2026-07-17

### Added
- **An RCON console.** With RCON enabled on your server, the Server Commands window gains an RCON tab: a simple
  terminal for sending raw RCON commands and reading the responses, with your recent commands remembered and a
  saved log. It is reachable even on a server that has only RCON on and not the REST API. Note that Palworld has
  deprecated RCON in favor of the REST API and plans to remove it in a future update, so prefer the REST-based
  commands where they cover what you need.
- **Server memory in the Discord `/status` command,** alongside the FPS, players, uptime, and version it
  already reports.
- **Choose where backups are saved.** A Backup Location button in the Backups section lets you pick a custom
  folder for your backup archives, or keep the default (a backups folder next to the launcher). It checks the
  folder is writable before saving, and your existing backups stay where they are.

### Changed
- **Server Commands opens whenever the server is running,** not only when the REST API is connected, so the RCON
  tab is reachable without REST. When the REST API isn't connected, its tab grays out with a short notice.
- **Backups now include only `PalWorldSettings.ini`** from the server config, alongside the world save, instead
  of the whole config folder. The other config files aren't part of a normal server's state.

## [0.5.0] - 2026-07-17

### Added
- **Six more languages: German, French, Spanish, Brazilian Portuguese, Korean, and Russian.** The launcher is
  now available in German (Deutsch), French (Français), Spanish (Español), Brazilian Portuguese (Português
  (Brasil)), Korean (한국어), and Russian (Русский), joining English, Simplified Chinese, Traditional Chinese,
  and Japanese. Pick one on first run or any time from Launcher Settings. The Game Settings names come from
  Palworld's own translations, the rest is machine-generated, so corrections via an issue or pull request are
  welcome.
- **A WorldOption.sav check on Start.** Worlds converted from co-op or single-player carry a WorldOption.sav
  that overrides your PalWorldSettings.ini on a dedicated server, which can leave the launcher unable to monitor
  or control the server. The launcher now spots it before starting and offers to rename it to .bak (with a link
  to the file) so your ini takes effect.
- **Warns about another running server before Start.** If a Palworld server this launcher didn't start is
  already running (a leftover process, or one it can't identify), hitting Start would launch a second server
  that competes for the same ports. The launcher now spots that before Start and offers to shut the other one
  down first. Can be turned off in Launcher Settings.

### Fixed
- **An imported server no longer gets stuck on "Starting..." indefinitely** when its REST API never answers
  (often a WorldOption.sav override, a wrong REST port, or a password mismatch). The status now reads "REST not
  responding" and points at what to check, instead of hanging, and it never force-restarts a server that is
  actually up.
- **Discord connection hiccups no longer flood the log.** A transient Discord API error (like a 500 during the
  bot's connect) used to log a full stack trace on every retry. Now it's a single concise line, and repeats are
  throttled, so a Discord outage doesn't bury the log.

## [0.4.0] - 2026-07-16

### Added
- **Automatic-updates switch and a version pin.** A single Automatic Updates toggle turns the launcher's
  automatic updating on or off (you can still check manually), and a new Pin Server Version option freezes the
  server on its current build and holds off every update until you unpin.
- **Import an existing server.** Already have a Palworld dedicated server installed somewhere else? The Import
  button copies it into the launcher, leaving your original in place until you've confirmed the managed copy
  works.
- **Start at Windows login.** An option in Launcher Settings drops a Startup shortcut so the launcher opens and
  starts your server when you sign in to Windows, keeping scheduled restarts, updates, backups, and recovery
  running. No admin rights needed.
- **Auto-reconnect on startup.** An opt-in setting to silently reconnect to a single already-running server
  when the launcher starts, instead of asking each time. Several running servers still prompt.
- **Command-line startup options.** `--install-server` installs SteamCMD and the server with no window, and
  `--start-server` opens the launcher and brings the server up on load, handy for scripts and scheduled tasks.
- **The game version shows next to the build number.** The Version stat and the pinned-build display now show
  the release version (like v1.0.1) alongside the build id, so you can tell which update a build is.
- **Open-source licenses and a copyright line** in Launcher Settings.

### Changed
- **Server Settings reorganized.** Game Settings now has its own sub-tabs (Admin, Gameplay, Game Balance,
  Performance, Undocumented), and the old Advanced dialog for process priority and CPU affinity is now a tab in
  the same window. The look of the app was tidied and unified throughout.
- **Launcher Settings is a gear icon** now, in the top-right, and it has gained the Hide SteamCMD Window and Log
  Server Status options that used to sit in the main window.
- **The Discord bot button** moved up next to Server Settings and Mods.
- **SteamCMD reinstalls itself if it goes missing** before an update or version check, so an imported or
  hand-placed server without it still updates.

### Fixed
- **Disabled update options gray out properly** when a version pin is on, instead of looking like you can still
  click them.
- **The schedule picker's time dropdowns** are no longer oversized.
- **The Server Settings search box** no longer draws the text cursor on top of the "Search" placeholder.

## [0.3.1] - 2026-07-14

### Added
- **Set a fixed Steam query port.** It is still auto-picked (the first free port from 27015) by default, but you
  can now set a specific one under Launch Arguments, handy if you forward it or run behind a strict firewall.
  The Port Check tests whatever you set.

## [0.3.0] - 2026-07-14

### Added
- **More languages.** The launcher now speaks Simplified Chinese (简体中文), Traditional Chinese (繁體中文), and
  Japanese (日本語) in addition to English. Pick your language when you first run it, or later under Launcher
  Settings, and it restarts itself to apply. All of the non-English translations are machine-generated, so
  corrections and suggestions on GitHub are very welcome.
- **Mods.** Manage Steam Workshop server mods from the launcher. Paste a mod's Workshop id or URL and it
  downloads the mod (and keeps it up to date on each start), then enable, disable, or remove mods from a list
  where each links to its Workshop page. A separate section manages loose `.pak` mods you drop in yourself,
  toggling them on and off by renaming rather than deleting, and there's a shortcut to the UE4SS mods folder
  for script mods. Downloading Workshop mods needs a one-time Steam sign-in, which Steam's own tool handles in
  its own window, the launcher never sees or stores your password.
- **Search your server settings.** The Server Settings window now has a search box that filters settings as you
  type. It matches a setting's name, its label, and its description (even its raw in-file name), in whatever
  language you are using, so searching "death" turns up the Hardcore character-recreation option through its
  description. Launch Arguments are left out of the search.
- **Chat and Players log tabs.** In-game chat and player joins and leaves each have their own tab now, separate
  from the general log.
- **Dark window title bars.** Every window's title bar now matches the app's dark theme instead of staying
  system-light.
- **A timed shutdown you can watch and skip.** When you shut down on a timer, the Stop button now shows the
  seconds ticking down and turns amber. Click it to shut down right away instead of waiting out the countdown.
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
- **Already-running server prompt.** When a managed server is already running as the launcher starts, it now
  asks whether to reconnect to it, shut it down, or exit, instead of adopting it automatically.
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
- **Passwords with a quote or backslash work now.** A server or admin password containing `"` or `\` could be
  misread, which broke the REST API connection. It is parsed correctly now.

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
