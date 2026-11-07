<div align="center">

<img src="docs/brand/jay-feather-amber-v1.png" alt="Jay" width="96">

# Jay

**A clean, lightweight launcher for Minecraft: Java Edition**

No telemetry, no remote switches. Download, unzip, run.

[![Latest release](https://img.shields.io/github/v/release/warriorleavestar/jay-launcher?color=d98a1f)](https://github.com/warriorleavestar/jay-launcher/releases/latest)
[![License](https://img.shields.io/github/license/warriorleavestar/jay-launcher)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-blue)

[简体中文](README.md) | English

![Jay home page](docs/images/jay-home.png)

</div>

The project was called Amber; from v0.7.0 it is named **Jay**, with the Jay name and feather icon. Existing accounts and settings carry over. See the [rename and compatibility notice](docs/announcements/2026-11-06-jay-rename.md) (Chinese).

> **v0.7.0 multiplayer does not work with older versions.** Multiplayer encryption and joining were rebuilt: rooms use new `JAY2-` invite codes and direct connections use `JAY2D-` / `JAY2R-` codes. Old `AMB-` room and direct codes are no longer accepted, so everyone playing together needs v0.7.0.
>
> Features marked **(experimental)** are complete and pass the automated tests but have not been fully verified in real-world use yet. Please [report problems](https://github.com/warriorleavestar/jay-launcher/issues/new/choose).

## Download

Get the package for your computer from [Releases](https://github.com/warriorleavestar/jay-launcher/releases/latest). v0.5.9 and earlier use the `Amber-` archive prefix and `Amber.exe`.

| System | File | Run |
|---|---|---|
| Windows 10+ x64 | `Jay-win-x64-aot.zip` (recommended) | Unzip and run `Jay.exe` |
| Apple silicon Mac | `Jay-osx-arm64.tar.gz` | Extract and run `./Jay` in Terminal |
| Intel Mac (experimental) | `Jay-osx-x64.tar.gz` | Extract and run `./Jay` in Terminal |
| Linux x64 (experimental) | `Jay-linux-x64.tar.gz` | Extract and run `./Jay` |
| Linux ARM64 (experimental) | `Jay-linux-arm64.tar.gz` | Extract and run `./Jay`; install Java through your distribution |
| Jay Server (experimental) | `JayServer-<platform>.zip` / `.tar.gz` | See [server/README.md](server/README.md) |

The recommended zip is compiled ahead of time to native code (Native AOT): the window opens in about 0.3 seconds and it uses less memory. It unzips to 4 files that must stay in the same folder.
Each release also has `Jay-win-x64.zip`, which holds a single `Jay.exe` that runs straight after unzipping if you prefer one file; it starts a little slower. Both builds have the same features and share the same data, so you can switch at any time.

The packages include .NET. Jay selects a suitable local Java for each game version and downloads one when Mojang publishes a runtime for that platform. Linux ARM64 needs a compatible 64-bit Java installed through the system.

The Apple silicon build was fully tested on a real Mac (install, launch, play). The Intel Mac and Linux builds pass the automated builds and tests but have not been tried on real hardware, so they are marked experimental. The macOS and Linux builds are currently unsigned. If Gatekeeper blocks the first macOS launch, approve it under System Settings → Privacy & Security. Full-LAN and exit-node modes remain Windows-only; ordinary rooms, direct connections, chat and game-only forwarding work across platforms.

## Features

### Versions and loaders

- Install releases and snapshots
- One-click Fabric, Quilt, Forge (including 1.5.2 and older), NeoForge and OptiFine
- Old Forge gets its FML libraries pre-verified in the instance, and old versions never write into your own .minecraft
- Optional separate saves, mods and config folders for each version

### Accounts

- Microsoft (device code), authlib-injector and offline accounts
- Multiple accounts and skin preview
- Passwords are never stored. Login tokens use Windows DPAPI on Windows and a per-user local encryption key on macOS/Linux

### Mods and modpacks

- Search and install mods, resource packs, shaders, data packs and modpacks from Modrinth and CurseForge, with required dependencies
- Import mrpack, CurseForge, MultiMC / Prism Launcher instances, HMCL and MCBBS modpacks, as well as PCL packs bundled with a launcher, zipped .minecraft folders and packs wrapped in an extra folder; export mrpack and CurseForge
- Manage the mods in each instance and the data packs in each world (enable, disable, delete)
- Mod compatibility check: reads each mod's declared loader, game version and dependencies, flags mismatches and finds the matching version in one click
- Fabric mods on Forge / NeoForge via the open-source Sinytra Connector and Forgified Fabric API, installed automatically (not every Fabric mod works); Quilt instances use Fabric mods directly
- Connector pre-processing: convert Fabric mods ahead of time so later launches skip the wait
- Mod baking (experimental, off by default): pre-process mods with a local universal loader before launch and cache the result; currently Minecraft 1.21.1 Fabric, Quilt and vanilla only, falling back to a normal launch on failure
- CurseForge files whose authors disabled third-party distribution are never downloaded behind their back; Jay links to the official page for a manual download instead
- Fast mod scanning: a folder of a few hundred mods reads in under a second, and unchanged folders reopen in milliseconds

### Game management and crash analysis

- Per-version Java, memory, JVM arguments, window size and pre-launch / post-exit commands
- Manage worlds, screenshots, resource packs, shaders and server lists
- Find the likely cause of a crash, list suspect mods and export an error report
- Toolbox: clean up game junk, download files, install Java ahead of time

### Multiplayer

- Play together without being on the same LAN: the host opens the world to LAN, creates a room and shares an invite code; a friend pastes it and "knocks", and gets in only after the host approves on a pop-up card
- New multiplayer encryption (experimental): invite codes are single-use and expire after 10 minutes by default, and contain no room secret; every member has their own key; the host can kick, which disconnects that person at once, bans them from the room and switches the room to a new key. Codes are masked on screen, cleared from the clipboard 60 seconds after copying, and never appear in logs or diagnostic reports. Tested on one network; not yet tested between two different networks
- Built on the open-source networking tool [EasyTier](https://github.com/EasyTier/EasyTier), downloaded from its GitHub releases and verified on first use; the default mode needs no admin rights and installs no driver
- Room chat: peer-to-peer, no server in between; each member encrypts with their own key and the host fills in the sender, so members can't impersonate each other
- Direct rooms (no relay node): swap one invite / reply code (no room secret inside), then Jay tries IPv6, router port mapping (UPnP / NAT-PMP) and IPv4 hole punching (with port prediction and spraying for strict NATs); with more players, a reachable member relays; a node is used only if all of that fails and you added one. "Check my network" tells you beforehand how easy you are to reach
- Multiplayer debug mode with a diagnostic report (room secrets removed, public IPs masked by default), plus a one-click full test matrix that switches both sides through every path and mode and records connectivity, time, latency, throughput and failure reasons
- Optional full-LAN mode: everyone gets a virtual IP and games' LAN broadcasts are forwarded, so other LAN games and apps work too (needs admin rights); members can also route their internet traffic through one member's PC (exit node, opt-in on both sides; all traffic stays inside the tunnel with leak protection)
- Incognito mode (off by default): game logs don't record chat; full-LAN mode no longer writes secrets to disk
- A "how to play together" guide on the Multiplayer page, which also recognises codes pasted into the wrong box
- You add the relay nodes yourself: the [node folder](node/README.md) has one-step scripts for Linux, Docker and Windows, and a share code to send to friends

### Jay Server (experimental)

- A Minecraft server only Jay can join, on a VPS or a computer on your LAN. The `jay-server` command-line tool downloads the official server (SHA-1 checked) and a suitable Java
- The admin hands each player a single-use `JAYS1-` invite; the player pastes it into the "Jay servers" card on the Multiplayer page and joins in one click. Each device has its own identity bound to a username, so a stolen access key doesn't work on another computer
- Unknown connections get not a single byte back, so scanners can't see a Minecraft server
- Passes the automated tests but has not been tried end to end with a real Minecraft server yet. See [server/README.md](server/README.md)

### Downloads

- Parallel chunked downloads, resume and SHA1 verification
- Choose the official source or the BMCLAPI mirror, or pick automatically by time zone, with automatic fallback on failure

## Screenshots

| Download | Instance settings |
|---|---|
| ![Download page](docs/images/download.png) | ![Instance settings](docs/images/instance.png) |

![Launcher settings](docs/images/settings.png)

## Privacy

- **No telemetry:** Jay collects no usage data and creates no device ID.
- **No remote control:** no remote switch or cloud config can change what Jay does. The time zone is only used to pick the default download source.
- **Updates are up to you:** Jay doesn't check for updates unless you set an update URL, and even then it only tells you. It never downloads or replaces anything by itself.
- **Network access only for what you're doing:** signing in, fetching version lists, downloading and searching. The services involved are Microsoft and Xbox sign-in, any skin site you add, Mojang's game file servers, your chosen download source, loader download sites (OptiFine via BMCLAPI, authlib-injector via its official site or BMCLAPI), Modrinth and CurseForge.
- **Multiplayer only connects to nodes you added:** only while you host or join a room on the Multiplayer page, Jay connects to the relay nodes you added there and to the other players in the room; joining a Jay server connects only to the server named in its invite. Jay ships no nodes and fetches no node list; room codes are generated randomly on your computer and never pass through an Jay server (Jay has none). The EasyTier component is downloaded from the releases page of `github.com/EasyTier/EasyTier`. Direct rooms ask the STUN servers you set (by default `stun.miwifi.com`, `stun.chat.bilibili.com`, `stun.cloudflare.com`; editable or empty) for your external address; they only answer that and carry no game data. They may also ask your router for a temporary port mapping, removed when the session ends.

The code is open source, so you can check all of this yourself.

## Configuration

Most features need no setup. Both of these are optional:

| Item | Where | Environment variable | Notes |
|---|---|---|---|
| Microsoft Client ID | Accounts page | `AMBER_MS_CLIENT_ID` | A built-in default is used if empty. Order: UI, then environment variable, then default |
| CurseForge API key | Settings page | `AMBER_CF_KEY` | Free from [console.curseforge.com](https://console.curseforge.com). CurseForge is unavailable without one; everything else still works |

Settings are stored under the system application-data directory in `Amber/settings.json` (`%APPDATA%\Amber` on Windows, `~/Library/Application Support/Amber` on macOS, and usually `~/.config/Amber` on Linux).

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```shell
git clone https://github.com/warriorleavestar/jay-launcher.git
cd jay-launcher/src
dotnet build
dotnet test
dotnet run --project Amber.App
```

To publish a single-file build, replace the RID with `win-x64`, `osx-arm64`, `osx-x64`, `linux-x64` or `linux-arm64`:

```shell
dotnet publish Amber.App -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=none -o ../publish
```

There is also a command-line version, for example `dotnet run --project Amber.Cli -- list-remote`.

Jay is built with C#, .NET 10 and [Avalonia](https://avaloniaui.net/).

The experimental Android and iOS projects currently provide Jay Server enrollment and a localhost gateway; they cannot launch Minecraft themselves yet. See the [Android](docs/android.md) and [iOS](docs/ios.md) experiment notes for build steps and current limitations.

## FAQ

**Windows says it protected my PC. What should I do?**
Jay isn't code-signed, so SmartScreen may block it. Make sure you downloaded it from this project's Releases, then choose "More info" → "Run anyway".

**Why is the exe so large?**
It includes the .NET runtime and UI libraries, so you don't have to install anything else.

**Can I use my existing `.minecraft` folder?**
Yes. Point the game directory at it on the Settings page.

**Microsoft sign-in fails. What can I try?**
Read the error message and make sure the account owns Minecraft. You can also use your own Azure app: register a public client that allows personal accounts, has device code flow enabled and has Minecraft API access, then enter its Client ID on the Accounts page.

**Why can't I find anything on CurseForge?**
The CurseForge API needs a key; see [Configuration](#configuration). Modrinth doesn't.

**Downloads are slow. What can I do?**
Switch the download source on the Settings page and try the official source or the BMCLAPI mirror.

**Does it run on macOS or Linux?**
Yes: Intel and Apple silicon Macs, plus Linux x64 and ARM64. The Apple silicon build is verified on real hardware; Intel Mac and Linux are experimental for now. Full-LAN, exit-node and Extreme encryption modes still depend on Windows system capabilities; the other main features are cross-platform.

**What can't it do?**
Run Forge mods on Fabric / Quilt: there is no reliable compatibility layer, and Jay says so plainly.

## License

[MIT](LICENSE)

The multiplayer feature uses EasyTier, a separate open-source project (LGPL-3.0). Jay does not bundle it; it is downloaded separately the first time you use multiplayer.

---

Jay is not an official Minecraft product and is not affiliated with Mojang or Microsoft.
