#!/usr/bin/env bash
# Scripts/run_client_badnet.sh
set -euo pipefail

SIM_ENABLE=1 \
SIM_LATENCY_MS=120 \
SIM_JITTER_MS=20 \
SIM_LOSS_PERCENT=1 \
SIM_SEED="${SIM_SEED:-1337}" \
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/run_client.sh" "${1:-127.0.0.1}" "${2:-7777}" "${3:-1}"
