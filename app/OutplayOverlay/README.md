# Outplay Overlay (v0 — Windows)

First build slice per the PRD (§5.6/§13): telemetry ingestion for iRacing + F1 25,
rendered on a live always-on-top overlay. No scoring, coaching, or voice yet —
this proves the ingestion + overlay loop end-to-end.

## Requirements (Windows only)

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- iRacing installed (for the iRacing path) and/or F1 25 (for the UDP path)

## Build & run

```powershell
cd app\OutplayOverlay
dotnet restore
dotnet build
dotnet run
```

A small always-on-top panel appears in the top-left of the screen showing
speed, throttle, brake, steering, and gear. Drag it by clicking anywhere on it.

## Enabling telemetry in each sim

**iRacing**: no setup needed — the app reads shared memory automatically
whenever iRacing is running a session.

**F1 25**: in-game, go to *Settings > Telemetry Settings* and set
"UDP Telemetry" to On, port `20777`, "UDP Format" to **2024** (F1 25 defaults
to a new 2025 format this parser does not understand — see Known Gaps below),
and "UDP Send Rate" as high as your machine can push (60Hz recommended).

Also run F1 25 in **Borderless** or **Windowed** display mode, not exclusive
Fullscreen — exclusive fullscreen bypasses the Windows compositor entirely and
no overlay window from any app can render on top of it.

## Known gaps in this build (tracked in the PRD, §14)

- `IRSDKSharper` API surface (`GetFloat`/`GetInt` calls in
  `Telemetry/IRacingTelemetrySource.cs`) needs to be checked against whatever
  version restores from NuGet — wrapper APIs shift between releases and this
  was written without the SDK installed locally.
- F1 25 UDP packet layout (`Telemetry/F125TelemetrySource.cs`) is based on the
  F1 23/24 community-documented format. F1 25 ships a *new* 2025 wire format by
  default that this parser does not implement — you must set in-game "UDP
  Format" to 2024 for this build to work. A future slice should add a proper
  2025-format parser (per the EA UDP spec) instead of relying on the legacy
  compatibility mode. Only the Car Telemetry packet (id 6) is parsed; fuel,
  tire wear, and lap delta need the Car Status / Car Damage / Lap Data packets
  (follow-up work).
- No corner scoring, no voice coaching, no persistence yet — this is the
  ingestion + overlay slice only (PRD §5.1 area, not yet §5.2/§5.3).
- Not signed/packaged as an installer — run via `dotnet run` or
  `dotnet publish` for now.

## Next slice (per PRD §5.2–§5.4)

1. Persist samples to Postgres per the `TelemetrySample` schema (§8.1).
2. Per-track corner segmentation (§8.4 — manual boundaries first).
3. Delta-to-PB scoring per corner, surfaced on the overlay.
4. Post-session debrief + brake-point audio cue (pulled into v1 per §11).
