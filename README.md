# Game Route Lab v4.0

![Game Route Lab](https://raw.githubusercontent.com/amrkemaro5-cpu/CrossFireRouteLab/main/assets/GameRouteLab.svg)

A native Windows diagnostic and route-research application for **any online game**. CrossFire is one test case, not a hard-coded limitation.

## What changed in v4

- New dark neon gaming dashboard inspired by the GRL design.
- GRL logo and application branding.
- Windows EXE icon generated automatically during CI.
- Automatic game fingerprinting using process name, executable path, window title, foreground process and connection evidence.
- Explicit protection against false selection of ChatGPT, Chrome, Edge, Firefox, Discord, Steam helpers and Windows services.
- Automatic per-game memory with cached game icons.
- Previous best endpoint, score and route evidence are retained locally.
- Automatic ISP / organization / ASN / public-IP profiling when available.
- Automatic router/vendor/model/firmware fingerprinting when exposed by the gateway.
- Automatic endpoint testing with ping and traceroute evidence.
- ICMP-blocked servers are treated as **unknown**, not as automatic game packet loss.
- Read-only operation: no Windows route, DNS, PPPoE, router, firmware or VPN settings are modified.

## Quick start

1. Start your game.
2. Enter an online match/session.
3. Open **GameRouteLab.exe**.
4. Click **AUTO ANALYZE**.
5. Wait for the candidate comparison to finish.
6. Read **CURRENT RESULT** and check the game's icon/profile in **GAME MEMORY**.

You no longer need to copy IP addresses manually.

## Detailed instructions

See **USER_GUIDE.md** in the package. It explains the steps one by one and explains ICMP timeouts, pathping, game detection and memory storage.

## Game memory

Profiles are stored locally at:

```text
%LOCALAPPDATA%\GameRouteLab
```

Each profile can remember the executable, friendly name, icon, number of analyses, previous best endpoint, score and recent route evidence. No router password is stored.

## Measurement philosophy

A game endpoint can block ICMP while still carrying valid game traffic. Therefore the application does not equate `ping = 100% loss` with `game = broken`. It combines process identity, socket evidence and route measurements.

The application is intentionally read-only. A local static route cannot force WE/Telecom Egypt to change its upstream Internet routing. The first objective is to establish reliable measurements from the actual connection before considering any route-selection mechanism.

## Build

- Windows 10/11
- .NET 8 SDK
- Python 3 + Pillow are used **only by GitHub Actions** to generate the EXE icon.

```text
dotnet restore CrossFireRouteLab.csproj
dotnet build CrossFireRouteLab.csproj -c Release
```

The GitHub workflow publishes a self-contained Windows x64 `GameRouteLab.exe`, includes the GRL icon and packages `README.md`, `USER_GUIDE.md` and `GameRouteLab.ico`.
