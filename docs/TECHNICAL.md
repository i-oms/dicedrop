# DiceDrop Technical Documentation

This document is for contributors and curious engineers. For the user guide, see [README.md](../README.md); for how to contribute, see [CONTRIBUTING.md](../CONTRIBUTING.md).

## 1. Architecture — Clean Architecture

Dependencies point inward only, enforced by project references.

```
Presentation.Wpf ──► Application ──► Domain
       │                  ▲
       └──► Infrastructure┘
```

| Project | Responsibility | May depend on |
|---|---|---|
| `Ludo.Domain` | Entities & rules: `LudoGame` aggregate, N-player `BoardTopology` (≥4 arms), `GameRules`, domain events. Pure, deterministic, no I/O/time/randomness. | nothing |
| `Ludo.Application` | Use cases exposed by `LudoApplication`: lobby (`AutoLobby`, `HostLobby`, `JoinLobby`, `LeaveSession`), games (`ProposeGame`, `AcceptInvite`/`DeclineInvite`, `StartGame`, `LeaveGame`, `StopGame`, `RollDice`, `MoveToken`), messenger (`SendChat`, `SendDirectChat`, `Nudge`, `SendPoll`, `VotePoll`, `RenamePlayer`, `UpdateProfile`) and `CheckForUpdate`. Ports (`IPeerNetwork`, `ILobbyDiscovery`, `ICryptoProvider`, `ISessionPresenter`, `IReleaseFeed`, `IDiagnosticLog`), the sync protocol and fair dice. | Domain |
| `Ludo.Infrastructure` | Adapters: TCP mesh transport, UDP LAN discovery, SHA-256/CSPRNG, JSON wire format, GitHub release feed, network/firewall diagnostics. | Application |
| `Ludo.Presentation.Wpf` | MVVM UI, board renderer, capture protection, tray icon, settings, composition root `App.xaml.cs` — only place that knows all layers. | Application, Infrastructure |

UI never touches domain. It receives immutable read models `SessionSnapshot` via `ISessionPresenter`.

The wire protocol is versioned (`ProtocolInfo.Version`, currently **5**: profiles, private chat, nudges, game invites, invite acknowledgements, notifications). Peers on a different version can't join each other's lobbies, so any change to `PeerMessages`, the serializer or the beacon must bump it.

**Transport security:** peer traffic is plain JSON over TCP on the local network. It is not encrypted or authenticated beyond the game handshake; fair dice and lockstep validation protect game integrity, not confidentiality.

## 2. How Peers Stay in Sync

### Mesh, not server
Every member connects to every other. Host keeps member list and greets newcomers. On host leave, first member in join order becomes host — deterministic because all peers hold same list.

### Games inside lobby
Proposer = initiator. One game per lobby: while one is being set up or played nobody else can propose, and a running game beats any invite. If two invites cross, lower ID wins on every peer. Every member tells the initiator once the invite is the one it holds (`MatchSeenMessage`); the initiator can only start after all connected members have, or after a 5-second grace period so a silent member can't block the start, so a crossing invite can't pull seated players away from a game that has begun. A member that drops or ignores an invite because something beats it tells that invite's initiator (`MatchOutrankedMessage`), which withdraws it, so an initiator that can't hear the winning game itself never waits forever. A peer seated in a start it has given up on sends a leave so the game doesn't wait on its dice. Initiator seats `self + accepted` and sends start message. Only initiator can stop. Game ends if initiator leaves lobby.

### Notifications
A notification (`PollMessage`) is sent only to the members its sender picked, along with the full recipient list. Each answer (`PollVoteMessage`) goes straight from the recipient to the sender and the other recipients, so members left out never hear about it. An answer can overtake its notification, in which case it is parked until the notification arrives. Answers can be changed: links keep their order, so all peers settle on the latest. The sender doesn't answer. Like chat, notifications live in memory and are dropped when the lobby is left. While the main window isn't active, a notification waiting for an answer shows in an alert window (`PollAlertWindow`) with its buttons. It has no timer: a card leaves when it is answered, clicked or dismissed.

### Spectators
Spectators don't participate in dice protocol. Initiator keeps append-only log of rolls/moves/drop-outs and streams to non-players. Late joiners replay log on fresh board. Gap detection -> re-request. A replay of an unknown match is only accepted from its seat 0 and never ends an open invite; it waits until the invite is gone. A peer seated in a running game ignores `LobbyClosedMessage` and `JoinRejectedMessage`.

### Deterministic lockstep
Ludo is turn-based — only current player may act. Each peer applies roll/move to its own `LudoGame` and validates against `GameRules`. Illegal/out-of-turn rejected locally.

### Ordering
All session state changes run on single executor. Message arriving ahead of dependency (e.g. move before roll) is parked and retried.

### Desync detection
Every roll request carries state digest. Mismatch logged on all peers.

## 3. Fair Dice — No Trusted Roller

Problem: in P2P, who rolls? Host could rig.

Solution: Commitment scheme with hash chains.

1.  Each player generates fresh secret hash chain `H_n -> ... -> H_0` per game. `H_0` is anchor.
2.  On AcceptInvite, publishes anchor. Initiator freezes all anchors in Start message. Each peer cross-checks anchors received directly vs Start.
3.  For roll `i`, every peer reveals `H_i`, verifiable against `H_{i-1}`.
4.  Die = `SHA-256(i || reveals in seat order) mod 6 + 1`

Properties:
- All values fixed before first roll — last revealer can't steer.
- No one can predict die until all reveals arrived.
- Forged reveal fails verification on every peer -> forger removed.

Implementation: `ICryptoProvider` (SHA-256 + CSPRNG) in Infrastructure.

## 4. Ports & Discovery

No fixed port can be guaranteed free on Windows (dynamic range 1024-65535 + Hyper-V/WSL reservations).

**Game traffic (TCP):**
- Host tries 47777 exclusive bind. If taken, uses OS-chosen free port. Joiners always OS-chosen. Handshake-less connections dropped.

**Discovery (UDP broadcast):**
- Host beacons 1/sec to 47778, 45778, 43778.
- Browsers listen on every bindable port -> works if one taken.
- Beacon carries real game port -> fallback auto-found.
- Wi-Fi often drops broadcasts, so once peer heard, beacons also unicast directly to it for as long as it keeps announcing.

**Auto-lobby:**
- While idle, joins best lobby heard (most players, then lowest ID), or opens one after 1.5-3s silence.
- Outranked lobby with room closes and moves.
- Lobby with game being set up/played never merges away.
- Lobbies keep announcing during games -> latecomers join and watch.
- Joined/tried lobby skipped for 20s to avoid bounce.
- One-way hearing possible (firewall), so host whose lower-ranked rival hasn't moved within 6s moves itself.

## 5. Screen-Capture Protection

- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` in `OnSourceInitialized`, before first frame shown. Captures show whatever behind window. Older Win10 fallback `WDA_MONITOR` (black).
- Fails closed: if protection can't be applied, app refuses to run. 200ms watchdog re-checks affinity. If stripped, content removed and window closes unless restored.
- Process-wide WinEvent hook protects every other top-level window (popups, menus). Tooltips/context menus disabled app-wide, notices shown in-window.

Defeats: PrintScreen, Snipping Tool / Win+Shift+S, Xbox Game Bar, OBS, ShareX, Teams/Zoom/Discord/Meet sharing, GDI, Windows.Graphics.Capture, DXGI duplication.

**What no software can prevent:**
- Phone camera, HDMI capture card/splitter
- Admin with kernel driver, patched DWM, code injection
- VM capture from host. RDP is covered in normal sessions.

## 6. Build & Release Pipeline

```
build.cmd -> dotnet test -> dotnet publish self-contained single-file win-x64 -> dist\DiceDrop.exe (~66 MB)
Version: DICEDROP_VERSION env, else newest vX.Y.Z tag behind current commit, else 0.0.0
Update check: GitHub API latest release on startup + every 6h, banner -> opens release page, no auto-download.
              Skipped for 0.0.0 builds; fails quietly offline (reason in network log).
Settings: %APPDATA%\DiceDrop\settings.json
Startup: optional, HKCU\Software\Microsoft\Windows\CurrentVersion\Run (only when "Start with Windows" is on)
Photos: shrunk to small JPEG, shared only with lobby
```

**CI** (`.github/workflows/ci.yml`): every push to `main` and every pull request runs `dotnet build` (warnings are errors) and `dotnet test` on `windows-latest`.

**Release** (`.github/workflows/release.yml`): triggered by pushing a `v*` tag or by a manual run with a version. It rejects anything that isn't a strictly increasing `vMAJOR.MINOR.PATCH`, then tests, publishes, and creates a GitHub release with `DiceDrop.exe` and `DiceDrop.exe.sha256` and auto-generated notes.

## 7. Limitations & Roadmap

- No reconnection: dropping peer removed, game continues
- Crash mid-broadcast of dice reveal can cause rare disagreement (digest detects, not repairs)
- No relay/NAT traversal
- Roadmap: turn timer, rule variants (GameRules already supports), reconnect with state transfer, sound, cross-platform viewer

## 8. Testing

- Domain: pure unit tests, deterministic board topology for 2-8 players
- Application: fair dice and update-check tests, plus multi-peer session tests that run real `TcpPeerNetwork` instances over loopback with scripted discovery
- Run: `dotnet test DiceDrop.slnx` or `build.cmd`
