# FAQ and troubleshooting

Common questions about hosting with the Palworld Server Launcher. There's a good chance yours is answered
below. If not, open an [issue](https://github.com/SSyl/PalworldServerLauncher/issues) and ask.

Back to the [README](../README.md).

---

<details>
<summary><strong>Why does Windows or my antivirus flag it?</strong></summary>

The exe isn't code-signed, so the first time you run it Windows shows a blue "Windows protected your PC" box.
Click **More info**, then **Run anyway**. Some antivirus tools flag it for the same reason, and because it's a
self-contained single-file build, which is a packaging style malware also uses.

Code-signing certificates cost a few hundred a year, which is hard to justify for a free project. If it
worries you, the full source is on GitHub and you can build the exe yourself.

</details>

<details>
<summary><strong>How do I connect to my server?</strong></summary>

In Palworld, click **Join Multiplayer Game** (not the invite-code option). Near the bottom of that screen
there's a field to type an IP address and port, with a Connect button next to it.

- Playing on the **same PC** that's running the server: enter `127.0.0.1:8211` and connect.
- Playing from **another PC on the same home network**: use the server PC's **local** IP (looks like
  `192.168.1.50:8211`). On the server PC, open Command Prompt, run `ipconfig`, and use the "IPv4 Address"
  under your active Ethernet or Wi-Fi connection.
- For **friends outside your network**: they use your **Public IP** (there's a copy button next to "Public
  IP" on the launcher's main window, it'll look like `203.0.113.5:8211`). Note: you usually **can't** reach
  your own server through your Public IP from inside your own home, that's normal, use `127.0.0.1` or your
  local IP instead.

</details>

<details>
<summary><strong>Can I run the server on the same PC I play on?</strong></summary>

Yes. Start the server in the launcher, then connect to `127.0.0.1:8211` in **Join Multiplayer Game**.

Check you have the memory for it first. Palworld's [official
requirements](https://docs.palworldgame.com/getting-started/requirements/) recommend 16 GB for the dedicated
server on its own, and the game needs its share on top of that.

</details>

<details>
<summary><strong>My friends can't join, or Port Check says 8211 isn't accessible</strong></summary>

`127.0.0.1` is your own PC's address, so only you can use it. For anyone else to join, they connect to your
**Public IP**, and that port has to be reachable from outside your network. If Port Check fails, it's almost
always one of three things:

1. **Windows Firewall** hasn't allowed the Palworld server. Press the Windows key, type "Allow an app through
   Windows Firewall", click **Change settings**, and tick the boxes for anything named "Pal" / "PalServer". If
   it's not in the list, add it (it lives in `PalworldServerLauncher\PalServer\Pal\Binaries\Win64`, or
   `PalworldServerLauncher\palworlddedicatedserver\Pal\Binaries\Win64` if you installed before v1.1.0).
2. **Router port forwarding** isn't set up, or points at the wrong device. Search "[your router model] port
   forward", then forward **UDP 8211** to your PC's **local** IP (the one that looks like `192.168.x.x`,
   **not** `127.0.0.1`).
3. **CGNAT.** Some ISPs don't give you a real public IP, which breaks port forwarding no matter what you do.
   If your firewall and router are definitely correct and it still fails, this is the likely cause. You can
   skip port forwarding entirely with a free tool like **Tailscale**, **ZeroTier**, or **Radmin**: you and
   your friends each install it, it gives every PC a shared address (starting with `100.`), and they connect
   to yours in Palworld (for example `100.15.20.50:8211`).

</details>

<details>
<summary><strong>My server isn't showing in the community server list</strong></summary>

By default the launcher runs your server as **private** (join by IP only). To list it publicly, open
**Server Settings** -> **Launch Arguments** tab, tick **Community/Public Server (`-publiclobby`)**, hit
**Save**, then restart the server. Then:

- Forward **UDP 27015** (the Steam query port the server uses to advertise itself to the list) and confirm it
  with **Port Check**. Your game port **8211** still needs forwarding too, so people can actually connect once
  they find you.
- Give it a **unique Server Name** and search that exact name in the browser instead of scrolling, thousands
  of servers share the default name.

Fair warning: Palworld's community browser is unreliable and can be slow to show a server even when
everything's set up correctly.

</details>

<details>
<summary><strong>Does the launcher have to stay open?</strong></summary>

The server keeps running if you close the launcher. Next time you open it, it offers to pick that server back
up, or does it on its own if you turn that on in Launcher Settings.

The catch is that the launcher is what performs scheduled restarts, scheduled backups, auto-updates, and crash
recovery. None of those happen while it's closed. If you want them without keeping a window open, turn on
**Start at login** so it comes up with Windows.

</details>

<details>
<summary><strong>The Server and Chat tabs are empty after reconnecting to a running server</strong></summary>

The launcher can only read the output of a server it started itself. Windows makes that connection when the
program launches and there's no way to add it afterward, so a server that was already running keeps its output
to itself. Palworld writes no log file either, so there's nothing else to read it from.

Everything that comes through the REST API carries on working, including the players list, the live stats,
scheduled restarts, backups, and crash recovery. Restart the server from the launcher and both tabs fill again.

</details>

<details>
<summary><strong>Do I need the REST API?</strong></summary>

The server runs without it, but the launcher loses most of what makes it useful. With the REST API off there
are no live stats, no in-game restart warnings, and no detection of a server that has stopped responding.
Backups capture the last autosave instead of the current world, and stopping the server becomes a force-stop
rather than a clean shutdown.

The launcher offers to turn it on the first time you start the server, and generates a secure admin password
for you. Keep its port off the public internet.

</details>

<details>
<summary><strong>Where are my saves, settings, logs, and backups?</strong></summary>

Everything the launcher manages, its own `launcher.json` settings, the server install, backups, and logs,
lives in a `PalworldServerLauncher` folder next to the exe. The game's own settings live in
`PalWorldSettings.ini`, editable from the launcher (Server Settings and Launch Arguments) or by hand.

</details>

<details>
<summary><strong>Why are some log lines out of order, or showing an older time?</strong></summary>

Palworld doesn't hand its player and chat lines over as they happen. It holds them and releases them in
bursts, sometimes a minute later. The launcher shows the time Palworld recorded for the event, not the moment
the line arrived, so a chat message carries the time it was actually sent. A line can therefore sit below one
with a later timestamp.

The log file on disk keeps both times, so nothing is lost.

</details>

<details>
<summary><strong>How do I change where backups go?</strong></summary>

Use the button next to **Backup Location** on the main window and pick a folder. Backups already written stay
where they are, so the change only applies to new ones.

</details>

<details>
<summary><strong>How do I uninstall it, or move it somewhere else?</strong></summary>

Everything it manages lives in one `PalworldServerLauncher` folder next to the exe, so deleting that folder
and the exe removes it completely. Nothing goes into the registry, and the only file outside that folder is
the optional **Start at login** shortcut.

To move it, move the exe and that folder together and keep them side by side. If you had **Start at login**
turned on, the shortcut still points at the old path, so switch it off and on again in Launcher Settings after
the move.

</details>

<details>
<summary><strong>Can I stop the server from auto-updating, or lock it to a version?</strong></summary>

Yes, as of v0.4.0. Tick **Pin server version** in the Misc section. It freezes the server on its current
build and turns off automatic updates until you unpin it. Note: this **holds** your current version, it can't
downgrade a server that already updated. Steam only reliably serves the latest build, and downgrading an
existing world risks corrupting the save, so pinning is meant to prevent unwanted updates going forward.

</details>

<details>
<summary><strong>I already have a dedicated server. Can I use it instead of installing a fresh one?</strong></summary>

Yes, as of v0.4.0. Use the **Import server** button on the main window, point it at your existing server
folder (the one containing PalServer.exe), and it copies it into the launcher so it can manage it. Your
original is left where it is until you've confirmed the managed copy works.

The button only appears when the launcher doesn't already have a server. To bring in a different one, delete
the current install at `PalworldServerLauncher\PalServer` first (or `palworlddedicatedserver` if the launcher
set it up before v1.1.0).

*(On an older version without Import: click Install to let it set up a fresh server, close the launcher, then
copy your existing server files over the top of the generated `PalworldServerLauncher\PalServer` folder, or
`palworlddedicatedserver` on installs made before v1.1.0.)*

</details>

<details>
<summary><strong>Do I have to use Steam Workshop to mod? (Workshop won't connect, or installing mods manually)</strong></summary>

The Steam Workshop connection is only a convenience for downloading and auto-updating Workshop mods. You
can ignore it and mod the server exactly like any other Palworld dedicated server.

The only thing the launcher changes is where the server lives: it's installed at
`PalworldServerLauncher\PalServer\`. (Installs created before v1.1.0 are in
`PalworldServerLauncher\palworlddedicatedserver\` instead, and stay there. Use whichever folder you actually
have.) So follow whatever your mod or mod loader documents, and wherever a guide tells you to use your
`Palworld` / `PalServer` folder, use that one instead. A few common cases:

- **Loose `.pak` mods:** these usually go in `...\PalServer\Pal\Content\Paks\~mods`, but follow
  the mod author's own instructions.
- **UE4SS you install yourself:** UE4SS goes into `...\PalServer\Pal\Binaries\Win64`, and its
  mods live under its own `ue4ss\Mods` folder there, per UE4SS's instructions.
- **A UE4SS mod that isn't on Steam Workshop, when you got UE4SS through Workshop:** put it in
  `...\PalServer\Mods\NativeMods\UE4SS\Mods`

There are a lot of different mod types and systems out there, so I can't outline them all here. What I can say
is that the launcher supports anything a standard Palworld dedicated server would.

</details>

<details>
<summary><strong>Can I install UE4SS myself instead of using the Steam Workshop one?</strong></summary>

Yes, UE4SS is on GitHub and NexusMods too. Two things to know if you go that way:

- **Don't have both copies at once.** A Steam Workshop UE4SS plus a hand install in `Pal\Binaries\Win64` means
  the server loads two copies of UE4SS and crashes on launch, so pick one. To turn the hand install off
  without deleting it, rename `Pal\Binaries\Win64\dwmapi.dll` to `dwmapi.dll.bak`.
- **Workshop mods won't reach a hand install.** The server puts Workshop mods in
  `Mods\NativeMods\UE4SS\Mods`, which a hand-installed UE4SS doesn't read, so they sit there without loading.

The Mods panel warns you about the first one whenever it finds both, and about the second one when you have
mods enabled.

</details>

<details>
<summary><strong>How do I change the language?</strong></summary>

Pick one the first time you run the launcher, or change it later in Launcher Settings, under the gear icon in
the top-right. The launcher restarts to apply it.

Ten are available: English, Deutsch, Español, Français, Português (Brasil), Русский, 日本語, 简体中文, 繁體中文,
and 한국어. Everything except English is machine-translated, so corrections via an issue or pull request are
welcome.

</details>

<details>
<summary><strong>Can I use a world I made in-game (a co-op session), not from a dedicated server?</strong></summary>

Not directly. A dedicated server's save is a different format from the one Palworld creates for an in-game
co-op session, so you can't just drop it in. There are third-party converters that move the world and player
data across, but that's outside what this tool does and I can't vouch for any specific one. One I see
recommended is https://physgun.com/tools/palworld-save-converter/

</details>

<details>
<summary><strong>Can I manage a server running on another machine (a NAS, a VPS, a friend's PC)?</strong></summary>

No. The launcher manages the server **locally**, it runs on the same machine as the server and controls that
install directly. It can't connect to or change a server on a different computer, so you'd run the launcher on
whatever machine is actually hosting.

</details>

<details>
<summary><strong>Is there a Linux version?</strong></summary>

Probably not, sorry. Palworld already ships an official Docker image for Linux (they don't recommend Docker on
Windows due to reduced performance), and there are third-party images that add auto-updating and scheduled
restarts. Between the many distros and their different firewall / SteamCMD setups, a native Linux build is a
lot of extra work for a spare-time project. On Linux you can reproduce most of what this does with cron jobs
and Docker, and there are community guides for it.

</details>
