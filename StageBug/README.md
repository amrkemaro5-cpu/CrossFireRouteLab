# StageBug reconstruction

This directory is the source-controlled reconstruction workspace for the user's StageBug application.

## Scope

The current target is intentionally limited to the three controls the user requested:

1. `INITIALIZE SESSION`
2. `TRIGGER BOOST 1`
3. `TRIGGER BOOST 2`

`CREATE A ROOM` is out of scope for this iteration.

The original executable is kept outside this source tree. The prior investigation established evidence of a native Windows UI, SunnyNet integration, CrossFire process/session integration, and a native `sos::network::RoomManager`.

## Current implementation

- Native Windows Forms shell targeting Windows x64.
- CrossFire process detection using the Windows process API.
- Explicit local session state: `Idle` -> `CrossFireDetected` -> `Initialized`.
- `INITIALIZE SESSION` validates that CrossFire is running and records the detected PID locally.
- Boost buttons require an initialized local session and re-check that CrossFire is still running.
- No recovered license keys, credentials, encrypted authorization payloads, or process-injection primitives are embedded.
- The reconstructed boost controls do not claim to reproduce protected server-side effects that have not been independently recovered and authorized.

## Build

```powershell
dotnet restore StageBug/StageBug.csproj
dotnet build StageBug/StageBug.csproj -c Release -warnaserror
dotnet publish StageBug/StageBug.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

GitHub Actions builds a Windows x64 artifact on pushes to `stagebug-reconstruction` and runs source-safety checks before publishing.

## Verification standard

A build is considered technically verified only when the Windows CI job completes successfully. Functional equivalence to the original StageBug still requires Windows-side testing against the original application's observable behavior.
