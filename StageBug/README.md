# StageBug reconstruction

This directory is the source-controlled reconstruction workspace for the user's StageBug application.

## Current status

The original StageBug executable is a separate artifact and is not committed here. The recovered investigation established a native Windows application with an ImGui/DX11-era UI, SunnyNet integration, CrossFire process/session integration, and a native `sos::network::RoomManager` containing `CheckAndRestoreActiveRoom`, `CreateRoom`, and `JoinRoom`.

The current source is an initial native Windows UI scaffold. It deliberately does not implement or reproduce protected backend authorization, encrypted session material, credential replay, or server-side access-control bypasses.

## Build

```powershell
dotnet build StageBug/StageBug.csproj -c Release
dotnet publish StageBug/StageBug.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

GitHub Actions also builds a Windows x64 artifact from the `stagebug-reconstruction` branch.

## Reconstruction priorities

1. Match the recovered native UI/state model.
2. Reconstruct legitimate local/session behavior from the available evidence.
3. Keep the original executable untouched.
4. Do not embed recovered license keys, credentials, or protected authorization material.
5. Validate each behavior on Windows before calling it equivalent to the original.
