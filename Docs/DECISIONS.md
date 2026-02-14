# Decisions

This file records the 10 Phase 1 decisions exactly as provided, with rationale, implementation mapping, and change-risk notes.

## Decision 1

Verbatim:

> MUST run the full client prediction + reconciliation path even for the host’s local player.
> Do NOT special-case “authoritative local host control” in Phase 1. That hides bugs and diverges from dedicated-server behavior.
> Implementation note: host runs both server + a local client instance logically (loopback), same input->server->snapshot->reconcile pipeline.

Rationale:

- Listen-server behavior must match dedicated-server behavior as closely as possible.
- Any host-only shortcut can hide replay/ack bugs and fail later when dedicated mode is introduced.

Primary implementation files:

- `Scripts/Net/NetSession.cs`
- `Scripts/Net/NetSession.Client.cs`
- `Scripts/Net/NetSession.Server.cs`

If changed later, what breaks:

- Host-only movement bugs can pass local tests but fail in real client/server sessions.
- Reconciliation regressions may go undetected until dedicated server work.

## Decision 2

Verbatim:

> Process exactly ONE sequential input per server tick (fixed 60Hz). No “gap-skip fast-forward”.
> If an input for the expected tick/seq is missing: reuse last known input (or neutral input) for that tick.
> Use redundancy (last 2 commands piggybacked) to reduce misses. Optional small input buffering can exist but default must not add noticeable latency.

Rationale:

- Fixed one-step advancement per tick keeps authority timeline stable.
- Gap-skip causes sudden authority jumps that destabilize reconcile and feel like teleporting.

Primary implementation files:

- `Scripts/Net/NetSession.Server.cs`
- `Scripts/Net/InputHistoryBuffer.cs`
- `Scripts/Net/NetCodec.cs`

If changed later, what breaks:

- Fast-forwarding creates larger correction spikes and can cause permanent divergence loops.

## Decision 3

Verbatim:

> Phase 1: edge-triggered JumpPressed only (no coyote time, no jump buffering).
> Phase 2 can add coyote/buffer windows.

Rationale:

- Keeps movement state machine minimal while validating netcode primitives.
- Removes ambiguity in replay behavior for initial correctness pass.

Primary implementation files:

- `Scripts/Net/NetModels.cs`
- `Scripts/Net/NetSession.Client.cs`
- `Scripts/Player/PlayerMotor.cs`

If changed later, what breaks:

- Adding jump grace windows without replay-safe logic can desync local prediction and server authority.

## Decision 4

Verbatim:

> Clamp pitch server-side to the same limits as the local client (e.g., [-89°, +89°]) and replicate the clamped value.
> Clients should also clamp before sim for safety, but remote visuals should use the authoritative (already clamped) value.

Rationale:

- One canonical orientation value avoids local/remote orientation mismatches.
- Server-side clamp prevents malicious or invalid input ranges.

Primary implementation files:

- `Scripts/Net/InputSanitizer.cs`
- `Scripts/Net/NetSession.Server.cs`
- `Scripts/Net/NetSession.Client.cs`
- `Scripts/Player/PlayerCharacter.cs`

If changed later, what breaks:

- Mixed clamp behavior causes head/body direction mismatches and replay drift.

## Decision 5

Verbatim:

> Phase 1: fixed interpolation delay from NetworkConfig (default 100ms).
> No dynamic/adaptive delay in Phase 1 (can be Phase 2).

Rationale:

- Fixed delay keeps tuning deterministic while baseline behavior is validated.
- Adaptive delay adds extra control loops and complexity not needed in Phase 1.

Primary implementation files:

- `Scripts/Net/NetworkConfig.cs`
- `Scripts/Net/NetSession.Client.cs`
- `Scripts/Net/RemoteSnapshotBuffer.cs`

If changed later, what breaks:

- Poor adaptive tuning can introduce oscillation or visible remote jitter.

## Decision 6

Verbatim:

> Use UNRELIABLE_ORDERED (unreliable sequenced) for snapshots on CH1.
> CH0 inputs: UNRELIABLE.
> CH2 control/handshake: RELIABLE.

Rationale:

- Snapshot stream should prefer freshest ordered data without reliability stalls.
- Inputs are frequent and redundant; control must be guaranteed.

Primary implementation files:

- `Scripts/Net/NetConstants.cs`
- `Scripts/Net/NetSession.Server.cs`
- `Scripts/Net/NetSession.Client.cs`
- `Scripts/Net/NetSession.Shared.cs`

If changed later, what breaks:

- Reliable snapshots can backlog and increase perceived latency; unordered snapshots can increase remote jitter.

## Decision 7

Verbatim:

> Phase 1: minimal sanity (clamp ranges, reject NaNs/infs, cap move axes magnitude).
> Phase 2: real anti-cheat checks (speed/accel limits, illegal state transitions, etc.).

Rationale:

- Basic sanity prevents obvious packet corruption and invalid inputs.
- Full anti-cheat is intentionally deferred to keep Phase 1 scope tight.

Primary implementation files:

- `Scripts/Net/InputSanitizer.cs`
- `Scripts/Net/NetSession.Server.cs`
- `Scripts/Net/NetSession.Client.cs`

If changed later, what breaks:

- Removing sanity checks can cause flying/divergence from invalid inputs.
- Over-aggressive anti-cheat early can produce false positives and broken feel.

## Decision 8

Verbatim:

> Server handshake config OVERRIDES all simulation/network-critical settings (tick, snapshot rate, interp delay, thresholds).
> Client may keep purely visual settings local (HUD, camera bob intensity, etc.).

Rationale:

- Shared movement-critical config is required for deterministic replay convergence.
- Visual-only client settings can remain local without affecting authority.

Primary implementation files:

- `Scripts/Net/NetCodec.Control.cs`
- `Scripts/Net/NetSession.Control.cs`
- `Scripts/Net/NetworkConfig.cs`

If changed later, what breaks:

- Divergent tick/interp/reconcile settings between client/server will increase correction error and can prevent convergence.

## Decision 9

Verbatim:

> Yes: debug overlay is a toggleable in-game panel (e.g., F1).
> Include editable net-sim controls (delay/jitter/loss) and live stats.

Rationale:

- Fast iteration on latency/jitter/loss scenarios is required for netcode tuning.
- In-game controls reduce setup friction and improve reproducibility.

Primary implementation files:

- `Scripts/Debug/DebugOverlay.cs`
- `Scripts/Bootstrap/InputBootstrap.cs`
- `Scripts/Bootstrap/Main.cs`
- `Scripts/Net/NetSession.cs`

If changed later, what breaks:

- Removing live controls slows debugging and makes acceptance criteria harder to validate consistently.

## Decision 10

Verbatim:

> Prefer CLI mode switch in the same project (e.g., --dedicated / --server) that loads a server-only entrypoint scene and disables rendering/audio.
> Not a separate project; a separate scene entrypoint is fine, selected by CLI.

Rationale:

- Single project reduces drift between listen and dedicated implementations.
- CLI mode switch allows shared netcode with different startup surfaces.

Primary implementation files (current scaffolding relevance):

- `Scripts/Bootstrap/CliArgs.cs`
- `Scripts/Bootstrap/Main.cs`
- `Scripts/Net/NetSession.cs`

If changed later, what breaks:

- Splitting into separate projects too early can duplicate netcode and create maintenance drift.
