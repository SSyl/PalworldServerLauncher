# Changelog

Notable, user-facing changes to the Palworld Server Launcher. Headline features and fixes, not every commit.

## [1.5.1] - 2026-08-20

### Fixed
- **Backups no longer hold the world save against the server.** The archive kept each file open in a way that blocked Palworld from replacing it, so a save landing during a backup could fail and take the server down with it. (#18)
- **A file the launcher can't read is left out of the backup instead of archived as zero bytes**, and the log names it rather than staying silent about it.
- **A backup that fails partway no longer leaves a zip behind.** The half-written archive opened cleanly and sat in the folder looking like a real backup, so a restore could pick it.
- **The repeating message keeps its interval across a clock change.** It was paced on the wall clock, so daylight saving or a time correction stalled it for the length of the jump.
- **A backup that can't read a file partway through is discarded** instead of keeping a half-copied file the log reported as left out.
- **A config file that isn't there is named in the log**, since restoring without GameUserSettings.ini boots an empty world.

## [1.5.0] - 2026-08-19

### Added
- **A repeating in-game message.** Edit Announcements now has a message you can send to everyone online every so often, from once a minute to once a day, for a Discord link or a server rule. It is skipped while nobody is on the server and while a restart countdown is running.
- **The setting Palworld added in 1.0.3**, whether enemy camps may spawn near your bases, is now in Server Settings under Gameplay. (#17)

### Fixed
- **Settings a game update adds now show up on their own.** The Undocumented tab reads the game's own default config as well as yours, so a key added after your PalWorldSettings.ini was written is listed at its default value and is saved into your config the moment you change it. Before, only keys already in your file were listed. (#17)

## [1.4.0] - 2026-08-13

### Added
- **A Launcher tab**, so the launcher's own messages can be read without the server's output mixed in. The All tab still shows everything.
- **Select and copy from any log tab**, by mouse, Ctrl+A and Ctrl+C, or the right-click menu.
- **An optional date on log timestamps**, off by default, for a server that has been up for days.
- **A failed SteamCMD run now says what SteamCMD said**, showing the reason from its own content log instead of one line with an exit code. (#15)
- **A Repair option after a failed update.** It sets Steam's app manifest aside and revalidates, which recovers an install SteamCMD otherwise refuses to update. Nothing is downloaded unless a file is missing or damaged, and it never runs without being asked.
- **Check for Update now says when the installed build can't be read**, and offers to rebuild it by validating the files, rather than reporting the server as up to date.
- **A filter box in the footer.** Type words to show only the lines containing all of them, matched against the text and the source, so `error` or `chat` narrows by where a line came from. Ctrl+F focuses it, Escape clears it, and it applies to every tab at once. (#14)

### Changed
- **The Server Log tab is now Server, and General is now All.**
- **The log controls moved into the footer**, beside the server stats, so nothing sits over the tab strip. Server Commands moved into the Misc box.
- **The Version stat shows just the version**, with the full build id on hover. The update line above it already spells out both.
- **Shorter labels and buttons in several languages**, and Next restart is now just Restart. French, Spanish and Portuguese were running past the space they had, and some buttons were cut off or pushed off the row entirely.
- **Log lines are colored by where they came from**, and player and chat lines carry the time Palworld recorded rather than the moment the launcher read them.
- **Log files are named `launcher-2026-08-13_00.52.29.log`** and record milliseconds. The old name sorted newer files above older ones.
- **A failed update is logged as an error, not information**, with SteamCMD's own reason lines beneath it.
- **A failure that never reached Steam no longer claims the update failed**, since SteamCMD can stop before it looks at the server at all.
- **`-log` is no longer passed to the server.** Measured against a live server with a real player, chat and join lines arrive without it.

### Fixed
- **The update prompt no longer returns every check after an update fails.** The launcher remembers the build it failed to reach and stops offering it until Steam publishes a newer one or you ask again. (#15)
- **An update that reports success without installing anything is now treated as a failure**, by comparing the installed build against the one being applied.
- **A stale build id from Steam no longer triggers a pointless update restart.** SteamCMD sometimes reports an older build than the one installed, and any difference used to count as an update.
- **A failed repair no longer leaves the install without a build id.** Steam's app manifest is put back unless the run left a usable one.
- **The Server and Chat tabs explain themselves after reconnecting** to a server the launcher didn't start, instead of sitting empty with no reason given.
- **A log file that can't be written no longer silences `--console` as well.** The file and the console are written independently, so one failing doesn't take the other with it.
- **The admin password is replaced with `***` in the log.** Logging in as an admin in-game types the password into chat and the server echoes it back, so it reached the log file people attach to bug reports. It is masked in the log file, the log tabs, and `--console`. Passwords under 8 characters are left alone, since a short one occurs in ordinary log text and cutting it would take real content with it.
- **Server output that stamps its own clock no longer shows two.** PalDefender and other console mods prefix every line with their own time, which the launcher printed alongside its own. (#14)

## [1.3.1] - 2026-08-11

### Fixed
- **"Open the launcher and start the server at Windows login" could get stuck off for good.** Windows keeps its own on/off switch for Startup entries, and switching it off in Task Manager left the option looking enabled while nothing happened at login. Unticking and reticking it never cleared that switch. Ticking the option now clears it, and the option reads as off for as long as Windows is blocking it. (#13)

### Changed
- **The log now records how the launcher was started and whether its login shortcut is in place**, so an autostart that doesn't fire can be worked out from the log file.

## [1.3.0] - 2026-08-10

### Added
- **Clear Logs button**, with an option to clear automatically when you start or restart the server yourself. Scheduled restarts, update restarts, and crash recovery never clear, so a server failing to start keeps the messages explaining why. The log file on disk is never touched. (#12)
- **Open UE4SS Mods Folder now finds a hand-installed UE4SS**, not just the Steam Workshop one, and says where to put it when neither is there. If both are installed it warns first, because two copies of UE4SS crash the server on launch. (#12)
- **The Mods panel warns when Workshop mods can't reach a hand-installed UE4SS.** The server installs them into its own UE4SS folder, which a hand install doesn't read, so they used to sit there doing nothing.

### Changed
- **Traditional Chinese now reads as Taiwan Chinese**, not Simplified Chinese in Traditional characters. Around fifty terms replaced, plus four conversion errors that had produced unrelated words.
- **Both Chinese translations now use the polite form** and Palworld's own wording for restart.
- **Small German corrections**, including the Start button, which now uses the verb form like every other button.

## [1.2.0] - 2026-08-06

### Added
- **A crash now says why.** Palworld writes no log file and its fatal errors never reach the console, so a crash used to leave the log stopping mid-sentence with nothing to go on. The launcher now reads the crash report Palworld itself writes and shows the reason, along with the folder holding the crash dump. A corrupt world, for one, now says so in as many words instead of looking like an unexplained crash. Reported in #11.
- **Crash lines say how long the server had been up and whether it ever finished starting.** A server that dies four seconds into startup is a different problem from one that dies after six hours, and the log used to describe both identically. The exit code is reported too.

### Changed
- **Log tabs now wrap long lines** instead of running off the right edge, so crash reasons and file paths can be read without scrolling sideways.
- **The unresponsive-server check is grayed out when the REST API is off**, with a tooltip explaining why, because the launcher cannot detect a frozen server without it. The setting keeps your values and comes back as you left it once REST is on again.
- **Plainer wording throughout the log and the interface.** Internal terms like "zombie", "backoff", and "circuit breaker" have been replaced with what they actually mean, and the startup line now names the launcher version.

### Fixed
- **Passwords are masked in the log.** Server and Admin passwords no longer appear in the log in plaintext.
- **Clicking Start after repeated crashes gives the server a real chance again.** The safety cutoff that stops an endless crash loop kept counting the old crashes, so the first crash after starting manually would immediately suspend restarts again, even though the launcher had just told you to fix the problem and press Start.
- **Startup backups no longer warn that they might be missing recent changes.** The server is stopped at that point, so the save on disk is the whole world and there is nothing to miss. The warning still appears where it belongs, when the server is running but REST is off.

## [1.1.0] - 2026-08-03

### Added
- **Stop the server from the command line with `--stop-server`.** It saves, runs the shutdown backup, and shuts the server down, then exits. Add a number of seconds (`--stop-server 60`) to warn players with an in-game countdown first, or use `--kill-server` to end a wedged server immediately. See [Advanced usage](docs/advanced-usage.md) for the details.
- **Opening a second launcher from the same folder now warns instead of quietly running two.** Two launchers on one folder share a server, a config, and a data folder, and fight over restarts, backups, and updates. The second one offers to force close the first, or to exit. Running a launcher per folder, one install each, is unaffected and still the intended way to run several servers.
- **`--stop-server` works even while a launcher window is open.** It hands the request to the running launcher, so the shutdown behaves exactly like clicking Stop (shutdown backup, Discord notification) and the launcher knows the server was stopped on purpose instead of treating it as a crash and starting it again.
- **Import now accepts a Linux server install and brings the world across.** Picking a folder holding the Linux dedicated server (`PalServer.sh`) used to be rejected as "not a server folder". The launcher now recognizes it, downloads the Windows dedicated server, and copies the world and settings over, moving them from Palworld's `LinuxServer` config folder to the `WindowsServer` one the Windows build reads. The original is left untouched.
- **Start now catches a world save the server isn't set to load, instead of letting it quietly begin an empty one.** Palworld picks its world by name from `GameUserSettings.ini` and never goes looking for one, so a restored backup, an imported server, or a save folder copied in by hand would start a brand new world and leave the real save on disk but unreachable in game. When exactly one save is there, Start offers to point the server at it. Beginning a new world on purpose is still one click away.

### Changed
- **New installs go in a folder named `PalServer` instead of `palworlddedicatedserver`.** That's Palworld's own name for the dedicated server, and it's 14 characters shorter, which matters because Palworld's autosave fails outright once the path to a save gets long enough. Servers installed before this update keep their existing folder and stay exactly where they are, whatever its capitalization.

### Fixed
- **Backups now include `GameUserSettings.ini`, so a backup can actually be restored into a fresh install.** That file is what tells the server which world folder to load. Restoring a backup without it left the world sitting on disk while the server created a new empty one beside it.
- **Chat shows up in the Chat tab on servers running PalDefender.** PalDefender replaces the game's own chat line with its own format, which the launcher did not recognize, so the Chat tab stayed empty and every message went to the Server Log instead. Reported in #8.

## [1.0.2] - 2026-07-27

### Fixed
- **Server Settings no longer opens in the wrong state while the server is starting or stopping.** Opening it during startup left the game settings editable, so changes made there were silently overwritten once the server came up, and opening it during shutdown left them locked after the server had stopped. The button is now unavailable until the server settles, and a window left open follows the server if it starts or stops.

## [1.0.1] - 2026-07-22

### Fixed
- **Server Settings no longer caps values the base game itself limits.** Pals per base, bases per guild, and Pal sync distance used to reject anything above the vanilla maximum, so a mod that raises those limits couldn't be configured. They now accept any value, and each tooltip states the game's own range.
- **Settings that use -1 for "no limit" can be entered again.** The number fields were blocking the minus sign, so the documented -1 (for example on the dropped active-items cap) couldn't be typed.

## [1.0.0] - 2026-07-22

### Changed
- **Visual refresh.** The app now shares a unified square dark look. Dropdowns, scroll bars, and checkboxes are dark-themed now instead of the leftover default light Windows ones.
- **The Server Settings editor is easier to scan.** Rows alternate shading and the row under your cursor highlights, so a setting lines up with its control across the width of the window.

### Added
- **Jump to latest in the log.** You can now scroll up in the log tabs to read earlier lines without being pulled back to the newest one, and a button appears to jump straight back to live.

### Fixed
- **The RCON console no longer opens with its text floating in the middle of the window.**

## [0.7.1] - 2026-07-21

### Changed
- **Signing in to Steam for Workshop mods is much clearer.** Connecting your Steam account now asks for your password in the launcher's own box, instead of the bare SteamCMD window that hid your typing and made it look like nothing was happening (a common source of confusion). Your password goes straight to Steam to sign in and is never saved or logged. Steam Guard is still confirmed in Steam's own window: approve the login on your phone, or type the code there. If a sign-in does not go through, the message now points you to the SteamCMD log in the main window for the exact error, instead of a window that closes on its own.

## [0.7.0] - 2026-07-19

### Added
- **Force a mod to run on your server.** Some Workshop mods work on a dedicated server but their author never marked them as server-compatible, so the server quietly skips them. The Mods window has a new Force column: tick it (past a warning) and the launcher makes the mod deploy server-side anyway, and re-applies that automatically each time the mod updates. Unticking it disables the mod, so the server stops loading it on the next start. Use it with care, since forcing a mod that truly isn't server-ready can crash the server or corrupt saves.

### Changed
- **Mods are no longer re-copied on every start.** The launcher used to copy every enabled Workshop mod into your server again on each start. It now copies a mod only when its content actually changed, so starts are quicker and lighter. A removed file in a mod update no longer lingers either, since the copy now mirrors the source exactly.

## [0.6.1] - 2026-07-17

### Fixed
- **The RCON console no longer garbles its output if you send a second command before the first one finishes.** Commands are sent one at a time now (the box briefly greys out while a command runs).
- **The memory stat shows in your regional number format again.** 0.6.0 briefly rendered it with a dot decimal regardless of locale.

## [0.6.0] - 2026-07-17

### Added
- **An RCON console.** With RCON enabled on your server, the Server Commands window gains an RCON tab: a simple terminal for sending raw RCON commands and reading the responses, with your recent commands remembered and a saved log. It is reachable even on a server that has only RCON on and not the REST API. Note that Palworld has deprecated RCON in favor of the REST API and plans to remove it in a future update, so prefer the REST-based commands where they cover what you need.
- **Server memory in the Discord `/status` command,** alongside the FPS, players, uptime, and version it already reports. Requested in #5.
- **Choose where backups are saved.** A Backup Location button in the Backups section lets you pick a custom folder for your backup archives, or keep the default (a backups folder next to the launcher). It checks the folder is writable before saving, and your existing backups stay where they are. Inspired by a PR from @Notplying (#6).

### Changed
- **Server Commands opens whenever the server is running,** not only when the REST API is connected, so the RCON tab is reachable without REST. When the REST API isn't connected, its tab grays out with a short notice.
- **Backups now include only `PalWorldSettings.ini`** from the server config, alongside the world save, instead of the whole config folder. The other config files aren't part of a normal server's state.

## [0.5.0] - 2026-07-17

### Added
- **Six more languages: German, French, Spanish, Brazilian Portuguese, Korean, and Russian.** The launcher is now available in German (Deutsch), French (Français), Spanish (Español), Brazilian Portuguese (Português (Brasil)), Korean (한국어), and Russian (Русский), joining English, Simplified Chinese, Traditional Chinese, and Japanese. Pick one on first run or any time from Launcher Settings. The Game Settings names come from Palworld's own translations, the rest is machine-generated, so corrections via an issue or pull request are welcome.
- **A WorldOption.sav check on Start.** Worlds converted from co-op or single-player carry a WorldOption.sav that overrides your PalWorldSettings.ini on a dedicated server, which can leave the launcher unable to monitor or control the server. The launcher now spots it before starting and offers to rename it to .bak (with a link to the file) so your ini takes effect.
- **Warns about another running server before Start.** If a Palworld server this launcher didn't start is already running (a leftover process, or one it can't identify), hitting Start would launch a second server that competes for the same ports. The launcher now spots that before Start and offers to shut the other one down first. Can be turned off in Launcher Settings.

### Fixed
- **An imported server no longer gets stuck on "Starting..." indefinitely** when its REST API never answers (often a WorldOption.sav override, a wrong REST port, or a password mismatch). The status now reads "REST not responding" and points at what to check, instead of hanging, and it never force-restarts a server that is actually up.
- **Discord connection hiccups no longer flood the log.** A transient Discord API error (like a 500 during the bot's connect) used to log a full stack trace on every retry. Now it's a single concise line, and repeats are throttled, so a Discord outage doesn't bury the log.

## [0.4.0] - 2026-07-16

### Added
- **Automatic-updates switch and a version pin.** A single Automatic Updates toggle turns the launcher's automatic updating on or off (you can still check manually), and a new Pin Server Version option freezes the server on its current build and holds off every update until you unpin.
- **Import an existing server.** Already have a Palworld dedicated server installed somewhere else? The Import button copies it into the launcher, leaving your original in place until you've confirmed the managed copy works.
- **Start at Windows login.** An option in Launcher Settings drops a Startup shortcut so the launcher opens and starts your server when you sign in to Windows, keeping scheduled restarts, updates, backups, and recovery running. No admin rights needed.
- **Auto-reconnect on startup.** An opt-in setting to silently reconnect to a single already-running server when the launcher starts, instead of asking each time. Several running servers still prompt.
- **Command-line startup options.** `--install-server` installs SteamCMD and the server with no window, and `--start-server` opens the launcher and brings the server up on load, handy for scripts and scheduled tasks.
- **The game version shows next to the build number.** The Version stat and the pinned-build display now show the release version (like v1.0.1) alongside the build id, so you can tell which update a build is.
- **Open-source licenses and a copyright line** in Launcher Settings.

### Changed
- **Server Settings reorganized.** Game Settings now has its own sub-tabs (Admin, Gameplay, Game Balance, Performance, Undocumented), and the old Advanced dialog for process priority and CPU affinity is now a tab in the same window. The look of the app was tidied and unified throughout.
- **Launcher Settings is a gear icon** now, in the top-right, and it has gained the Hide SteamCMD Window and Log Server Status options that used to sit in the main window.
- **The Discord bot button** moved up next to Server Settings and Mods.
- **SteamCMD reinstalls itself if it goes missing** before an update or version check, so an imported or hand-placed server without it still updates.

### Fixed
- **Disabled update options gray out properly** when a version pin is on, instead of looking like you can still click them.
- **The schedule picker's time dropdowns** are no longer oversized.
- **The Server Settings search box** no longer draws the text cursor on top of the "Search" placeholder.

## [0.3.1] - 2026-07-14

### Added
- **Set a fixed Steam query port.** It is still auto-picked (the first free port from 27015) by default, but you can now set a specific one under Launch Arguments, handy if you forward it or run behind a strict firewall. The Port Check tests whatever you set.

## [0.3.0] - 2026-07-14

### Added
- **More languages.** The launcher now speaks Simplified Chinese (简体中文), Traditional Chinese (繁體中文), and Japanese (日本語) in addition to English. Pick your language when you first run it, or later under Launcher Settings, and it restarts itself to apply. All of the non-English translations are machine-generated, so corrections and suggestions on GitHub are very welcome.
- **Mods.** Manage Steam Workshop server mods from the launcher. Paste a mod's Workshop id or URL and it downloads the mod (and keeps it up to date on each start), then enable, disable, or remove mods from a list where each links to its Workshop page. A separate section manages loose `.pak` mods you drop in yourself, toggling them on and off by renaming rather than deleting, and there's a shortcut to the UE4SS mods folder for script mods. Downloading Workshop mods needs a one-time Steam sign-in, which Steam's own tool handles in its own window, the launcher never sees or stores your password.
- **Search your server settings.** The Server Settings window now has a search box that filters settings as you type. It matches a setting's name, its label, and its description (even its raw in-file name), in whatever language you are using, so searching "death" turns up the Hardcore character-recreation option through its description. Launch Arguments are left out of the search.
- **Chat and Players log tabs.** In-game chat and player joins and leaves each have their own tab now, separate from the general log.
- **Dark window title bars.** Every window's title bar now matches the app's dark theme instead of staying system-light.
- **A timed shutdown you can watch and skip.** When you shut down on a timer, the Stop button now shows the seconds ticking down and turns amber. Click it to shut down right away instead of waiting out the countdown.
- **Live server commands.** A Server Commands panel lets you announce a message, kick, ban, or unban a player, and save the world, all while the server is running, right from the launcher.
- **Force Shutdown.** A button that immediately stops a server that's wedged or stuck shutting down. It stays hidden until a start, stop, or restart has been dragging for a while, so it's there when you need it and out of the way the rest of the time.
- **Discord control commands.** Your control bot can now announce, kick, ban, and unban from Discord, with a checklist of exactly which commands it's allowed to run. The ones that stop the server or remove players stay off until you turn them on.
- **Port Check.** See whether your server's ports are actually reachable from the internet.
- **CPU usage** now sits alongside the other live stats (FPS, players, memory, and so on).
- **Compact view.** Click the little arrow above the Restarts / Backups / Misc sections to fold them away for a smaller, log-focused window. Your choice is remembered next time.

### Changed
- **Already-running server prompt.** When a managed server is already running as the launcher starts, it now asks whether to reconnect to it, shut it down, or exit, instead of adopting it automatically.
- **The Stop button now asks how you want to shut down:** right away, or on a timer that shows players an in-game countdown first. If the REST API is off, it explains that it can only force-stop.
- **Live stats moved to a status bar** along the bottom of the window, under the log.
- **Launch Arguments** are now a tab inside the Server Settings window instead of a separate button.
- **Server commands show up in the Server Log.** Announcing, kicking, banning, unbanning, and saving each leave a line so you can see what happened.
- Some layout tidying: the settings buttons were rearranged, and Status and Update now share a row with your public IP.

### Fixed
- **A timed shutdown now keeps its timer even when nobody's online.** It used to shut down instantly on an empty server.
- **The Server Log is much quieter.** The launcher's own health checks no longer flood it with "REST accessed endpoint" lines every few seconds.
- **A server you stop now stays stopped.** Fixed some timing cases where an automatic restart or recovery could bring it right back after you deliberately shut it down.
- The **"Working..." button** no longer changes width as its animated dots come and go.
- **Passwords with a quote or backslash work now.** A server or admin password containing `"` or `\` could be misread, which broke the REST API connection. It is parsed correctly now.

## [0.2.0] - 2026-07-10

### Added
- **Palworld 1.0 support.** The settings editor now covers every 1.0 setting, including the new voice-chat, dropped-item-physics, and guild-ownership-transfer options.
- **Difficulty presets.** One click applies a Casual, Normal, Hard, or Hardcore set of values in the World Settings tab. It previews exactly what will change first, and switching presets always lands on a clean configuration.
- **Save confirmation.** Before writing `PalWorldSettings.ini`, the editor shows exactly which settings will change, and records each change in the log.
- **Undocumented Settings tab.** Settings the official docs don't cover are grouped here with the launcher's best guess, so the main tabs stay trustworthy. Anything a future game update adds shows up here too.

### Changed
- **One tabbed Server Settings window.** The separate Game, Admin, and New Settings buttons are now a single Server Settings dialog with World Settings, Admin, and Undocumented tabs.
- **Settings use the game's own wording.** Labels match what you see in-game under Edit World Settings, and the real in-file name is in each tooltip.
- **Start / Stop / Restart are icons now** (a play triangle, a square, and a circular arrow), so the action buttons take less space.
- The **Difficulty** setting now warns that it has no effect on a dedicated server (it is a client / single-player setting).

### Fixed
- The **REST API enabled** setting no longer shows a misleading "reset to default" next to it while it is on, and a bulk "Reset to defaults" no longer turns it off.

## [0.1.0]

First public pre-release.

- Installs and auto-updates the dedicated server via SteamCMD.
- Scheduled restarts with in-game warnings, plus crash and zombie recovery.
- Scheduled and on-demand world backups.
- Live health and player monitoring over Palworld's REST API.
- A settings editor for `PalWorldSettings.ini` and the launch arguments.
- Optional Discord webhook notifications and a slash-command control bot.
