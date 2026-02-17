# Plan: Shooting Responsiveness (Client Feel, Server Authority Preserved)

## Goal

Fix local shooting feel so clicks get immediate feedback, while keeping hit/miss authority fully on server.

## Constraints

- No protocol or packet layout changes.
- Keep server authoritative for hit validation and damage.
- Keep current fire request path (fire embedded in `InputCommand.FirePressed`).
- Keep `FireResult` as authoritative hit/miss feedback.

## Problem Summary

- Local projectile/tracer visuals are currently spawned only on `FireVisual` receive, so they are delayed by RTT.
- `FireVisual.Origin` comes from server-authoritative origin, but local camera/view is render-smoothed, causing side-offset perception while moving/turning.

## Proposed Approach

1. Spawn predicted local fire FX immediately on local click path.
2. Keep server fire evaluation unchanged.
3. Suppress rendering of `FireVisual` when `visual.ShooterPeerId == _localPeerId` to avoid duplicate local visuals.
4. Continue rendering `FireVisual` for all remote shooters unchanged.

## Exact Touchpoints

### 1) Local immediate FX trigger

- File: `Scripts/Net/NetSession.Input.cs`
- Method: `TryLatchFirePressed()`

Plan:
- Keep existing input latch behavior.
- Add a call to a local-only helper (in `NetSession.Weapon.cs`) that spawns immediate predicted FX if local character/camera is valid.

### 2) Predicted FX implementation

- File: `Scripts/Net/NetSession.Weapon.cs`
- Methods/fields to use:
  - `SpawnLocalProjectile(...)` (already exists)
  - `YawPitchToDirection(...)` (already exists)
  - `_localCharacter`, `_lookYaw`, `_lookPitch`

Plan:
- Add helper like `TrySpawnPredictedLocalFireFx()`:
  - Compute direction from local view (`_lookYaw`, `_lookPitch`) or local camera forward.
  - Use local camera/world view origin (not server origin).
  - Use local character velocity for projectile visual carry (`SpawnLocalProjectile` supports this).
- Keep this visual-only; do not affect authoritative fire/hit flow.

### 3) Suppress delayed server visual for local shooter

- File: `Scripts/Net/NetSession.Weapon.cs`
- Method: `HandleFireVisual(byte[] packet)`

Plan:
- After decode, if `visual.ShooterPeerId == _localPeerId` and `_localPeerId != 0`, return early.
- Continue existing path for remote shooters (spawn remote projectile from server visual).

### 4) No changes to authority path

- Keep unchanged:
  - `ProcessFireFromInputCommand(...)` (server evaluation)
  - `BroadcastFireResult(...)` and `HandleFireResult(...)` (authoritative feedback)
  - Packet structs/codecs (`NetModels`, `NetCodec.Fire`, constants)

## Rollout Stages

### Stage A: Local predicted FX plumbing

- Add `TrySpawnPredictedLocalFireFx()` and call from `TryLatchFirePressed()`.
- No suppression yet.

Check:
- Local click instantly shows projectile FX even under latency.
- Server hit logic still works.

### Stage B: Local FireVisual suppression

- Add suppression check in `HandleFireVisual` for local shooter.

Check:
- No double projectile/tracer for local shots.
- Remote shot visuals still appear.

### Stage C: Alignment polish (no authority impact)

- Tune predicted origin/direction source:
  - prefer local camera world transform for origin,
  - keep direction tied to local look/camera forward.

Check:
- “Shot from the side” perception reduced while strafing/turning.

## Acceptance Checks

1. Build:
- `dotnet build NetRunnerSlice.csproj` passes.

2. Immediate local feel:
- In listen host and standalone client, clicking fire shows local FX immediately (no noticeable RTT delay).

3. Authority unchanged:
- `FireResult` still determines hit/miss indicator behavior.
- No client-side hit authority introduced.

4. No duplicate local visuals:
- One visual per local shot (predicted only), not predicted + delayed server visual.

5. Remote correctness:
- Remote shooters still render from authoritative `FireVisual`.

6. Reconciliation safety:
- Normal movement metrics remain bounded (`Last Acked Seq`, `Pending Inputs`, correction values).

## Minimal Diagnostics (Temporary)

Add short `GD.Print` logs, then remove after validation:

- On local predicted spawn:
  - `FirePredFx: peer=<id> yaw=<...> pitch=<...> origin=<...>`
- On suppressed server visual:
  - `FireVisualSuppressLocal: peer=<id> serverTick=<...>`
- Optional latency sanity:
  - On `HandleFireResult` for local shooter: log time delta from last local predicted shot to result receive.

## Risks / Notes

- Predicted visual and authoritative server visual may differ slightly in exact origin/direction under correction smoothing; this is acceptable for visual-only responsiveness as long as hit authority remains server-side.
- Keep predicted FX lightweight to avoid per-shot allocation spikes beyond current projectile visual behavior.
