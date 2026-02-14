# Vision

Build a reusable Godot 4.6 .NET multiplayer FPS foundation where player movement feels immediate while remaining server authoritative.

## Phase 1 Focus

- Correct networked movement vertical slice only.
- Listen server host/join flow.
- Prediction + reconciliation + interpolation + basic time sync.
- Walk/jump + mouse look on a simple collision map.

## Principles

- Server authority first.
- Smooth feel under real latency.
- Explicit packet protocol (no movement RPC sync magic).
- Small, testable components with low per-tick allocations.
