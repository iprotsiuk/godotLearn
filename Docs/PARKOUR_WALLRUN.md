# Parkour Wallrun Spec

This document defines deterministic wallrun/walljump behavior for prediction + reconciliation.

## Entry Conditions

- Current locomotion mode is `Air`.
- Character is not grounded after simulation step.
- Valid wall contact exists from slide collisions:
  - collision normal has `abs(y) < 0.2`,
  - XZ component is non-zero and normalized.
- Forward move intent is active (`MoveAxes.LengthSquared() >= 0.01` and `MoveAxes.Y > 0.01`).
- Air reattach cooldown is not active (`WallRunTicksRemaining == 0` while in `Air`).
- `NetworkConfig.WallRunMaxTicks == 0` (unlimited) or `> 0` (timed).

## Maintain Conditions

- While in `WallRun`, wall contact must remain valid.
- While in `WallRun`, wall normal continuity must remain valid (`dot(previous, candidate) >= 0.5`); sharp corners detach to `Air`.
- Adhesion:
  - outward normal velocity is removed (`v dot n > 0` gets canceled),
  - inward component is enforced toward target `-WallRunStickSpeed`.
- Tangent movement:
  - wish direction uses input yaw + move axes,
  - wish is projected onto wall plane via `Slide(wallNormal)`,
  - horizontal velocity accelerates toward along-wall wish using `GroundAcceleration`.
- Gravity:
  - vertical velocity applies `Gravity * WallRunGravityScale`.
- Wall normal stability:
  - choose collision normal most aligned with previous wall normal when available.

## Exit Conditions

- Grounded after move => switch to `Grounded` and clear wallrun timers.
- Wall contact lost => switch to `Air` (no reattach cooldown applied).
- Wallrun timeout (`WallRunTicksRemaining <= 0`) => switch to `Air` and set reattach cooldown (only when `WallRunMaxTicks > 0`).
- Jump press during `WallRun` => walljump in same tick, switch to `Air`, set reattach cooldown.

## Walljump Rules

- Walljump is allowed from `WallRun` without `CanJump` gate.
- Launch velocity:
  - preserve tangent component: `tangent = velocity.Slide(wallNormal)`,
  - apply jump impulse: `velocity = tangent + Up * WallJumpUpVelocity + wallNormal * WallJumpAwayVelocity`.
- `OnJump()` is called for existing jump lock lifecycle.

## Cooldown Rationale

- `WallRunTicksRemaining` is reused:
  - in `WallRun`: remaining wallrun duration,
  - in `Air`: reattach cooldown timer.
- Default cooldown is `8` ticks (~133ms at 60 Hz).
- Purpose:
  - prevents periodic `WallRun -> Air -> WallRun` flicker at timeout boundaries,
  - prevents duplicate walljump from redundant `JumpPressed` input packets.

## Multiplayer Requirements

- Any simulation-critical locomotion state must be replicated in snapshots and applied before replay.
- Reconcile order remains:
  1. apply authoritative position/velocity
  2. apply authoritative locomotion net-state
  3. apply grounded override
  4. drop acked inputs
  5. replay unacked inputs
- Do not use non-replicated counters (`ModeTicks`) for simulation-critical branching.
- No authority or transport invariant changes:
  - server authority in `NetSession.Server`,
  - prediction + reconciliation in `TickClient` / `ReconcileLocal`,
  - strict sequential input consumption on server,
  - explicit packet protocol and channel assignments unchanged.

## Tuning Parameters

- Server-authoritative (`NetworkConfig`, Welcome-synced):
  - `WallRunMaxTicks`
  - `WallRunGravityScale`
  - `WallJumpUpVelocity`
  - `WallJumpAwayVelocity`
- Local locomotion constants (`PlayerLocomotion.cs`):
  - `WallRunStickSpeed`
  - `WallRunReattachCooldownTicks`
  - `WallContactMaxAbsY`
  - `WallRunIntentMinMoveAxesSq`
