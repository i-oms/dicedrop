<p align="center"><img src="assets/logo.svg" width="96" alt="DiceDrop logo"></p>

# Contributing to DiceDrop

Thanks for helping out! DiceDrop is a serverless, peer-to-peer Ludo game for Windows, and contributions of every size are welcome: bug reports, fixes, new features, tests, docs, and even typo fixes.

This guide walks you through the whole process from a fresh machine to a merged pull request, with every git command you need. If you have contributed to open source before, the [Quick reference](#quick-reference) at the bottom may be all you need.

---

## Contents

1. [Ways to contribute](#1-ways-to-contribute)
2. [Set up your machine](#2-set-up-your-machine)
3. [Fork and clone the repository](#3-fork-and-clone-the-repository)
4. [Build, run and test](#4-build-run-and-test)
5. [Make your change on a branch](#5-make-your-change-on-a-branch)
6. [Commit your work](#6-commit-your-work)
7. [Keep your branch up to date](#7-keep-your-branch-up-to-date)
8. [Push and open a pull request](#8-push-and-open-a-pull-request)
9. [Respond to review](#9-respond-to-review)
10. [After your pull request is merged](#10-after-your-pull-request-is-merged)
11. [Project rules you must follow](#11-project-rules-you-must-follow)
12. [Fixing common git problems](#12-fixing-common-git-problems)
13. [Quick reference](#quick-reference)

---

## 1. Ways to contribute

| You want to… | Do this |
|---|---|
| Report a bug | [Open an issue](https://github.com/i-oms/dicedrop/issues/new). Say what you did, what you expected, and what happened. Include your Windows version (`winver`) and how many players were in the game. |
| Suggest a feature | Open an issue first and describe the idea. Agreeing on the design before you write code saves everyone time, especially for anything touching networking or game rules. |
| Fix a bug or build a feature | Comment on the issue to say you're taking it, then follow this guide. |
| Improve documentation | Edit the `.md` files directly; the same branch → pull request flow applies. |
| Test | Play a LAN game with several PCs and report anything odd. Multi-PC testing is the hardest thing for maintainers to do alone. |

Looking for a first task? Check issues labelled **good first issue**, or pick something from the roadmap in [docs/TECHNICAL.md](docs/TECHNICAL.md#7-limitations--roadmap): turn timer, rule variants in the lobby, reconnection, sound effects.

> **Never post a security problem in a public issue.** If you find a way to defeat screen-capture protection, cheat the fair dice, or crash other peers over the network, report it privately as described in [SECURITY.md](SECURITY.md).

---

## 2. Set up your machine

You need:

- **Windows 10 (2004 or newer) or Windows 11.** The game uses WPF and Windows capture-protection APIs, so it builds and runs only on Windows.
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**. Check with:

  ```
  dotnet --version
  ```

  It should print `10.x.x`.
- **[Git](https://git-scm.com/download/win)**. Check with:

  ```
  git --version
  ```
- **An editor.** Visual Studio 2022+, JetBrains Rider, or VS Code with the C# Dev Kit extension all work.
- **A GitHub account.**

### Configure git (once per machine)

Tell git who you are. This name and email appear on your commits:

```
git config --global user.name "Your Name"
git config --global user.email "you@example.com"
```

> Tip: to keep your real address private, use the `…@users.noreply.github.com` address from GitHub → **Settings** → **Emails**.

Make git handle Windows line endings sensibly:

```
git config --global core.autocrlf true
```

Optionally, make `main` the default name for new repositories and make `git pull` rebase instead of merge:

```
git config --global init.defaultBranch main
git config --global pull.rebase true
```

---

## 3. Fork and clone the repository

You don't have write access to the main repository, so you work on your own copy (a **fork**) and send changes back through a **pull request**.

### 3.1 Fork on GitHub

1. Open <https://github.com/i-oms/dicedrop>.
2. Click **Fork** (top right), then **Create fork**.

You now have `https://github.com/<your-username>/dicedrop`.

### 3.2 Clone your fork

```
git clone https://github.com/<your-username>/dicedrop.git
cd dicedrop
```

### 3.3 Connect to the original repository

Your clone already has a remote called `origin` that points to **your fork**. Add a second remote called `upstream` that points to **the original project**, so you can pull in other people's changes:

```
git remote add upstream https://github.com/i-oms/dicedrop.git
git remote -v
```

You should see:

```
origin    https://github.com/<your-username>/dicedrop.git (fetch)
origin    https://github.com/<your-username>/dicedrop.git (push)
upstream  https://github.com/i-oms/dicedrop.git (fetch)
upstream  https://github.com/i-oms/dicedrop.git (push)
```

Remember the rule: **you pull from `upstream` and push to `origin`.**

---

## 4. Build, run and test

From the repository root:

```
dotnet build DiceDrop.slnx
dotnet test DiceDrop.slnx
```

Run the game from source:

```
dotnet run --project src/Ludo.Presentation.Wpf
```

Run only some tests while you work (xUnit filter by class or test name):

```
dotnet test DiceDrop.slnx --filter "FullyQualifiedName~LudoGameTests"
```

Build the portable release exe exactly as it ships (runs every test first, then writes `dist\DiceDrop.exe`):

```
build.cmd
```

### Testing multiplayer

The game finds peers by LAN broadcast, so you need more than one copy running.

- **Best:** two or more PCs (or VMs with bridged networking) on the same network.
- On first launch, allow DiceDrop through Windows Firewall for **private networks**, or peers won't see each other.
- DiceDrop allows only one copy per Windows user, so testing on a single PC needs a second user account, a VM, or a second machine.

Please say in your pull request how you tested it, for example: "3 players on 2 PCs, played to the finish."

---

## 5. Make your change on a branch

**Never commit directly to `main`.** Every change goes on its own branch, which keeps your fork's `main` a clean mirror of upstream and lets you work on several things at once.

### 5.1 Start from the latest code

```
git checkout main
git fetch upstream
git merge --ff-only upstream/main
git push origin main
```

### 5.2 Create a branch

Name it after what it does, using one of these prefixes:

| Prefix | For |
|---|---|
| `feature/` | new functionality |
| `fix/` | bug fixes |
| `docs/` | documentation only |
| `test/` | tests only |
| `refactor/` | restructuring with no behaviour change |

```
git checkout -b feature/turn-timer
```

If there's an issue, putting its number in the branch name helps: `fix/42-dice-desync`.

### 5.3 Work and check your progress

As you edit, these commands show where you are:

```
git status                 # which files changed, which are staged
git diff                   # unstaged changes, line by line
git diff --staged          # what will go into the next commit
git log --oneline -10      # recent commits
```

Keep a branch focused on **one** change. A pull request that fixes a bug *and* reformats ten files *and* adds a feature is hard to review. Split it into separate branches.

---

## 6. Commit your work

### 6.1 Stage and commit

Stage specific files (preferred, so nothing unintended sneaks in):

```
git add src/Ludo.Domain/Gameplay/GameRules.cs tests/Ludo.Domain.Tests/LudoGameTests.cs
```

or stage everything you changed:

```
git add -A
```

Review what you're about to commit, then commit:

```
git diff --staged
git commit
```

`git commit` without `-m` opens your editor so you can write a proper multi-line message. For a small change, a one-liner is fine:

```
git commit -m "Fix exact-roll check for tokens in the home column"
```

### 6.2 Writing good commit messages

```
Add a per-turn timer to the lobby options

The host can now pick 15, 30 or 60 seconds per turn. When the timer
runs out the current player's turn is skipped on every peer, using
the existing turn-advance path so peers stay in lockstep.

Closes #17
```

- **First line:** under ~72 characters, imperative mood ("Add", "Fix", "Remove", not "Added" or "Fixes"), no full stop.
- **Blank line**, then a body explaining **why** and anything non-obvious. The diff already shows *what* changed.
- `Closes #17` (or `Fixes #17`) closes the issue automatically when the pull request is merged.

### 6.3 Small, frequent commits

Commit whenever you reach a working step. Every commit should build. Lots of small "wip" commits are fine while you work; you can tidy them up before review (see [12.4](#124-squash-messy-commits-before-review)).

---

## 7. Keep your branch up to date

While you work, `upstream/main` may move on. Bring those changes into your branch with a **rebase**, which replays your commits on top of the latest code and keeps history linear:

```
git fetch upstream
git rebase upstream/main
```

### If the rebase hits a conflict

Git stops and lists the conflicting files. For each one:

1. Open the file and look for the markers:

   ```
   <<<<<<< HEAD
   (code from upstream)
   =======
   (your code)
   >>>>>>> your commit
   ```
2. Edit it into the correct final version and delete the markers.
3. Mark it resolved:

   ```
   git add path/to/file.cs
   ```

Then continue:

```
git rebase --continue
```

Repeat until the rebase finishes. If things go wrong and you want to start over:

```
git rebase --abort
```

After resolving conflicts, **build and test again** before pushing:

```
dotnet build DiceDrop.slnx
dotnet test DiceDrop.slnx
```

---

## 8. Push and open a pull request

### 8.1 Push your branch to your fork

The first time you push a branch, set its upstream with `-u`:

```
git push -u origin feature/turn-timer
```

Later pushes on that branch are just:

```
git push
```

If you rebased after already pushing, the history changed, so a normal push is rejected. Use a **safe** force push, which refuses to overwrite anything you haven't seen:

```
git push --force-with-lease
```

> Never use plain `git push --force`, and never force-push `main`.

### 8.2 Open the pull request

1. Go to your fork on GitHub. A banner offers **Compare & pull request**; click it.
   (Or open <https://github.com/i-oms/dicedrop/compare> and choose your fork and branch.)
2. Check the target is **base repository: `i-oms/dicedrop`**, **base: `main`**.
3. Write a clear title and description. A good description includes:

   ```
   ## What
   Adds a configurable per-turn timer (15/30/60 s) chosen by the host in the lobby.

   ## Why
   Games stall when a player walks away. Closes #17.

   ## How I tested
   - dotnet test: all passing, plus 4 new tests in LudoGameTests
   - 3 players on 2 PCs, played a full game, let one turn time out

   ## Screenshots
   (Note: the game window is excluded from screen capture, so describe UI
   changes in words or photograph the screen with a phone.)
   ```
4. If the work isn't finished but you'd like early feedback, choose **Create draft pull request**.

### 8.3 Pull request checklist

Before you mark it ready for review:

- [ ] `dotnet build DiceDrop.slnx` succeeds with **zero warnings** (warnings fail the build)
- [ ] `dotnet test DiceDrop.slnx` passes
- [ ] New logic in Domain or Application has tests
- [ ] The branch is rebased on the latest `upstream/main`
- [ ] Tested in a real multiplayer game if networking, sync or UI changed
- [ ] The [project rules](#11-project-rules-you-must-follow) are respected
- [ ] README / [docs/TECHNICAL.md](docs/TECHNICAL.md) updated if behaviour, ports, protocol or build steps changed

---

## 9. Respond to review

A maintainer will review your pull request and may ask for changes. This is normal, and the goal is a better game, not a judgment of you.

To make requested changes, keep working on the **same branch**:

```
git checkout feature/turn-timer
# ...edit files...
git add -A
git commit -m "Use the shared executor for timer expiry"
git push
```

The pull request updates automatically, so there's no need to open a new one.

To pick up a reviewer's suggestion you accepted on GitHub, pull it into your local branch first:

```
git pull
```

Every pull request runs the **CI** workflow (build with warnings as errors, then all tests). When every conversation is resolved and CI is green, a maintainer will merge it.

---

## 10. After your pull request is merged

Clean up and get ready for the next contribution:

```
git checkout main
git fetch upstream
git merge --ff-only upstream/main
git push origin main

git branch -d feature/turn-timer
git push origin --delete feature/turn-timer
```

If `git branch -d` complains the branch isn't merged (this happens when the pull request was squash-merged), it is safe to force the delete:

```
git branch -D feature/turn-timer
```

To remove local references to branches already deleted on GitHub:

```
git fetch --prune
```

🎉 Thank you, you're now a DiceDrop contributor!

---

## 11. Project rules you must follow

DiceDrop has a few firm design rules. Pull requests that break them won't be merged, so please read these before you start coding.

### 11.1 Clean Architecture: dependencies point inward

```
 Presentation.Wpf ──► Application ──► Domain
        │                  ▲
        └──► Infrastructure┘
```

| Project | Put here | Must **not** |
|---|---|---|
| `Ludo.Domain` | Game rules, board, players, domain events | reference any other project or do I/O, timers, randomness, or `DateTime.Now`. It must stay pure and deterministic. |
| `Ludo.Application` | Use cases, ports (interfaces), sync protocol, fair dice | know about TCP, UDP, JSON, WPF or any concrete technology |
| `Ludo.Infrastructure` | Adapters that implement Application's ports (network, discovery, crypto, serialization) | contain game rules |
| `Ludo.Presentation.Wpf` | Views, view models, board rendering, capture protection, composition root (`App.xaml.cs`) | touch Domain types directly. The UI reads `SessionSnapshot` through `ISessionPresenter`. |

New external technology (a sound library, a new transport) gets a **port** (interface) in Application and an **adapter** in Infrastructure or Presentation, wired up in `App.xaml.cs`. Don't add a project reference that points outward. If you're unsure where something belongs, ask in the issue.

### 11.2 Any number of players

The board and rules support **2 to 8 players** (`BoardTopology` is N-player, with at least four arms). Never hard-code 4 players, 4 colours or a 4-sided board. Use `ArmCount`, each seat's `Arm` and `SeatPalette`, which is keyed by arm.

### 11.3 Screen-capture protection must never regress

The game is capture-proof by design and **fails closed**: if protection can't be applied, the app refuses to run.

- Every new window must derive from `ProtectedWindow`, never plain `Window`.
- Don't add tooltips, `MessageBox`, popups or context menus inside the game. Show notices within the protected window instead.
- Never weaken, delay or bypass the watchdog in `Security/`.

If your change adds any new on-screen surface, test it with Win+Shift+S and a screen-sharing app, and confirm it is not captured.

### 11.4 Peers must stay in sync

Every peer runs the same rules on its own copy of the game (deterministic lockstep), so:

- Anything that changes game state must be **deterministic**: identical inputs → identical results on every PC. No `Random`, no local clock, no dictionary-order dependence in Domain.
- Dice come only from the fair-dice hash chain (`FairDice/`). Never add another source of randomness to gameplay.
- Changing the wire format (`PeerMessages`, serializer, beacon) breaks compatibility with older builds. Bump `ProtocolInfo.Version` and call it out clearly in your pull request.

### 11.5 Portable single exe

DiceDrop ships as one self-contained `DiceDrop.exe` with no installer, no admin rights and no runtime to install.

- Don't require installation, elevation or files next to the exe.
- Think twice before adding a NuGet package. Every dependency grows the exe and must work with single-file publishing. Mention any new package and why in your pull request.
- Keep anything written to disk or the registry to the minimum, and only on user request. The one exception is "Start with Windows", which is on from the first run and can be turned off.

### 11.6 Code style

- The build has `TreatWarningsAsErrors` and nullable reference types **on**, so fix warnings rather than suppressing them.
- Match the surrounding code: file-scoped namespaces, `sealed` classes by default, `_camelCase` private fields, `PascalCase` members, `var` where the type is obvious.
- Comments explain **why**, not what. Keep them short.
- Domain and Application changes come with **xUnit tests** in `tests/`. Name tests after the behaviour, for example `Six_brings_token_out_of_base`.
- Keep the architecture and protocol sections of [docs/TECHNICAL.md](docs/TECHNICAL.md) accurate when you change those areas.

---

## 12. Fixing common git problems

### 12.1 "I committed to `main` by mistake"

Move the commit to a new branch, then reset `main`:

```
git branch fix/my-change          # new branch keeps your commit
git reset --hard upstream/main    # main goes back to upstream
git checkout fix/my-change
```

### 12.2 Undo the last commit but keep the changes

```
git reset --soft HEAD~1
```

### 12.3 Fix the last commit (message or a forgotten file)

```
git add forgotten-file.cs
git commit --amend
```

If you had already pushed it: `git push --force-with-lease`.

### 12.4 Squash messy commits before review

Combine your branch's commits into one clean commit:

```
git fetch upstream
git reset --soft upstream/main
git commit -m "Add a per-turn timer to the lobby options"
git push --force-with-lease
```

### 12.5 Park unfinished work to switch branches

```
git stash push -m "half-done timer UI"
git checkout other-branch
# ...later...
git checkout feature/turn-timer
git stash pop
```

### 12.6 Throw away all uncommitted changes to a file

```
git restore path/to/file.cs
```

> ⚠️ This permanently discards those edits.

### 12.7 "I lost a commit"

Git almost never truly deletes commits. Find it in the reflog and bring it back:

```
git reflog
git checkout -b rescued <commit-hash>
```

---

## Quick reference

```
# one-time setup
git clone https://github.com/<your-username>/dicedrop.git
cd dicedrop
git remote add upstream https://github.com/i-oms/dicedrop.git

# start a change
git checkout main
git fetch upstream
git merge --ff-only upstream/main
git checkout -b fix/short-description

# work
dotnet build DiceDrop.slnx
dotnet test DiceDrop.slnx
git add -A
git commit -m "Fix the thing"

# stay current
git fetch upstream
git rebase upstream/main

# publish
git push -u origin fix/short-description     # then open a PR on GitHub
git push --force-with-lease                   # after a rebase

# clean up after merge
git checkout main
git fetch upstream
git merge --ff-only upstream/main
git branch -d fix/short-description
git push origin --delete fix/short-description
```

---

## Code of conduct

Be kind and patient. Assume good intent, keep feedback about the code and not the person, and help newcomers. Harassment or personal attacks of any kind aren't tolerated, and maintainers may remove comments or block contributors who cross that line.

## License

By contributing, you agree that your contributions are licensed under the same license as the project (see `LICENSE`).
