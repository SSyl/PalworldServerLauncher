# Advanced usage

Optional extras for running more than one server on the same machine, or driving the launcher from the
command line.

Back to the [README](../README.md).

---

## Running more than one server

Each copy of the exe runs one server, installed next to it. To run several on the same machine, drop a copy
of the exe into its own folder per server. Each one stays completely separate (settings, logs, install,
backups). Just give each server its own ports:

- **Listen port** (`-port`, default 8211), set under **Launch Arguments**.
- **REST API port** (default 8212) and **RCON port** (default 25575, if you turn it on), set in that
  server's `PalWorldSettings.ini`.

The Steam query port picks the first free one automatically (starting at 27015), or you can set a fixed
**Query port** under Launch Arguments if you forward it.

Running as many launchers as you have folders is fine and is the intended setup. What is not fine is two
launchers on the *same* folder, since they would share one server, one config, and one data folder, and fight
over restarts, backups, and updates. Opening a second launcher from a folder that already has one running
offers to force close the other one, or to exit. Force closing the other launcher does not stop its server,
which keeps running and is picked back up when a launcher next opens.

## Command-line options

You can double-click the launcher, or start it from a terminal with a couple of extra options:

- `--debug` (or `--verbose`): write more detailed logs.
- `--console`: mirror the launcher's logs into the terminal you started it from, handy for keeping an eye on
  a server from the command line.
- `--start-server`: open the launcher and bring the server up on load, adopting one that's already running.
  Good for a scheduled task or a hands-off start.
- `--install-server`: install SteamCMD and the server without opening the window, then exit.
- `--stop-server`: save and shut the server down, then exit. No window. Add a number of seconds
  (`--stop-server 60`, up to 3600) to give players an in-game countdown first.
- `--kill-server`: end the server process immediately, with no save. For a server that has stopped responding.

```powershell
PalworldServerLauncher.exe --console --debug
PalworldServerLauncher.exe --stop-server 60
```

### Stopping a server the launcher is already running

`--stop-server` works whether or not a launcher window is open. If one is open, it hands the request to that
launcher and lets it do the shutdown, so everything behaves exactly as if you had clicked Stop: the shutdown
backup runs, Discord is notified, and the launcher knows the server was stopped on purpose. Without this, an
outside shutdown would look like a crash to the open launcher and it would start the server straight back up.

While it waits, the command prints the launcher's own progress, so you can watch the backup and the shutdown
in the terminal you ran it from.

If no launcher is open, the command stops the server itself, doing the same save, shutdown backup, and
shutdown that the launcher would.

A few details worth knowing:

- **A plain `--stop-server` waits** until the server has actually stopped before it exits, so exit code 0 means
  it is down. That includes the case where it was not running to begin with, so a script can run this to be
  sure. Anything else means the stop failed, and the reason is printed.
- **A countdown returns once the server has accepted it**, rather than waiting the countdown out, so its exit
  code 0 means the countdown started, not that the server is down yet. It shuts down on its own once the
  countdown runs out. Acceptance comes after the save and the shutdown backup, so the command takes as long as
  those do. If the server refuses the request or does not answer, the command reports that and exits non-zero.
- **`--stop-server` needs the REST API enabled** to save and shut down cleanly. With it off the command
  refuses, and you can use `--kill-server` instead, though the server then loses anything since its last
  autosave.
- **It only reaches launchers running under your own Windows account.** A launcher started by a different
  account (a service, or a scheduled task set to run as another user) will not answer, and the command falls
  back to stopping the server directly.
- **Each copy of the exe controls its own server.** Run the command from the folder of the server you want to
  stop, the same way each copy keeps its own settings and install.
