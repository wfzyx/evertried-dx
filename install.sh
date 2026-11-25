#!/usr/bin/env bash
# Evertried DX installer.
#   ./install.sh            install / reinstall (idempotent: always patches a fresh copy of the pristine files)
#   ./install.sh uninstall  restore the original files
# WINDATA=<Windows build's Evertried_Data dir> also installs the Halloween skin ported from it (see DxSkin).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
GAME="${GAME:-$HOME/.local/share/Steam/steamapps/common/Evertried}"
BACKUP="${BACKUP:-$HOME/.local/share/evertried-mods/backup}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

PREFS="${PREFS:-$HOME/.config/unity3d/Dan Domingues & Lunic Games/Evertried/prefs}"

if [ "${1:-}" = "uninstall" ]; then
  dotnet run -c Release --project "$ROOT/tools/Patcher" -- uninstall "$GAME" "$BACKUP"
  rm -f "$GAME/Evertried_Release_Data/Managed/halloween.dxskin"
  # The unmodded game has 5 skins: a save still wearing the ported one (index 5) would not load its skin.
  if [ -f "$PREFS" ]; then
    sed -i 's|\(_PlayerSkinsCollection_SelectedSkin" type="string">\)NQ==|\1MA==|' "$PREFS"
  fi
  python3 "$ROOT/tools/fix_fmod.py" "$GAME/Evertried_Release_Data/Plugins" "$BACKUP" --restore
  exit 0
fi

# Linux build: FMOD needs its executable-stack flag cleared on glibc >= 2.41.
python3 "$ROOT/tools/fix_fmod.py" "$GAME/Evertried_Release_Data/Plugins" "$BACKUP"

# Build the helper against the game's assemblies, then inject calls into Assembly-CSharp.dll.
dotnet build -c Release "$ROOT/src/EvertriedQoL" -p:GamePath="$GAME" --nologo -v q
dotnet run -c Release --project "$ROOT/tools/Patcher" -- install "$GAME" \
  "$ROOT/src/EvertriedQoL/bin/Release/net472/EvertriedQoL.dll" "$BACKUP"

# Halloween skin art, extracted from the user's own Windows install (never stored in this repo).
if [ -n "${WINDATA:-}" ]; then
  uv run -q --with UnityPy "$ROOT/tools/extract_skin.py" "$WINDATA" "$GAME/Evertried_Release_Data/Managed/halloween.dxskin"
fi
