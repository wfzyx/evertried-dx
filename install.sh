#!/usr/bin/env bash
# Evertried DX installer.
#   ./install.sh            install / reinstall
#   ./install.sh uninstall  restore the original files
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
GAME="${GAME:-$HOME/.local/share/Steam/steamapps/common/Evertried}"
BACKUP="${BACKUP:-$HOME/.local/share/evertried-mods/backup}"

if [ "${1:-}" = "uninstall" ]; then
  python3 "$ROOT/tools/fix_fmod.py" "$GAME/Evertried_Release_Data/Plugins" "$BACKUP" --restore
  exit 0
fi

# Linux build: FMOD needs its executable-stack flag cleared on glibc >= 2.41.
python3 "$ROOT/tools/fix_fmod.py" "$GAME/Evertried_Release_Data/Plugins" "$BACKUP"
