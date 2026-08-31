# StageBug reconstruction

This directory is the standalone source-controlled reconstruction workspace for the supplied StageBug application.

## Included

- `Program.cs` — Windows Forms entry point.
- `MainForm.cs` — reconstructed control surface for session, Boost, and room state.
- `SessionController.cs` — local session state machine and CrossFire observation integration.
- `CrossFireObserver.cs` — non-invasive Windows process/window/module observation with explicit identification and module-inspection state.
- `RoomState.cs` — local active-room state and JSON persistence.
- `StageBugDiagnostics.cs` — local diagnostic logging under `%LOCALAPPDATA%\\StageBug\\stagebug.log`.
- `FORENSIC_STATUS_2026-08-30.md` — evidence-derived reconstruction notes.

## Verified local state model

`Idle` -> `CrossFireDetected` -> `Initialized` -> `Boost1Applied` -> `Boost2Applied`.

Initialization requires a detected CrossFire process, readable executable identity, and a ready main window. CrossFire instance changes reset the local session state. Boost 2 is intentionally unavailable until Boost 1 has completed.

## Verification note

This reconstruction deliberately does not patch or modify the original `StageBug.exe`, and it does not include the old runtime-patching helper.

The supplied runtime evidence shows that the original StageBug also used a native SunnyNet/SvcData layer, multiple readiness signals (`engine_ready`, `client_game_ready`, `step1_sub_ready`), native RoomManager callbacks, and protected server-side operations. Those components are not present in this standalone reconstruction and are therefore not claimed to be reproduced.

The reconstruction does not embed recovered credentials, signing keys, encrypted authorization payloads, process-memory patching, or anti-tamper bypasses.

## Build

```powershell
dotnet restore StageBug/StageBug.csproj
dotnet build StageBug/StageBug.csproj -c Release -warnaserror
dotnet publish StageBug/StageBug.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

GitHub Actions builds the `StageBug` project for Windows x64 on pushes to `stagebug-reconstruction`.

## Verification standard

A green CI build verifies compilation and packaging only. Functional equivalence to the supplied StageBug application requires Windows-side testing against the original observable behavior and the missing native integration components.
