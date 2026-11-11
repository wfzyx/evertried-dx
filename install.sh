#!/usr/bin/env bash
# Evertried DX installer.
#   ./install.sh            install / reinstall (idempotent: always patches a fresh copy of the pristine files)
#   ./install.sh uninstall  restore the original files
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
GAME="${GAME:-$HOME/.local/share/Steam/steamapps/common/Evertried}"
BACKUP="${BACKUP:-$HOME/.local/share/evertried-mods/backup}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if [ "${1:-}" = "uninstall" ]; then
  dotnet run -c Release --project "$ROOT/tools/Patcher" -- uninstall "$GAME" "$BACKUP"
  python3 "$ROOT/tools/fix_fmod.py" "$GAME/Evertried_Release_Data/Plugins" "$BACKUP" --restore
  exit 0
fi

# Linux build: FMOD needs its executable-stack flag cleared on glibc >= 2.41.
python3 "$ROOT/tools/fix_fmod.py" "$GAME/Evertried_Release_Data/Plugins" "$BACKUP"

# Build the helper against the game's assemblies, then inject calls into Assembly-CSharp.dll.
dotnet build -c Release "$ROOT/src/EvertriedQoL" -p:GamePath="$GAME" --nologo -v q
dotnet run -c Release --project "$ROOT/tools/Patcher" -- install "$GAME" \
  "$ROOT/src/EvertriedQoL/bin/Release/net472/EvertriedQoL.dll" "$BACKUP"
