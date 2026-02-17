# Plan: Locomotion State in Authoritative Snapshot

## 1) Problem Statement

Current reconciliation resets locomotion to `CreateInitial(snapshot.Grounded)` in `ReconcileLocal(...)` via `ResetLocomotionFromAuthoritative(...)`.

That behavior drops authoritative locomotion internals each snapshot:
- `Mode`
- `ModeTicks`
- `WallNormal`
- `WallRunTicksRemaining`
- `SlideTicksRemaining`

For future parkour (wallrun/walljump/slide), these fields control branch decisions and timers. If they are wiped on every reconcile, replay runs with a different state than the server timeline. Result: deterministic divergence under prediction + reconciliation (mode flaps, wrong wall side, timer mismatch, repeated correction churn).

## 2) Proposed Packed Wire Format (6 bytes/player)

Add a packed locomotion payload to each `PlayerStateSnapshot` entry.

### Byte layout (little-endian byte stream)

- `LocoMeta` (1 byte)
  - bits `0..2`: `Mode` (`0..4` currently, `0..7` reserved)
  - bit `3`: `WallNormalValid` (`1` if wall normal is meaningful)
  - bits `4..7`: reserved (`0` for now)
- `ModeTicksQ` (1 byte)
  - `ModeTicks` saturated to `0..255`
- `WallRunTicksQ` (1 byte)
  - `WallRunTicksRemaining` saturated to `0..255`
- `SlideTicksQ` (1 byte)
  - `SlideTicksRemaining` saturated to `0..255`
- `WallOctX` (1 byte)
- `WallOctY` (1 byte)

Total added: **6 bytes**.

### Quantization rules

- Counter quantization:
  - `Q = clamp(value, 0, 255)`
  - decode as `int(Q)`
- Wall normal quantization (octahedral, signed 8-bit per axis):
  - If `WallNormalValid == 0`, encode `(WallOctX, WallOctY) = (127, 127)` and decode to `Vector3.Zero`.
  - Else:
    1. Normalize `n`.
    2. Project to octahedron:
       - `p = n / (abs(n.x) + abs(n.y) + abs(n.z))`
       - if `p.z < 0`: fold (`p.x`, `p.y`) with oct-wrap transform.
    3. Quantize:
       - `qx = round(clamp(p.x, -1, 1) * 127)`
       - `qy = round(clamp(p.y, -1, 1) * 127)`
       - `WallOctX = byte(qx + 127)`, `WallOctY = byte(qy + 127)`
  - Decode:
    1. `fx = (WallOctX - 127) / 127.0`, `fy = (WallOctY - 127) / 127.0`
    2. Reconstruct `z = 1 - abs(fx) - abs(fy)`; if `z < 0`, inverse-fold `fx/fy`.
    3. Normalize reconstructed vector.

Notes:
- This preserves directional wall-side data for deterministic wallrun/walljump branching.
- 8-bit counters are enough for active parkour windows; saturation is deterministic and explicit.

## 3) Size Update and Packet Arithmetic

Current constants:
- `SnapshotStateBytes = 81`
- `SnapshotPacketBytes = 6 + SnapshotStateBytes * MaxPlayers`
- `MaxPlayers = 16`

With packed locomotion (+6 bytes/state):
- `SnapshotStateBytes = 81 + 6 = 87`
- `SnapshotPacketBytes = 6 + (87 * 16) = 6 + 1392 = 1398`

Delta vs current packet:
- old: `6 + 81*16 = 1302`
- new: `1398`
- increase: `+96` bytes total packet

Why packed instead of naive locomotion serialization:
- Naive `int/int/int + Vector3` style additions are ~`+25..28` bytes/player.
- That would push packet to ~`1702..1750` bytes for 16 players before network headers, exceeding practical safe MTU margins.

## 4) Exact Code Touchpoints and Edit List

### A) Snapshot model and constants

- `Scripts/Net/NetModels.cs`
  - Extend `PlayerStateSnapshot` with 6 packed locomotion bytes (or equivalent packed fields).
- `Scripts/Net/NetConstants.cs`
  - Bump `ProtocolVersion`.
  - Update `SnapshotStateBytes` from `81` to `87`.
  - `SnapshotPacketBytes` formula remains unchanged but resolves to new total.

### B) Snapshot codec

- `Scripts/Net/NetCodec.cs`
  - `WriteState(...)`: append 6 locomotion bytes in fixed order.
  - `ReadState(...)`: read the same 6 bytes in the same order.
  - Keep strict fixed-size packet behavior.

### C) Server snapshot production (authoritative source)

- `Scripts/Net/NetSession.Server.cs`
  - `BuildSnapshotStates(...)`: populate packed locomotion fields from authoritative `PlayerCharacter` locomotion state (`GetLocomotionState()`), including quantization.

### D) Client reconciliation consume point

- `Scripts/Net/NetSession.Client.cs`
  - `ReconcileLocal(...)`: replace locomotion reset path with authoritative locomotion restore from packed snapshot data, then replay unacked inputs.
  - Keep `SetGroundedOverride(snapshot.Grounded)` for floor-contact continuity.

### E) Locomotion encode/decode helper placement

- Add a small helper (recommended) for pack/unpack + oct encode/decode to avoid duplicated bit math.
  - Preferred location: `Scripts/Player/Locomotion/` (domain-owned logic), callable from net/session code.
  - Alternative: `Scripts/Net/` helper if team wants all wire transforms under net layer.

## 5) Rollout Stages (each stage compiles) + Acceptance Checks

### Stage 1: Introduce packing primitives (no protocol changes yet)

Changes:
- Add helper APIs for:
  - `LocomotionState -> packed(6 bytes)`
  - `packed(6 bytes) -> LocomotionState`
  - oct encode/decode + counter saturation.

Acceptance checks:
- `dotnet build NetRunnerSlice.csproj` passes.
- Local debug assertion/log check confirms pack->unpack mode and timer values are stable for representative values.

### Stage 2: Wire snapshot schema and codec + protocol bump

Changes:
- Add packed fields to `PlayerStateSnapshot`.
- Update `NetConstants.SnapshotStateBytes` and `ProtocolVersion`.
- Update `WriteState(...)` / `ReadState(...)` to include 6 bytes.

Acceptance checks:
- `dotnet build NetRunnerSlice.csproj` passes.
- Host/client with mixed versions are rejected at hello/welcome (expected).
- Same-version host/client connect and exchange snapshots without decode failure.

### Stage 3: Server emits authoritative locomotion payload

Changes:
- In `BuildSnapshotStates(...)`, fill packed locomotion from server-side locomotion state.

Acceptance checks:
- `dotnet build NetRunnerSlice.csproj` passes.
- Debug logging on server shows non-default locomotion payload when state is non-default.

### Stage 4: Client reconcile consumes authoritative locomotion

Changes:
- In `ReconcileLocal(...)`, replace reset-to-initial locomotion with decoded authoritative locomotion state restore before replay.

Acceptance checks:
- `dotnet build NetRunnerSlice.csproj` passes.
- Under netsim scenario B (`Docs/DEBUGGING_RUNBOOK.md`), ack advances and pending inputs remain bounded.
- Reconcile correction spikes do not increase versus baseline when repeatedly reconciling.
- Parkour-prep check: mode/timer continuity survives reconcile boundaries (instrumented logs show no reset-to-initial every snapshot).

## 6) ProtocolVersion Bump (Explicit)

`ProtocolVersion` **must** increment because `PlayerStateSnapshot` wire layout changes (fixed-size state bytes and packet length). Without bumping, old clients would parse incorrect offsets and corrupt snapshot decode/reconciliation.

Planned bump:
- `NetConstants.ProtocolVersion: 5 -> 6`

This keeps handshake compatibility explicit and prevents silent cross-version desync.
