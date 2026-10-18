#!/usr/bin/env bash
# Exit code 0 if a Unity editor is running. Unity Hub's background service is also a unity.exe
# ("C:\Program Files\Unity Hub\resources\unity.exe serve") and doesn't count.
#   bash port/unity_running.sh && echo "close Unity first"
powershell.exe -NoProfile -Command \
    "if (Get-Process -Name Unity -ErrorAction SilentlyContinue | Where-Object { \$_.Path -notlike '*\\Unity Hub\\*' }) { exit 0 } else { exit 1 }"
