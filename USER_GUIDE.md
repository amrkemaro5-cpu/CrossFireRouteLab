# Game Route Lab — Simple Step-by-Step Guide

## First run

1. Download `GameRouteLab-Windows-x64.zip` from the GitHub Actions artifact.
2. Extract the ZIP to a normal folder such as `Desktop\GameRouteLab`.
3. Double-click **GameRouteLab.exe**.
4. If Windows shows SmartScreen, choose the option to view more information and run the application only if you trust the build you downloaded.
5. You do **not** need Python or .NET installed for the published EXE.

## Analyze a game automatically

1. Start the game itself, not only its launcher.
2. Sign in normally.
3. Enter an online lobby/match/session so the game creates its real network connections.
4. Keep the game running.
5. Alt-Tab to **Game Route Lab**.
6. The dashboard now performs a background game scan while it is open. You can also click **REFRESH GAMES** for an immediate scan.
7. Click **AUTO ANALYZE**.
8. Wait for the analysis to finish. Traceroute can take time for each candidate endpoint.
9. Read **BEST ENDPOINT (CURRENT)** and **ROUTE QUALITY** for the measured result.
10. The endpoint field is automatically filled from the best saved endpoint when one exists; manual entry is still supported.
11. Look at **GAME MEMORY** on the left. The game icon is taken from the game's executable when possible.
12. Run another analysis later. The program stores observations locally and keeps previous route evidence for each detected game.

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

## Game detection and future games

The scanner is not limited to one hard-coded game. It uses several signals together:

- known game names
- game executable folders such as `Games`, Steam `steamapps\common`, Riot, Garena and similar locations
- the active window/process
- a live public network socket
- the socket protocol and port
- the game's window title when available

This allows unknown/future games to be candidates without blindly treating every Internet-connected application as a game.

ChatGPT, Chrome, Edge, Firefox, Discord, Steam helper processes, launchers, Windows services and other known non-game processes are explicitly filtered out of game memory.

## If the program says "No game detected"

1. Make sure the actual game executable is running.
2. Enter an online match/session, not only the launcher.
3. Keep the game visible or make it the foreground application once.
4. Alt-Tab back to Game Route Lab.
5. Click **REFRESH GAMES**.
6. Then click **AUTO ANALYZE** again.

If the game is installed in an unusual folder and has an unusual executable name, the foreground-window + live-socket detection is used as a fallback. The scanner still excludes known non-game applications.

## If a game is detected but no game sockets are found

This usually means the game is between sessions, is using a protected/network architecture that does not expose a useful public socket at that moment, or the connection disappeared before the scan. Enter an active online match and run the scan again.

## Live analysis console

The **LIVE ANALYSIS CONSOLE** is intentionally resizable and has vertical/horizontal scrollbars. Use the scrollbar or mouse wheel to inspect older scan output, route-table data, traceroute output and diagnostic messages.

## Understanding ping results

A game server can block ICMP. Therefore:

`30 sent / 0 received`

does **not** automatically mean the game server has 100% game packet loss.

Game Route Lab treats ICMP-only failure as **unknown/blocked** and combines process evidence with route evidence instead of declaring the endpoint bad from ping alone.

## Manual tools

- **PING 30x** — 30 ICMP probes to the selected endpoint.
- **TRACEROUTE** — Windows `tracert` route discovery.
- **PATH QUALITY** — route-quality measurement using the selected endpoint.
- **ROUTE TABLE** — current Windows route table.
- **FIND CONNECTIONS** — current game connection candidates.
- **NETWORK** — local/ISP profile.
- **ROUTER** — gateway/router fingerprint.
- **SAVE REPORT** — saves the console and current result to a text report.

AUTO ANALYZE is the recommended path because it discovers game endpoints automatically and ranks the measured candidates.

## Where game memory is stored

```text
%LOCALAPPDATA%\GameRouteLab
```

The program stores profiles and cached game icons there. It does not store your router password.

## Important limitation: route optimization

This application remains **read-only** in this build. It measures and ranks routes; it does not force your ISP to use an arbitrary upstream route.

A future optimization mode should be treated separately from measurement mode and should require explicit user confirmation, show exactly what Windows setting would change, create a rollback point, and verify the result. The current analyzer never silently changes DNS, PPPoE, Windows routes, router settings or firmware.
