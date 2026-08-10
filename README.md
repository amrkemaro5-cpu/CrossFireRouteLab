# Game Route Lab v3.0

Native C#/.NET 8 Windows diagnostic and route-research application for **any online game**. CrossFire is simply the first real-world test case.

## What is automatic now
- Detect the active game process and its public network endpoints without manual IP copying.
- Score endpoint confidence using foreground process, executable path, process name and connection evidence.
- Detect the local gateway, interface, WAN type and DNS servers.
- Fingerprint the router web interface for vendor/model/firmware when exposed without login.
- Enrich the network profile with public IP, ISP, organization and ASN when the optional read-only public-IP lookup is reachable.
- Test every discovered game endpoint with ICMP when supported, TCP evidence for TCP endpoints, and traceroute route evidence.
- Correctly treat ICMP-blocked game servers as **unknown ICMP**, not as 100% game packet loss.
- Rank candidates and explain why the current candidate scored first.
- Maintain local per-game memory under `%LOCALAPPDATA%\\GameRouteLab`.
- Extract each game's executable icon and cache a PNG for the Game Memory panel.
- Remember recent best endpoints and recent route signatures for each game.
- Save a complete diagnostic report.

## Use
1. Start an online game and enter a match/session so its real game connections exist.
2. Open **Game Route Lab**.
3. Click **AUTO ANALYZE GAME**.
4. The program detects the game, router, firmware, ISP/ASN and active endpoints itself.
5. It tests the candidates and shows the current best measured candidate.
6. Click a game in **GAME MEMORY** to review its previous observations and route signatures.

You do **not** need to copy IP addresses into the program.

## Important measurement rule
Game servers often block ICMP. A result such as `30 sent / 0 received` from `ping.exe` does not prove that the game's TCP/UDP traffic is broken. Game Route Lab therefore combines process/connection evidence, TCP tests where applicable, and route evidence instead of treating ICMP alone as the answer.

## Safety
The current application is deliberately **read-only**. It does not change Windows routes, DNS, PPPoE, router settings, firmware, firewall rules, or install/use a VPN.

The project is intended to establish reliable measurements first. Any future route-selection feature should only act on verified capabilities of the user's actual ISP/router and should never claim that a static route can force an ISP to use an arbitrary upstream path.

## Build
- Windows 10/11
- .NET 8 SDK

`dotnet restore CrossFireRouteLab.csproj`

`dotnet build CrossFireRouteLab.csproj -c Release`

The CI workflow also publishes a self-contained Windows x64 executable named `GameRouteLab.exe` and uploads a `GameRouteLab-Windows-x64` artifact.
