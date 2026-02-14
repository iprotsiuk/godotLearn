# Contributor Skill Notes

## Goal

Extend game modes/maps without destabilizing authoritative movement netcode.

## Guardrails

1. Keep movement protocol ownership in `Scripts/Net/*`; do not move movement replication into RPC/MultiplayerSynchronizer.
2. Preserve 60 Hz server/client simulation and run movement simulation only in `_PhysicsProcess`.
3. Keep local prediction + sequence-based reconciliation intact for local player.
4. Keep remote interpolation in `_Process` with buffered snapshots.
5. Any new mode logic must be additive around the net layer, not inside packet codec paths.

## Where To Extend

- New modes: add `IGameMode` implementations in `Scripts/GameModes`.
- New maps: replace world geometry setup in `Scripts/Bootstrap/Main.cs` while keeping collision semantics compatible with `CharacterBody3D`.
- UI: extend `Scripts/UI/MainMenu.cs` and `Scripts/Debug/DebugOverlay.cs` without changing net packet formats.

## Netcode Safety Checklist

- Do sequence numbers remain monotonic and acked correctly?
- Are yaw/pitch still included in input and snapshots?
- Is correction still snap-vs-smooth threshold based?
- Are channels still separated: input CH0, snapshot CH1, control CH2?
- Is packet allocation behavior still bounded?
