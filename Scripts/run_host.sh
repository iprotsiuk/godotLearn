#!/usr/bin/env bash
# Scripts/run_host.sh
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if command -v godot >/dev/null 2>&1; then
  GODOT_BIN="${GODOT_BIN:-godot}"
else
  GODOT_BIN="${GODOT_BIN:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
fi
PORT="${1:-7777}"

SIM_ENABLE="${SIM_ENABLE:-0}"
SIM_LATENCY="${SIM_LATENCY_MS:-0}"
SIM_JITTER="${SIM_JITTER_MS:-0}"
SIM_LOSS="${SIM_LOSS_PERCENT:-0}"
SIM_SEED="${SIM_SEED:-1337}"

"${GODOT_BIN}" --path "${ROOT_DIR}" -- \
  --role=host \
  --port="${PORT}" \
  --window-pos=40,40 \
  --window-size=1280,720 \
  --sim-enable="${SIM_ENABLE}" \
  --sim-latency="${SIM_LATENCY}" \
  --sim-jitter="${SIM_JITTER}" \
  --sim-loss="${SIM_LOSS}" \
  --sim-seed="${SIM_SEED}"
