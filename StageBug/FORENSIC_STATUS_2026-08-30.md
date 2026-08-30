# StageBug forensic status — 2026-08-30

This document records evidence-derived findings from the original `StageBug.exe`, `EpicWebHelper.DMP`, and the prior investigation. It intentionally separates confirmed evidence from hypotheses.

## Confirmed

- Original StageBug binary is PE32+ x64 and is structurally packed; on-disk sections include `.UPX0`, `.UPX1`, `.UPX2`.
- The captured runtime image in `EpicWebHelper.DMP` has the same x64 application image size as the supplied StageBug binary.
- The dump contains a full runtime image of the main application and a full `SvcData.dll` image.
- `SvcData.dll` contains SunnyNet c-shared exports including `CreateSunnyNet`, `SunnyNetSetPort`, `SunnyNetSetCallback`, `SunnyNetStart`, `OpenDrive`, and process-routing functions.
- Embedded Go metadata in `SvcData.dll` identifies the SunnyNet source family and revision used by the captured runtime.
- StageBug's recovered initialization sequence resolves SunnyNet functions dynamically and configures a local port of 2025 before starting the network/process interception layer.
- CrossFire process handling is integrated with the SunnyNet process-driver layer rather than being only a periodic process-existence poll.
- Native RTTI identifies `sos::network::RoomManager` and methods including `CheckAndRestoreActiveRoom`, `CreateRoom`, and `JoinRoom`.
- The runtime contains state/payload vocabulary for `engine_ready`, `client_game_ready`, `step1_sub_ready`, `boost_seq`, `boost1_applied`, `boost2_applied`, `last_boost_type`, `host_boost_sync`, `host_ip`, `host_port`, `payload_boost_1`, and `payload_boost_2`.
- The runtime contains UI/state strings for `INITIALIZE SESSION`, `TRIGGER BOOST 1`, `TRIGGER BOOST 2`, and the stage-lock messages.
- The authentication window/controller is associated with `##AuthWin` and license UI state such as `enter_license_key`, `savedLicenseKey`, and `activate_now`.
- The startup path calls a native authentication-window/controller routine and tests its Boolean result before continuing.

## Strong hypotheses requiring runtime corroboration

- The exact popup reported by the user (`File corrupted! This program has been manipulated...`) is consistent with VMProtect-style memory integrity protection. VMProtect documentation explicitly describes pre-entry-point image integrity checks and the same family of corruption/tamper messages.
- The `.UPX*` section naming may not prove ordinary UPX alone; VMProtect documentation allows protected segments to be renamed to names such as `.UPX*`, and the binary also contains several literal `VMP` markers. This is suggestive but not by itself conclusive proof of VMProtect.
- The main application object appears to contain an active-state byte at `+0x88`, while the authentication controller object passed through the application's `+0x80` field returns its own `+0x08` byte. These are distinct object offsets and should not be conflated.

## Remaining unknowns

- Exact anti-tamper/protection implementation and exact point in startup where it runs.
- Full authentication-state transition semantics.
- Exact event-by-event path from CrossFire/SunnyNet callbacks to `Session Initialized!`.
- Exact Boost 1 and Boost 2 handler semantics and result paths.
- Complete `RoomManager` object layout and method bodies.
- Protected backend authorization semantics are intentionally not reconstructed into a replay/forgery mechanism.

## Important correction

Earlier work treated `[object+0x08]` as the application's global activation flag. That was not sufficiently supported. The recovered startup/controller relationship shows the relevant success-state write is associated with a different object and offset. Any future analysis should rely on cross-referenced object ownership and callers, not generic byte-store patterns.
