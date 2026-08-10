# Game Route Lab — Simple Step-by-Step Guide

## First run

1. Download `GameRouteLab-Windows-x64.zip` from the GitHub Actions artifact.
2. Extract the ZIP to a normal folder such as `Desktop\GameRouteLab`.
3. Double-click **GameRouteLab.exe**.
4. If Windows shows SmartScreen, choose the option to view more information and run the application only if you trust the build you downloaded.
5. You do **not** need Python or .NET installed for the published EXE.

## Analyze a game automatically

1. Start your game.
2. Sign in normally.
3. Enter an online lobby/match/session so the game creates its real network connections.
4. Keep the game running.
5. Alt-Tab to **Game Route Lab**.
6. Click **AUTO ANALYZE**.
7. Wait for the analysis to finish. It may take a few minutes because traceroute is performed for candidate endpoints.
8. Read **CURRENT RESULT** for the best measured candidate.
9. Look at **GAME MEMORY** on the left. The game icon is taken from the game's executable when possible.
10. Run another analysis later. The program stores observations locally and keeps the game's previous best endpoint and route evidence.

## What the program detects by itself

- Active game process and PID
- Game executable path and friendly name
- Game icon
- Public game endpoints and ports
- Local IP and gateway
- ISP / organization / ASN / public IP when available
- Router vendor/model/firmware when the gateway exposes enough information
- Ping evidence
- Traceroute evidence
- Previous observations for each game

You do **not** need to copy an IP from `netstat` anymore.

## If the program says "No game detected"

1. Make sure the game is actually running.
2. Enter an online match/session, not only the launcher.
3. Alt-Tab back to Game Route Lab.
4. Click **REFRESH GAMES**.
5. Then click **AUTO ANALYZE** again.

The scanner intentionally refuses to guess common applications such as ChatGPT, Chrome, Edge, Firefox, Discord, Steam helpers and Windows services as games. This is better than selecting the wrong process and building bad route data.

## If a game is detected but no game sockets are found

This usually means the game is between sessions, is using a protected/network architecture that does not expose a useful public socket at that moment, or the connection disappeared before the scan. Enter an active online match and run the scan again.

## Understanding ping results

A game server can block ICMP. Therefore:

`30 sent / 0 received`

does **not** automatically mean the game server has 100% game packet loss.

Game Route Lab treats ICMP-only failure as **unknown/blocked** and combines process evidence with route evidence instead of declaring the endpoint bad from ping alone.

## Manual tools

- **PING 30x** — 30 ICMP probes to the endpoint in the box.
- **TRACEROUTE** — Windows `tracert` route discovery.
- **PATH QUALITY** — Windows `pathping`; this can take several minutes.
- **CONNECTIONS** — current `netstat -ano` snapshot.
- **ROUTE TABLE** — current Windows route table.
- **NETWORK** — local/ISP profile.
- **ROUTER** — gateway/router fingerprint.
- **SAVE REPORT** — saves the console to a text report.

AUTO ANALYZE is the recommended path because it chooses the game endpoints automatically.

## Where game memory is stored

```text
%LOCALAPPDATA%\GameRouteLab
```

The program stores profiles and cached game icons there. It does not store your router password.

## Important limitation

This application is currently **read-only**. It measures and ranks routes; it does not force your ISP to use an arbitrary upstream route. A Windows static route cannot command WE/Telecom Egypt to change its upstream Internet routing.

The goal is to collect reliable measurements first. Only after enough evidence is available should any route-selection idea be considered.
