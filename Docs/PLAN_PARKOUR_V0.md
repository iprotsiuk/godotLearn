# Plan: Parkour v0 (WallRun + WallJump)

## 1) Scope and hard constraints

- Implement Parkour v0 as **WallRun + WallJump** only.
- Preserve existing netcode contract:
  - server-authoritative simulation,
  - client prediction + reconciliation,
  - explicit byte protocol (`SendBytes` / `peer_packet`),
  - no `MultiplayerSynchronizer` for movement.
- Keep movement constants server-authoritative via `Welcome` override.
- Keep changes staged so each stage builds and can be validated independently.

## 2) Required config fields (server-authoritative via Welcome)

### 2.1 Minimum required fields for v0

Add to `NetworkConfig` and include in `ControlType.Welcome` payload:

1. `WallRunMaxTicks` (`int`)
- Authoritative max duration per wallrun.
- Used by server simulation and client prediction/replay.

2. `WallRunGravityScale` (`float`)
- Gravity multiplier while in wallrun.
- Used each simulation tick in wallrun mode.

3. `WallJumpVerticalVelocity` (`float`)
- Upward impulse applied on walljump.
- Must be authoritative to avoid replay drift.

### 2.2 Keep inside Welcome byte slack

Current `Welcome` layout in `Scripts/Net/NetCodec.Control.cs` uses bytes `0..81` (82 bytes total used).

- `NetConstants.ControlPacketBytes` is `96`.
- Current slack = `96 - 82 = 14` bytes.

Adding the 3 required fields above uses `4 + 4 + 4 = 12` bytes, leaving `2` bytes slack with no packet-size change.

### 2.3 If an extra v0 field is later required

If we decide we must also add `WallJumpAwaySpeed` (or similar), do it in a dedicated protocol step:

- increase `NetConstants.ControlPacketBytes`,
- extend `WriteControlWelcome`/readers,
- bump `NetConstants.ProtocolVersion`.

(Do not silently overrun the current 96-byte control packet.)

## 3) Required locomotion state: authoritative vs derived

### 3.1 Must remain authoritative and replicated

These are simulation-critical and must come from snapshot authority:

- `LocomotionMode` (`Grounded/Air/WallRun` in v0 flow),
- wall reference normal (`WallNormal` XZ quantized),
- `WallRunTicksRemaining`,
- walljump cooldown/lockout ticks.

Note: to keep v0 minimal and avoid snapshot format growth, use existing `LocoSlideTicksRemaining` as the walljump cooldown carrier in v0 (slide is out of v0 scope). Keep naming/documentation explicit so this is temporary and intentional.

### 3.2 Derived (not authoritative payload)

Safe to derive locally from authoritative state + current input:

- wall tangent direction (from `WallNormal` and `Vector3.Up`),
- camera/body presentation effects,
- wall-side sign choice for visuals,
- eligibility helpers that are pure functions of current authoritative state and scene collision query for this tick.

## 4) Exact code touchpoints

### 4.1 Config and handshake payload

- `Scripts/Net/NetworkConfig.cs`
  - add new parkour config properties.
- `Scripts/Net/NetCodec.Control.cs`
  - extend `WriteControlWelcome(...)` signature and offsets.
  - add `ReadControl...` accessors for new fields.
  - keep total payload <= `ControlPacketBytes` in v0 minimum.
- `Scripts/Net/NetSession.Control.cs`
  - server side: pass new config values into `WriteControlWelcome`.
  - client side: apply received overrides to `_config` during `ControlType.Welcome`.

### 4.2 Locomotion simulation

- `Scripts/Player/Locomotion/PlayerLocomotion.cs`
  - replace `EnableWallRun=false` stub behavior with real wallrun entry/maintain/exit.
  - implement walljump impulse path from wallrun state.
  - decrement/update authoritative counters each tick.
  - map cooldown to `SlideTicksRemaining` for v0.
- `Scripts/Player/Locomotion/LocomotionState.cs`
  - keep/adjust fields only as needed for v0 semantics (no broad refactor).
- `Scripts/Player/PlayerMotor.cs`
  - no protocol logic changes; only diagnostic taps if needed.

### 4.3 Snapshot authority + reconciliation

- `Scripts/Net/NetSession.Server.cs`
  - `BuildSnapshotStates()`: ensure v0 authoritative locomotion values are packed each snapshot.
- `Scripts/Net/NetSession.Client.cs`
  - `ReconcileLocal(...)`: continue applying authoritative locomotion before replay; confirm cooldown field mapping is applied consistently.
- `Scripts/Net/LocomotionNetState.cs`
  - keep pack/unpack aligned with v0 semantics for cooldown carrier.

### 4.4 Diagnostics and validation visibility

- `Scripts/Debug/DebugOverlay.cs`
  - optional text update: label cooldown field as `WallJumpCd` (or dual label) for v0 testing clarity.
- `Docs/NETCODE_STRATEGY.md`
  - update locomotion payload semantics and reconcile contract after implementation.
- `Docs/DECISIONS.md`
  - log temporary `SlideTicksRemaining -> WallJumpCooldown` reuse decision.

## 5) Staged rollout (each stage compiles)

## Stage 0: Baseline capture (no code edits)

- Run host + 2 clients with Scenario B from runbook.
- Record baseline from `F1`:
  - `Last Acked Seq`, `Pending Inputs`, `CorrXZ/CorrY/Corr3D`,
  - `ServerDiag Usage...`, `Missing streak`, `EffectiveDelayTicks`.

Acceptance check:
- Baseline metrics are captured and attached to task notes before v0 edits.

## Stage 1: Handshake config extension only

Changes:
- Add 3 required v0 config fields to `NetworkConfig`.
- Thread them through `Welcome` write/read/apply (using existing 14-byte slack).
- Keep movement behavior unchanged.

Acceptance checks:
- `dotnet build NetRunnerSlice.csproj` passes.
- Host/client same version connect successfully.
- Log one line after welcome apply showing new parkour constants.

Diagnostics:
- Verify no regressions in `Last Acked Seq` advancement and `Pending Inputs` boundedness.

## Stage 2: Authoritative locomotion counters wiring (no behavior change)

Changes:
- Reserve and document `SlideTicksRemaining` as v0 walljump cooldown carrier.
- Ensure pack/unpack + snapshot + reconcile preserve this value exactly.
- Keep actual wallrun/walljump logic still off.

Acceptance checks:
- Build passes.
- Inject temporary counter changes server-side (debug) and confirm local client receives same values through snapshot/reconcile.

Diagnostics:
- Add temporary `GD.Print` in `BuildSnapshotStates` and `ReconcileLocal` for cooldown/timer parity; remove after verification.

## Stage 3: WallRun enter/maintain/exit (authoritative + predicted)

Changes:
- Implement deterministic wallrun state machine in `PlayerLocomotion.Step`:
  - enter from air when wall contact/eligibility passes,
  - maintain with `WallRunGravityScale` and tangent-constrained horizontal behavior,
  - decrement `WallRunTicksRemaining`,
  - exit to air on timeout/loss of wall.
- Keep jump behavior unchanged in this stage.

Acceptance checks:
- Build passes.
- Host sees same wallrun transitions as client local prediction after reconciliation.
- Under Scenario B, no sustained correction growth or rubber-band loops.

Diagnostics:
- Track mode transitions (`Air <-> WallRun`) and timer values in temporary logs.
- Check `Corr3D` spikes are brief and converge.

## Stage 4: WallJump from WallRun

Changes:
- On `JumpPressed` during valid wallrun:
  - apply authoritative walljump impulse (upward via `WallJumpVerticalVelocity`, lateral away from wall using existing speed basis),
  - set walljump cooldown in reused cooldown field,
  - transition mode to `Air`.
- Ensure cooldown decrements deterministically and blocks immediate reattach.

Acceptance checks:
- Build passes.
- Repeated wallrun->walljump loops feel immediate locally and converge after snapshots.
- `Last Acked Seq` advances continuously; `Pending Inputs` remains bounded.

Diagnostics:
- Log walljump events with `serverTick`, `seq`, `wallNormal`, cooldown set value.
- Verify cooldown parity between server snapshot and client post-reconcile state.

## Stage 5: Cleanup + docs + full regression

Changes:
- Remove temporary logs.
- Update docs (`NETCODE_STRATEGY`, `DECISIONS`, onboarding note if needed).
- Keep protocol/channel/invariant wording aligned with current code.

Acceptance checks:
- Build passes.
- Full runbook A/B/C/D checks pass.
- No movement path uses `MultiplayerSynchronizer`.

## 6) Explicit non-goals for v0

- No slide gameplay.
- No wall-cling gameplay.
- No new packet stream type.
- No migration to RPC movement replication.
- No dedicated-server-specific parkour branch (same sim rules for listen + client prediction path).

## 7) Risk watchlist

1. Reconcile order regressions
- Keep strict order: apply authoritative state -> drop acked -> replay unacked.

2. Hidden protocol drift
- Any Welcome layout change beyond slack requires explicit protocol bump.

3. Timer mismatch between server/client
- All parkour-critical timers must be snapshot-authoritative (or deterministically replayed from authoritative source).

4. Wall-contact nondeterminism
- Keep wallrun eligibility checks simple and deterministic; avoid frame-rate dependent branches outside fixed tick.
