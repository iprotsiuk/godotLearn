# Architecture

This file is the high-level map. The practical, implementation-level networking spec is `Docs/NETCODE_STRATEGY.md`.

## Runtime Topology

- Listen server mode (Phase 1): host process runs authoritative server simulation and a local client prediction/reconciliation path via loopback packets.
- Client mode: standalone client connects by direct IP/port.
- Transport: `ENetMultiplayerPeer` with `channel_count = 3`.

## Tick Model

- Physics simulation tick: 60 Hz (`project.godot` -> `physics/common/physics_ticks_per_second=60`).
- Server simulation: `NetSession._PhysicsProcess`.
- Client prediction/reconciliation: `NetSession._PhysicsProcess`.
- Remote interpolation and render smoothing: `NetSession._Process` / `PlayerCharacter._Process`.

## Message Protocol (Binary, Explicit)

Common: first byte is `PacketType`.

### Client -> Server (`PacketType.InputBundle`, CH0, unreliable)

- Bundle contains 1..3 `InputCommand` entries.
- Each `InputCommand`:
  - `seq` (`uint`)
  - `clientTick` (`uint`)
  - `dtFixed` (`float`)
  - `moveAxes` (`Vector2` as 2 floats)
  - `buttons` (`byte` bitmask)
  - `yaw` (`float`)
  - `pitch` (`float`)
- Redundancy: latest + up to 2 prior commands.

### Server -> Client (`PacketType.Snapshot`, CH1, unreliable-ordered)

- Header:
  - `serverTick` (`uint`)
  - `stateCount` (`byte`)
- Per player state:
  - `peerId` (`int`)
  - `lastProcessedSeqForThatClient` (`uint`)
  - `pos` (`Vector3`)
  - `vel` (`Vector3`)
  - `yaw` (`float`)
  - `pitch` (`float`)
  - `grounded` (`byte`)
  - `locomotion` packed fields (`mode`, wall normal XZ, wallrun/slide timers)
  - See `Docs/NETCODE_STRATEGY.md` for full binary field-level spec.

### Control/Handshake (`PacketType.Control`, CH2, reliable)

- `Hello`: protocol version.
- `Welcome`: protocol version + assigned peer id + server-authoritative network/sim config payload (tick/snapshot/interp/extrapolation/reconcile/pitch clamp).
- `Ping`/`Pong`: RTT and jitter estimation support, plus server tick sampling.

## Server Authority

- Server owns authoritative `CharacterBody3D` state for each connected peer.
- Inputs are consumed exactly one sequential command per tick after stream start.
- If expected input is missing, server reuses last input for that tick.
- Server broadcasts snapshots at 20 Hz by default.

## Client Prediction + Reconciliation

- Client simulates local movement immediately using same motor logic as server.
- Client stores sent inputs in a circular history keyed by sequence.
- Listen host local player uses the same prediction/reconciliation pipeline (no host-authoritative local bypass).
- On snapshot for local player:
  - apply authoritative pos/vel + locomotion state + grounded override,
  - drop acked commands,
  - replay remaining unacked commands.
- Correction policy:
  - if error > `ReconciliationSnapThreshold` (default 1.5m): snap.
  - else apply render-only correction decay (`ReconciliationSmoothMs`, default 100ms).

## Remote Interpolation

- Per remote peer: time-ordered snapshot buffer.
- Render time = estimated server time - interpolation delay (default 100ms).
- Position lerp + yaw lerp-angle + pitch lerp.
- Limited extrapolation when future snapshot missing, clamped by `MaxExtrapolationMs`.

## Time Sync (Basic)

- `NetClock` tracks server-vs-local offset estimate.
- Uses periodic snapshot server ticks and RTT-derived one-way estimate.
- Offset slews gradually (no hard jumps) to keep interpolation stable.

## Deterministic Network Repro

- `NetworkSimulator` can inject packet delay/jitter/loss in-engine.
- Controlled by CLI args and fixed seed for reproducible runs.

## Runtime Safety Checks

- Server-side sanity on incoming inputs: finite checks, move axis clamping/normalization, yaw wrap, pitch clamp.
- Client-side safety clamping for the same fields before sending.

## Extensibility

- `IGameMode` interface isolates rules from net movement core.
- `FreeRunMode` implemented for Phase 1.
- `RaceModeStub` / `TagModeStub` included as extension placeholders.
