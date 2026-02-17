# PLAYER_VISUALS.md

## Why "dragging a player into the map" is wrong in multiplayer

In this project, a player is a **runtime network entity**, not a static world prop.

If you place a player model directly in a map scene (`testWorld.tscn`), that node is just a fixed map child. It is **not** tied to per-peer spawn, authority, prediction, or reconciliation. In multiplayer this causes wrong ownership, duplicate visuals, and mismatch between what simulation drives vs what you see.

A map scene should contain world geometry and spawn markers, not pre-placed player avatars.

## Actual runtime spawn path

Player instances are created at runtime through net session code:

1. `Scripts/Net/NetSession.Shared.cs` -> `NetSession.CreateCharacter(...)`
2. `Scripts/Player/PlayerCharacter.cs` -> `PlayerCharacter.Setup(...)`

`CreateCharacter(...)` decides per-peer setup (`withCamera`, visibility, tint), then `Setup(...)` builds the character node hierarchy and visual/view rigs.

## Multiplayer-safe split of responsibilities

Use this separation and keep it strict:

1. **Simulation root (deterministic)**
   - `PlayerCharacter` / `CharacterBody3D` + `PlayerMotor` / locomotion state.
   - Drives authoritative movement, prediction, reconciliation.

2. **Render rig (third-person, per-character visual)**
   - `Scenes/Player/ThirdPersonModel.tscn` instanced under visual yaw root.
   - Exists for visible spawned characters (local hidden in first-person, remote visible).

3. **Local-only view rig (camera/arms)**
   - `Scenes/Player/FirstPersonViewRig.tscn` instanced only when `withCamera == true`.
   - Remote players do not get an active camera rig.
   - Hidden server-sim characters do not instantiate local view rig.

## How to replace visuals safely

### Third-person model

Edit:

- `Scenes/Player/ThirdPersonModel.tscn`

This is the replaceable per-character model scene (GLTF, skeleton, mesh hierarchy, etc). Keep it as a model-only scene that can be instanced for each spawned player.

### First-person camera + arms

Edit:

- `Scenes/Player/FirstPersonViewRig.tscn`

Adjust local camera position/rotation, arms hierarchy, and `WeaponMount` anchor here. This rig should remain local-player only.

## Render-only effects vs deterministic simulation

### Must stay render-only (`_Process` / visual transforms only)

- Headbob
- Wallrun camera roll
- Weapon sway
- Camera shake
- Cosmetic tilt/lag

These effects should only offset local visual nodes (for example `Effects` in first-person rig) and must not write simulation state.

### Must stay deterministic (`_PhysicsProcess` / motor state)

- `PlayerMotor` movement integration
- Grounding/jump/wallrun rules used by simulation
- Velocity/position used for prediction and reconciliation
- Networked movement state and snapshots

Do not mix cosmetic camera math into motor or net state. If an effect changes gameplay motion or packets, it is in the wrong layer.
