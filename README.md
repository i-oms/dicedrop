<p align="center">
  <img src="assets/logo.png" width="120" alt="DiceDrop logo" />
</p>

<h1 align="center">DiceDrop</h1>

<p align="center">
  <strong>One EXE. No server. No account. No cheating.</strong><br>
  Serverless, peer-to-peer Ludo for 2 to 8 players on Windows.
</p>

<p align="center">
  <a href="https://github.com/i-oms/dicedrop/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/i-oms/dicedrop?style=flat-square&label=Download&color=blue" /></a>
  <a href="https://github.com/i-oms/dicedrop/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/i-oms/dicedrop/ci.yml?branch=main&style=flat-square&label=CI" /></a>
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/github/license/i-oms/dicedrop?style=flat-square" /></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square" />
  <img alt="Windows 10 2004+ / 11" src="https://img.shields.io/badge/platform-Windows%2010%2F11-blue?style=flat-square" />
  <img alt="Downloads" src="https://img.shields.io/github/downloads/i-oms/dicedrop/total?style=flat-square" />
</p>

<p align="center">
  <a href="https://github.com/i-oms/dicedrop/releases/latest">⬇️ Download DiceDrop.exe</a> •
  <a href="#quick-start">Quick start</a> •
  <a href="#how-to-play">How to play</a> •
  <a href="docs/TECHNICAL.md">Technical docs</a> •
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

---

## What is this?

**DiceDrop** is portable Ludo for a local network. Open the EXE on every PC on the same Wi-Fi or LAN and they find each other and share a lobby automatically. There is no central server, no login and no installer, and game traffic never leaves your network.

> `DiceDrop.exe`: double-click and play. Chats are never saved, and the game window can't be screen-captured.

<!--
  Screenshots and recordings: the window is excluded from screen capture, so photograph the screen
  with a phone. Save them in assets/ and embed them here, e.g. ![Board](assets/screenshot-board.png)
-->

## Features

- **2 to 8 players.** The classic cross board for 2 to 4 players; a six-, seven- or eight-armed star board for 5 to 8, built from the same arms. Your own yard is always at the bottom left.
- **Zero server.** Full-mesh peer-to-peer over TCP, with lobby discovery by UDP broadcast. The host only keeps the member list.
- **Fair dice with no trusted roller.** Every player commits to a secret hash chain before the first roll, and each die is `SHA-256(roll ‖ all reveals) mod 6 + 1`. Nobody can steer or predict a roll.
- **Spectating.** Not playing? Watch live. Leave a game and keep watching. Late joiners replay the full history.
- **Built-in messenger.** Lobby group chat, private chats, and **Nudge** (shakes the other player's window and plays a sound).
- **Notifications.** Ask the people you pick a question with your own answer buttons, like **Yes** and **No**. Everyone asked sees every answer, so you can decide together.
- **Capture-proof window.** `WDA_EXCLUDEFROMCAPTURE` keeps the game out of PrintScreen, Snipping Tool, OBS and Teams/Zoom/Discord screen sharing.
- **Portable and private.** One self-contained EXE of about 66 MB with no .NET install needed. Settings live in `%APPDATA%\DiceDrop\settings.json`, and the registry is touched only if you turn on **Start with Windows**.
- **Update notices.** Checks GitHub releases at startup and every 6 hours, then shows a banner. It never downloads or installs anything by itself. To update, just open the new EXE: it closes the running copy and takes over.

## Quick start

1. Download **[DiceDrop.exe from the latest release](https://github.com/i-oms/dicedrop/releases/latest)**. Each release also has a `DiceDrop.exe.sha256` checksum.
2. Copy it to every player's PC (Windows 10 version 2004 or newer, or Windows 11).
3. Run it. When Windows Firewall asks, choose **Allow** for **private networks**, because peers connect directly to each other.
4. You join the lobby automatically. Pick your name and photo in **Settings** (the gear next to your name).

That's it. No port forwarding is needed.

**Playing over the internet?** DiceDrop has no relay server and finds lobbies by LAN broadcast, so you need a virtual LAN that carries broadcasts. **ZeroTier** and **Radmin VPN** work; **Tailscale** doesn't forward broadcasts, so it won't work.

**Everyone needs the same version.** This version speaks network protocol 3, and copies running an older protocol can't join its lobbies.

<details>
<summary><b>PCs don't find each other? Click for the fix.</b></summary>

Press **F12** (or the **Network log** button, bottom left) on both PCs. The log is kept in memory only and shows, in order:

- **env**: this PC's network adapters, whether the network is Private or Public, and the Windows Firewall rules for this exact EXE. Lines starting with `PROBLEM` name the cause and the fix.
- **udp**: which discovery ports opened, where beacons go, and which PCs were heard. "Heard only beacons from this PC" means no other PC's beacons are arriving: check the firewall on both PCs, check they're on the same subnet, and turn off the router's Wi-Fi client (AP) isolation.
- **tcp**: connections in and out, and why a connection failed. *Timed out* usually means the host's firewall; *refused* means nothing is listening.
- **lobby**: what auto-lobby decided (join, host, merge) and why.

**Copy all** puts the log on the clipboard so you can compare both PCs.

Most of the time the cause is a Windows Firewall **BLOCK** rule that was created when someone cancelled the firewall prompt. Delete that rule in *Windows Defender Firewall → Advanced settings → Inbound Rules* and allow DiceDrop again. Firewall rules are tied to the EXE's path, so a moved `DiceDrop.exe` needs a new rule.

</details>

## How to play

1. **The lobby forms by itself.** The first PC to open DiceDrop becomes the host. If two lobbies open at the same moment, the smaller one merges into the bigger one within a second or two. If the host leaves, the longest-standing member takes over.
2. **Chat.** The window works like a messenger. People and chats are on the left, the open chat is in the middle, and games are on the right. **Lobby** is the group chat; click a person to chat privately. In a private chat, **Nudge** shakes their window (at most once every 10 seconds). The tray icon's **Send message…** sends a message without opening the window.
3. **Invite.** Anyone can pick a game on the right and click **Invite players**, or use **Invite to Ludo** in a private chat. Invitees choose **Join game** or **Not now**, and whoever sent the invite clicks **Begin game** when ready.
4. **Roll and move.** Click the dice or press **Space**, then click one of your bouncing pawns.
5. **Watch or leave.** Players not in the game can **Watch** it live. Players can **Leave game** at any time and keep watching. Whoever started the game can **Stop game** for everyone.

**Rules:** a 6 brings a token out. A 6, a capture, or bringing a token home earns another roll. Three 6s in a row forfeit the turn. Start squares and ★ squares are safe. You need the exact roll to finish. The game continues until everyone has a finishing place.

## Build from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bat
build.cmd                 :: runs all tests, then writes dist\DiceDrop.exe (self-contained)
build.cmd D:\out          :: same, into another folder
```

From PowerShell run `.\build.cmd`, and from Git Bash run `./build.cmd`. For development:

```bash
dotnet build DiceDrop.slnx
dotnet test DiceDrop.slnx
dotnet run --project src/Ludo.Presentation.Wpf
```

**Versioning.** `build.cmd` stamps the newest `vX.Y.Z` git tag behind the current commit, or `DICEDROP_VERSION` if it is set (for example `set DICEDROP_VERSION=1.2.0`). A build without either, including a plain `dotnet build`, is version `0.0.0` and never checks for updates.

**Releasing (maintainers).** Push a tag such as `git tag v1.2.0 && git push origin v1.2.0`, or run **Actions → Release → Run workflow**. The [release workflow](.github/workflows/release.yml) accepts only strictly increasing `vMAJOR.MINOR.PATCH` versions. It runs the tests, publishes the EXE, and attaches it with its SHA-256 checksum to a new GitHub release.

## Documentation

The technical details are in **[docs/TECHNICAL.md](docs/TECHNICAL.md)**:

- Architecture (Clean Architecture, project map)
- How peers stay in sync (mesh, ordering, desync detection)
- The fair dice protocol
- Ports and the discovery protocol
- How screen-capture protection works, and what it can't stop
- Limitations and roadmap

## Contributing

Contributions are welcome: bug reports, fixes, features, tests and docs.

1. Fork the repo, create a branch such as `feature/turn-timer` or `fix/42-dice-desync`, and make sure `dotnet test DiceDrop.slnx` passes.
2. Open a pull request against `main`.

[CONTRIBUTING.md](CONTRIBUTING.md) is the step-by-step guide, with every git command and the project rules. To report a security issue, see [SECURITY.md](SECURITY.md).

Good first issues: a turn timer, rule variants in the lobby, reconnection, and game sound effects.

## License

[MIT](LICENSE). You're free to use, copy, modify and distribute DiceDrop, including commercially, as long as you keep the copyright notice and license text.

---

<p align="center">Built with C#, WPF and a lot of UDP beacons. If you like it, ⭐ star the repo so others can find it.</p>
