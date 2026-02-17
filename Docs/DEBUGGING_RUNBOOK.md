# Debugging Runbook

Use this runbook when movement quality regresses.

## Fast Instrumentation Checklist

Always capture before changing code:

- Debug panel (`F1`) stats from `DebugOverlay.cs`:
  - `RTT`, `Jitter`, `Last Acked Input`, `Pending Inputs`, `Reconcile Error`.
- Current net-sim settings (enable/latency/jitter/loss).
- Whether issue reproduces on:
  - standalone client
  - listen host local player
  - remote player view
- Join handshake checkpoint:
  - `Welcome received` appears in logs.
  - `LocalCharacter spawned` appears after join.

## Symptom -> Likely Cause -> Where to Inspect -> What to Log

| Symptom | Likely Cause | Where to Inspect | What to Log |
|---|---|---|---|
| Sluggish controls | Prediction path not running every physics tick, or input not captured/camera not in local predicted node | `Scripts/Net/NetSession.Client.cs` (`TickClient`), `Scripts/Net/NetSession.cs` (`_UnhandledInput`), `Scripts/Player/PlayerCharacter.cs` | Log per tick: `clientTick`, built `seq`, local pre/post predict position, `pendingInputCount` |
| Wrong yaw/pitch or remote facing wrong direction | Degrees/radians mismatch, pitch not clamped consistently, yaw not propagated in snapshot | `Scripts/Net/InputSanitizer.cs`, `Scripts/Net/NetSession.Server.cs` (`SetLook`), `Scripts/Net/NetSession.Client.cs` (`UpdateRemoteInterpolation`) | Log input yaw/pitch (rad), server clamped yaw/pitch, snapshot yaw/pitch for same peer |
| Flying / divergent movement | Invalid inputs accepted, server/client config mismatch, replay mismatch, repeated jump edge from reused input | `Scripts/Net/InputSanitizer.cs`, `Scripts/Net/NetSession.Control.cs` (overrides), `Scripts/Net/NetSession.Client.cs` (`ReconcileLocal`), `Scripts/Player/PlayerMotor.cs` | Log `seq`, `ack`, replay count, correction magnitude, velocity Y before/after reconcile, active config values |
| Wallrun/slide resets or pops after reconcile | Locomotion net-state not replicated in snapshot or not applied before replay | `Scripts/Net/NetSession.Server.cs` (`BuildSnapshotStates`), `Scripts/Net/NetSession.Client.cs` (`ReconcileLocal`), `Scripts/Net/NetCodec.cs` | Log packed locomotion bytes per snapshot, decoded mode/timers before replay, and post-apply local locomotion state |
| Jittery remote players | CH1 transfer mode wrong, interpolation delay too low, clock jumps, sparse snapshots | `Scripts/Net/NetSession.Server.cs` (CH1 mode), `Scripts/Net/NetSession.Client.cs` (`renderTime`), `Scripts/Net/NetClock.cs`, `Scripts/Net/RemoteSnapshotBuffer.cs` | Log snapshot arrival `serverTick`, estimated server time, render time, sample interval, extrapolation duration |
| Rubber-banding forever | Ack not advancing, seq stream drift, strict sequential server policy fed stale/missing expected seqs repeatedly | `Scripts/Net/NetSession.Server.cs` (`LastProcessedSeq`, missing-input fallback), `Scripts/Net/NetSession.Client.cs` (`_lastAckedSeq`, pending replay), `Scripts/Net/NetSession.Control.cs` | Log `lastProcessedSeqForThatClient`, local `_nextInputSeq`, `_lastAckedSeq`, pending count trend over time |

## Targeted Logs to Add (Temporary)

Add temporary `GD.Print` with peer id and tick context in these methods:

- `NetSession.Client.cs`:
  - `TickClient`
  - `HandleSnapshot`
  - `ReconcileLocal`
- `NetSession.Server.cs`:
  - `TickServer`
  - `BuildSnapshotStates`
  - `HandleInputBundle`
- `NetSession.Control.cs`:
  - handshake apply path (`ControlType.Welcome`)

Remove temporary logs after root cause is verified.

## How to Validate A/B/C/D

Acceptance criteria:

- A) local movement immediate under latency
- B) under simulated 120 ms RTT + 20 ms jitter + 1% loss, no permanent divergence/flying and smooth remotes
- C) remote orientation matches yaw/pitch correctly
- D) reconciliation converges (no infinite rubber-band)

### Step-by-step

1. Build once:

```bash
dotnet build NetRunnerSlice.csproj
```

2. Start host and two clients (three terminals):

```bash
SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_host.sh 7777
SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_client.sh 127.0.0.1 7777 1
SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_client.sh 127.0.0.1 7777 2
```

3. On each instance press `F1` and verify net-sim controls match expected values.

4. Validate A (local feel):

- Move/jump continuously on client 1.
- Expect immediate local response (prediction), not 1-RTT delayed starts.

5. Validate B (stress):

- Run/jump for 2-3 minutes with directional changes.
- Monitor `Reconcile Error`: small oscillations are acceptable; repeated huge spikes are not.
- Ensure no sustained upward drift/flying and no permanent divergence.

6. Validate C (orientation):

- On client 2, observe client 1 body/head turning.
- Confirm no 90°/180° flips and pitch tracks expected look direction.

7. Validate D (convergence):

- On client 1, check `Last Acked Input` advances continuously.
- `Pending Inputs` should remain bounded and not grow forever.

8. If failure occurs, use the symptom table above and capture temporary logs before edits.

## WAN Input Quality Diagnostics Recipe (Approx 50ms RTT)

Use this recipe to validate the new WAN input diagnostics counters and trends.

1. Start host and one client with matching net-sim:

```bash
SIM_ENABLE=1 SIM_LATENCY_MS=25 SIM_JITTER_MS=5 SIM_LOSS_PERCENT=0.5 ./Scripts/run_host.sh 7777
SIM_ENABLE=1 SIM_LATENCY_MS=25 SIM_JITTER_MS=5 SIM_LOSS_PERCENT=0.5 ./Scripts/run_client.sh 127.0.0.1 7777 1
```

2. Run continuous movement + direction changes for 2-3 minutes.

3. Inspect metrics:
- Client `F1` overlay:
  - `ServerDiag Usage buffered/hold/neutral`
  - `ServerDiag Drops old/future`
  - `ServerDiag Missing streak cur/max`
  - `ServerDiag EffectiveDelayTicks`, `ServerDiag RTT/Jitter`
  - `Pending Inputs Count`, `Last Acked Seq`, `Last Correction XZ/Y/3D`
- Server console (`listen` and `dedicated`): periodic `ServerWANDiag: ...` lines per peer.

4. Good baseline under this profile:
- `buffered` usage near 100% over time (small short dips are acceptable).
- `hold` and `neutral` much lower than buffered.
- `dropped_old` and `dropped_future` near zero (occasional non-zero is possible during stalls or focus changes).
- `missing streak current` usually returns to 0 quickly; `max` should not keep climbing rapidly.
- `Pending Inputs Count` remains bounded and `Last Acked Seq` advances continuously.

## Focused WAN Scheduling Tests (Server-Authoritative Tick Consumption)

Use these targeted checks after scheduling changes.

1. Launch host + client at approx 50ms RTT:

```bash
SIM_ENABLE=1 SIM_LATENCY_MS=25 SIM_JITTER_MS=5 SIM_LOSS_PERCENT=0.5 ./Scripts/run_host.sh 7777
SIM_ENABLE=1 SIM_LATENCY_MS=25 SIM_JITTER_MS=5 SIM_LOSS_PERCENT=0.5 ./Scripts/run_client.sh 127.0.0.1 7777 1
```

2. Test A: hold `W` for 3s, then fully release.
- Expected: authoritative/server motion stops promptly after release; no continued “push” from stale inputs.
- Validate via remote view and server diagnostics.

3. Test B: missing input quality under same profile.
- Expected: `ServerDiag Usage buffered/hold/neutral` is dominated by `buffered`.
- Expected: `ServerDiag Missing streak cur/max` stays low; current streak should return to `0` frequently.
- Expected: `ServerDiag Drops old/future` remains near `0`.

4. Test C: listen-server local delay behavior.
- Run listen host only.
- Expected: local peer diagnostics show `ServerDiag EffectiveDelayTicks: 0`.

## Non-negotiable Invariants During Debugging

- Do not bypass prediction/reconciliation for listen host local player.
- Do not change CH1 away from `UnreliableOrdered` during baseline validation.
- Do not reintroduce gap-skip server input consumption.
- Do not apply local camera transform from hidden authoritative server node.

## Notes

- Arching movement was due to per-axis accel; fixed via vector `MoveToward`.
