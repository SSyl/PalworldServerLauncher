# FAQ and troubleshooting

Common questions about hosting with the Palworld Server Launcher. There's a good chance yours is answered
below. If not, open an [issue](https://github.com/SSyl/PalworldServerLauncher/issues) and ask.

Back to the [README](../README.md).

---

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
<summary><strong>My friends can't join, or Port Check says 8211 isn't accessible</strong></summary>

`127.0.0.1` is your own PC's address, so only you can use it. For anyone else to join, they connect to your
**Public IP**, and that port has to be reachable from outside your network. If Port Check fails, it's almost
always one of three things:

1. **Windows Firewall** hasn't allowed the Palworld server. Press the Windows key, type "Allow an app through
   Windows Firewall", click **Change settings**, and tick the boxes for anything named "Pal" / "PalServer". If
   it's not in the list, add it (it lives in `PalworldServerLauncher\palworlddedicatedserver\Pal\Binaries\Win64`).
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
<summary><strong>Where are my saves, settings, logs, and backups?</strong></summary>

Everything the launcher manages, its own `launcher.json` settings, the server install, backups, and logs,
lives in a `PalworldServerLauncher` folder next to the exe. The game's own settings live in
`PalWorldSettings.ini`, editable from the launcher (Server Settings and Launch Arguments) or by hand.

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

*(On an older version without Import: click Install to let it set up a fresh server, close the launcher, then
copy your existing server files over the top of the generated `PalworldServerLauncher\palworlddedicatedserver`
folder.)*

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
