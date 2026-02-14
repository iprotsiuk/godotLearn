# Netcode Strategy

## Overview

This Phase 1 vertical slice proves that we can keep movement responsive while preserving server authority.

What is proven:

- Local movement is immediate through client prediction (`NetSession.Client.cs` + `PlayerMotor.cs`).
- Server is authoritative for physics state (`NetSession.Server.cs`).
- Local state converges using sequence-based reconciliation (`ReconcileLocal` in `NetSession.Client.cs`).
- Remote players are smoothed by interpolation with clamped extrapolation (`RemoteSnapshotBuffer.cs` + `UpdateRemoteInterpolation` in `NetSession.Client.cs`).
- Listen server host runs the same netcode pipeline as standalone clients (loopback), not a separate control path (`NetSession.cs`, `NetSession.Client.cs`, `NetSession.Server.cs`).

## Terminology

- Tick: discrete simulation step at fixed rate (60 Hz). One tick is `1/60 s`.
- Seq: monotonically increasing input sequence number (`InputCommand.Seq`, `uint`).
- Snapshot: authoritative state sample sent by server (`PlayerStateSnapshot`).
- Ack: `lastProcessedSeqForThatClient` inside each snapshot entry for that peer.
- Interp delay: render offset behind estimated server time (`InterpolationDelayMs`, default 100 ms).
- Sim state vs render state:
  - Sim state = actual `CharacterBody3D` state used for physics/reconciliation.
  - Render state = visual offset smoothing (`PlayerCharacter._renderOffset`) applied in `_Process`.

## Data Flow Diagrams

### Local Client Pipeline

```text
Input events (_UnhandledInput / InputMap)
  -> BuildInputCommand (seq, axes, yaw/pitch, jump edge)
  -> InputSanitizer.SanitizeClient
  -> Predict locally: PlayerMotor.Simulate on local PlayerCharacter
  -> Store in InputHistoryBuffer (pending unacked)
  -> Encode CH0 bundle via NetCodec.WriteInputBundle (latest + last 2)
  -> Send to server (or loopback call on host)

Server sim processes input, then sends snapshot
  -> HandleSnapshot
  -> Find local player's PlayerStateSnapshot
  -> ReconcileLocal:
       apply authoritative pos/vel
       drop acked seqs
       replay remaining pending inputs
       compute correction magnitude
       snap or render-smooth correction
```

### Remote Entity Pipeline

```text
Incoming snapshot (CH1)
  -> For each non-local peer: append RemoteSample into RemoteSnapshotBuffer

Each render frame (_Process)
  -> renderTime = estimatedServerTime - interpolationDelay
  -> TrySample(renderTime):
       interpolate between surrounding snapshots
       else extrapolate with velocity (clamped by MaxExtrapolationMs)
  -> Apply sampled pos/vel/yaw/pitch to remote PlayerCharacter
```

### Listen Server Loopback

```text
Host process runs BOTH logical roles:

Local predicted player (visible, camera owner)
  + hidden authoritative server player (no camera, not rendered)

TickClient:
  local input -> predict local -> encode bundle
  -> loopback into HandleInputBundle(fromPeer = localPeer)

TickServer:
  consumes strict sequential input
  simulates hidden authoritative player
  writes snapshot
  -> loopback into HandleSnapshot for local reconciliation
```

## Message Specs

Source: `NetModels.cs`, `NetCodec.cs`, `NetCodec.Control.cs`, `InputSanitizer.cs`.

### InputCommand (Client -> Server)

Fields:

- `seq` (`uint`): input sequence number, starts at 1, strictly increasing.
- `clientTick` (`uint`): client-side simulation tick index.
- `dtFixed` (`float`, seconds): fixed step; server overwrites with authoritative value.
- `moveAxes` (`Vector2`, unitless): intended movement axes.
- `buttons` (`InputButtons` bitmask): `JumpPressed`, `JumpHeld`.
- `yaw` (`float`, radians): horizontal look/heading.
- `pitch` (`float`, radians): vertical look.

Server-side clamps/validation (`InputSanitizer.TrySanitizeServer`):

- Reject if `seq == 0`.
- Reject if any axis/yaw/pitch is NaN or infinity.
- Clamp `moveAxes` each component to `[-1, 1]`; normalize if magnitude > 1.
- Mask buttons to allowed bits.
- Wrap yaw to `[-pi, pi]`.
- Clamp pitch to `[-PitchClampDegrees, +PitchClampDegrees]` converted to radians.
- Force `dtFixed = 1 / ServerTickRate`.

### PlayerStateSnapshot (Server -> Client)

Fields per entity:

- `peerId` (`int`): target entity identity.
- `lastProcessedSeqForThatClient` (`uint`): server ack for that peer’s input stream.
- `pos` (`Vector3`, meters, world space).
- `vel` (`Vector3`, meters/second).
- `yaw` (`float`, radians, authoritative).
- `pitch` (`float`, radians, authoritative and already clamped server-side).
- `grounded` (`bool`).

Ack meaning:

- Local client removes pending commands `<= lastProcessedSeqForThatClient`.
- Remaining pending commands are replayed on top of authoritative state.

### Control / Handshake Payload

Packet type `ControlType.Welcome` includes server-authoritative overrides:

- assigned peer id
- server tick rate
- client tick rate
- snapshot rate
- interpolation delay (ms)
- max extrapolation (ms)
- reconciliation smooth window (ms)
- reconciliation snap threshold (meters)
- pitch clamp (degrees)

Client applies these in `NetSession.Control.cs` and rebuilds `NetClock` from server tick rate.

## Channel Strategy

Defined in `NetConstants.cs`.

- CH0 (`NetChannels.Input`): `Unreliable`, high-frequency input bundles.
  - Reason: late input is usually worse than dropped input.
  - Reliability support: redundancy of latest + previous 2 commands per packet.
- CH1 (`NetChannels.Snapshot`): `UnreliableOrdered` snapshots.
  - Reason: do not block on old snapshots; prefer newest ordered state stream.
- CH2 (`NetChannels.Control`): `Reliable` handshake/ping/pong.
  - Reason: control messages must arrive and remain coherent.

## Host-Local Dual Representation Rules

Implemented in `StartListenServer` (`NetSession.cs`) and `CreateCharacter` (`NetSession.Shared.cs`).

- Rendered local node:
  - created with camera (`CreateCharacter(localPeerId, true)`)
  - used for local prediction and final rendered movement.
- Authoritative host server node:
  - created hidden (`CreateCharacter(localPeerId, false, false)`)
  - advanced only by server simulation.
- Collision policy (`PlayerCharacter.cs`):
  - player `CollisionLayer = 2`
  - player `CollisionMask = 1`
  - world static geometry is layer 1.
  - Result: players collide with world but not with each other or with their own duplicate representation.

DO NOT:

- Do not attach camera to authoritative server node.
- Do not render hidden authoritative node for host-local player.
- Do not drive local visible node directly from server node transform every tick.
- Do not bypass `ReconcileLocal` for host-local movement.

## Server Input Policy

Implemented in `TickServer` (`NetSession.Server.cs`).

- Strict sequential consumption: one expected sequence per server tick after stream start.
- Startup alignment: first valid command initializes stream at first received seq.
- Missing expected seq behavior: reuse last known input for that tick.
  - `JumpPressed` is cleared when reusing to avoid repeated edge-trigger jumps.
- Gap-skip fast-forward is forbidden.
  - Reason: it creates non-deterministic jumps in authority timeline and destabilizes reconciliation.

## Reconciliation + Smoothing Policy

Implemented in `ReconcileLocal` (`NetSession.Client.cs`) + `PlayerCharacter._Process`.

- Compute correction magnitude: distance between pre-reconcile predicted position and post-replay position.
- If correction > `ReconciliationSnapThreshold` (meters, default `1.5`): immediate snap (clear render offset).
- Else: apply render-only offset and decay over `ReconciliationSmoothMs` (default `100 ms`).
- Render smoothing function is exponential decay in `PlayerCharacter._Process`.

Interpreting metrics (`SessionMetrics`, shown by `DebugOverlay.cs`):

- Normal: small occasional corrections under threshold, stable pending input count.
- Suspicious: sustained high corrections, pending input count only grows, ack stalls.
- Bug-level: repeated snap-threshold breaches with no convergence.

## Time Sync Approach

Implemented in `NetClock.cs` and used in `UpdateRemoteInterpolation`.

- Estimate server time as: `localTime + offset`.
- Observe server tick from snapshots/pongs.
- Convert tick to server seconds via authoritative server tick interval.
- Add one-way estimate (`RTT / 2`) and derive offset sample.
- Slew offset using blend factor `0.1` (no hard jumps).

Why no hard jumps:

- Hard jumps shift `renderTime` discontinuously, causing remote interpolation jitter and apparent warps.

## Network Simulation

Implemented by `NetworkSimulator.cs`; configured from `CliArgs.cs`, `Main.cs`, and live via `DebugOverlay.cs`.

Behavior:

- Applied on send path only (`SendPacket` -> `EnqueueSend`).
- Random drop by `lossPercent`.
- Delay = `latencyMs + uniform[-jitterMs, +jitterMs]`.
- Packet copied and queued until `Flush` dispatch time.

Reproduce acceptance criteria B (target ~120 ms RTT, ~20 ms jitter, ~1% loss):

1. Start host with net sim split one-way budget:
   - `SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_host.sh 7777`
2. Start client with same split:
   - `SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_client.sh 127.0.0.1 7777 1`
3. In `F1` debug panel, verify measured RTT/jitter are near target and loss is active.
4. Run jump/movement loops for 2-3 minutes and watch convergence metrics.

## Known Limitations

- No dedicated server entry scene yet (`--dedicated` not implemented in Phase 1).
- No lag compensation/rewind.
- No adaptive interpolation delay.
- No advanced anti-cheat beyond input sanity checks.
- No quantization/compression tuning beyond fixed binary structs.
- No authoritative gameplay systems beyond movement (weapons, inventory, AI, scoring all out of scope).
