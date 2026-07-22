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

## Command-line options

You can double-click the launcher, or start it from a terminal with a couple of extra options:

- `--debug` (or `--verbose`): write more detailed logs.
- `--console`: mirror the launcher's logs into the terminal you started it from, handy for keeping an eye on
  a server from the command line.
- `--start-server`: open the launcher and bring the server up on load, adopting one that's already running.
  Good for a scheduled task or a hands-off start.
- `--install-server`: install SteamCMD and the server without opening the window, then exit.

```powershell
PalworldServerLauncher.exe --console --debug
```
