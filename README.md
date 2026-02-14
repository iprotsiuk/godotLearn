# NetRunnerSlice (Godot 4.6 .NET)

Phase 1 multiplayer movement vertical slice for a 3D FPS runner/jumper:

- Server-authoritative simulation
- Client prediction + reconciliation (sequence replay)
- Remote interpolation via snapshot buffer
- Listen server host/join flow (ENet)
- Basic time sync and debug overlay metrics
- Host-local path uses full prediction + reconciliation loopback pipeline

## Networking model

- High-level design: `ARCHITECTURE.md`
- Practical spec + invariants: `Docs/NETCODE_STRATEGY.md`
- Decision log and tradeoffs: `Docs/DECISIONS.md`
- Troubleshooting + validation checklist: `Docs/DEBUGGING_RUNBOOK.md`
- Agent handoff/read order: `Docs/NEW_AGENT_ONBOARDING.md`

## Current Scope

Implemented now:

- Walk + jump + mouse look
- Simple collision map (`CharacterBody3D`)
- Host/Join menu (no auto-host at launch)
- Explicit binary packets on ENet channels (no movement RPC sync)
- In-engine network simulation (delay/jitter/loss)

Not in Phase 1:

- weapons/projectiles
- AI
- inventory/pickups
- wallrun/advanced parkour
- matchmaking/NAT punch-through

## Local Run

Note: on case-insensitive filesystems, `scripts/` and `Scripts/` resolve to the same directory. Launch helpers are in `Scripts/run_*.sh`.

### Host + 2 clients (single sequence)

```bash
./Scripts/run_host.sh 7777
./Scripts/run_client.sh 127.0.0.1 7777 1
./Scripts/run_client.sh 127.0.0.1 7777 2
```

### Bad-network repro (approx 120ms RTT / 20ms jitter / 1% loss)

```bash
SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_host.sh 7777
SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_client.sh 127.0.0.1 7777 1
SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_client.sh 127.0.0.1 7777 2
```

## Tick/Rate Defaults

From `Config/network_config.json`:

- server tick: 60 Hz
- client tick: 60 Hz
- snapshot send: 20 Hz
- interpolation delay: 100 ms
- max extrapolation: 100 ms
- snap threshold: 1.5 m
- smooth correction window: 100 ms

## CLI Arguments

Use after Godot `--` separator:

- `--role=host|join`
- `--ip=127.0.0.1`
- `--port=7777`
- `--window-pos=x,y`
- `--window-size=w,h`
- `--sim-enable=1`
- `--sim-latency=120`
- `--sim-jitter=20`
- `--sim-loss=1`
- `--sim-seed=1337`

## Debug Overlay

Shows:

- RTT + jitter estimate
- server/client tick
- last acked input
- pending input count
- reconciliation correction magnitude
- network simulation state

Controls:

- `F1`: toggle debug panel
- panel fields: enable/latency/jitter/loss
- `Apply Net Sim`: applies simulation settings live

## Project Layout

- `Scenes/` scene roots
- `Scripts/Bootstrap` startup + map + wiring
- `Scripts/Net` transport/protocol/sync/prediction/reconciliation/interpolation
- `Scripts/Player` motor and pawn visuals
- `Scripts/GameModes` mode interfaces + stubs
- `Scripts/UI` host/join menu
- `Scripts/Debug` overlay
- `Docs` setup/input docs
- `Scripts/run_*.sh` local launch helpers
