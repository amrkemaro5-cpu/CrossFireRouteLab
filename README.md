# CrossFire Route Lab v1.0

Native C#/.NET 8 Windows diagnostic application for measuring the current CrossFire network path.

## Requirements
- Windows 10/11
- .NET 8 SDK

## Build
`dotnet restore`

`dotnet build -c Release`

The executable will be under `bin/Release/net8.0-windows/`.

## Use
1. Start CrossFire and enter an actual match.
2. Click **Find Connections**.
3. Verify the discovered endpoint.
4. Run **Ping 30x**, **Traceroute**, and **Path Quality**.
5. Run **Network Snapshot**.
6. Save the report.

## Safety
This v1.0 build is deliberately read-only. It does not change Windows routes, DNS, PPPoE, TD-W9960 settings, firmware, or install a VPN.

The next stage should compare verified live endpoints, latency, jitter, packet loss, hop changes, and (where technically possible) different WE sessions before any route-changing feature is enabled.
