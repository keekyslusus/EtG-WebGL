#!/usr/bin/env bash
# Audit installed shader selection, then render temporary branch/keyword probes in Unity.
# Unity must be closed. No apply.sh: original shader/material assets are read-only.
set -euo pipefail
PORT="$(cd "$(dirname "$0")" && pwd)"
python -I "$PORT/variant_selection.py" "$@"
