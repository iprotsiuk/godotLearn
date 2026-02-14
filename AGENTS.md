# AGENTS.md

## Project

NetRunnerSlice is a Godot 4.6 .NET multiplayer FPS movement vertical slice focused on server-authoritative movement with responsive client feel.

## Read Order

1. `VISION.md`
2. `ARCHITECTURE.md`
3. `Docs/NETCODE_STRATEGY.md`
4. `Docs/NEW_AGENT_ONBOARDING.md`

## Invariants (Do Not Break)

- Keep server authority in `Scripts/Net/NetSession.Server.cs`.
- Keep client prediction + reconciliation in `Scripts/Net/NetSession.Client.cs`.
- Keep strict sequential input consumption on server (one expected seq per tick after stream start).
- Keep explicit packet protocol (no movement replication magic / no MultiplayerSynchronizer movement).
- Keep custom transport on SceneMultiplayer bytes API:
  - send via `SceneMultiplayer.SendBytes`
  - receive via `SceneMultiplayer.peer_packet`
- Keep channel roles:
  - channel 1: input (`Unreliable`)
  - channel 2: snapshots (`UnreliableOrdered`)
  - channel 3: control (`Reliable`)
  - channel 0 is reserved for SceneMultiplayer internals.
- Keep yaw/pitch in input and snapshot payloads.
- Keep time sync + interpolation pipeline active for remote smoothing.

## Scene Flow

- Entry: `res://Scenes/Main.tscn`
- Menu: `res://Scenes/UI/MainMenu.tscn`
- World: `res://Scenes/testWorld.tscn` (reuse exactly, no generated replacement worlds)

## ProtoController Rule (Critical)

`ProtoController` inside `res://Scenes/testWorld.tscn` is an editor spawn marker only.

At runtime:
- read its transform as spawn origin reference,
- remove it from the loaded world instance,
- spawn/play only net-spawned `PlayerCharacter` pawns under runtime `Players`.

This prevents duplicate capsules and wrong “which pawn is mine” behavior.

## Quick Multiplayer Verification

1. Run host from menu, verify world loads from `res://Scenes/testWorld.tscn`.
2. Verify no extra `ProtoController` capsule remains in runtime world.
3. Join with a client and move with WASD + mouse look.
4. Verify host sees client movement and client sees host movement.
5. Verify yaw/pitch replication appears correct for remote view.
6. Verify no “jump in place only” secondary pawn behavior.

## UI Input Safety

If debug UI overlays are present, full-screen root controls must not swallow clicks intended for the menu.

- Overlay full-screen root: `MouseFilter = Ignore` (or safe pass-through).
- Interactive overlay panel controls: `MouseFilter = Stop`.
