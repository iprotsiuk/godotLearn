# New Agent Onboarding

This is the minimum handoff required to work safely on movement netcode.

## Read Order

1. `VISION.md`
2. `ARCHITECTURE.md`
3. `Docs/NETCODE_STRATEGY.md`
4. `Docs/DEBUGGING_RUNBOOK.md`

Do not edit movement code before finishing this read order.

## Invariants You Must Not Violate

- Server authority stays in `Scripts/Net/NetSession.Server.cs`.
- Local control must use prediction + reconciliation (`TickClient` + `ReconcileLocal`).
- Listen host local player must use the same net pipeline (loopback), not direct authoritative control.
- Server consumes one expected input seq per tick after stream start; no gap-skip fast-forward.
- Channels remain:
  - CH0 input = `Unreliable`
  - CH1 snapshots = `UnreliableOrdered`
  - CH2 control = `Reliable`
- Yaw/pitch are part of input and snapshots; server clamps pitch and uses yaw for movement direction.

## How to Run

From project root:

```bash
./Scripts/run_host.sh 7777
./Scripts/run_client.sh 127.0.0.1 7777 1
./Scripts/run_client.sh 127.0.0.1 7777 2
```

Use `F1` for debug panel and live net-sim controls.

## Reproduce Net Scenario B

Target: ~120 ms RTT, ~20 ms jitter, ~1% loss end-to-end.

Use split one-way simulation on all peers:

```bash
SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_host.sh 7777
SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_client.sh 127.0.0.1 7777 1
SIM_ENABLE=1 SIM_LATENCY_MS=60 SIM_JITTER_MS=10 SIM_LOSS_PERCENT=0.5 ./Scripts/run_client.sh 127.0.0.1 7777 2
```

Confirm with overlay metrics (`RTT`, `Jitter`).

## If You Touch Movement, How Not to Break Prediction

Before edits:

- Run baseline and record overlay metrics.
- Confirm `Last Acked Input` advances and `Pending Inputs` stays bounded.

While editing:

- Keep input format and seq/ack semantics unchanged unless protocol change is deliberate and documented.
- Keep physics simulation in `_PhysicsProcess`; keep interpolation/smoothing in `_Process`.
- Keep `InputSanitizer` clamp rules aligned between client and server.
- Preserve replay order: authoritative snapshot -> drop acked -> replay unacked.

After edits:

- Re-run host + two clients with net sim scenario B.
- Validate A/B/C/D using `Docs/DEBUGGING_RUNBOOK.md` checklist.
- Update `Docs/NETCODE_STRATEGY.md` and `Docs/DECISIONS.md` if behavior or invariants changed.
