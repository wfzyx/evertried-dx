#!/usr/bin/env bash
# Build here and install on another machine over ssh, e.g. a Steam Deck that has no .NET SDK.
#   tools/deploy-remote.sh <ssh-host>               build + install
#   tools/deploy-remote.sh <ssh-host> --uninstall   put the original files back
# The game does not need to be installed locally: the reference assemblies are copied from the
# remote machine, and the remote's pristine Assembly-CSharp.dll backup is what gets patched.
set -euo pipefail

HOST="${1:?usage: deploy-remote.sh <ssh-host> [--uninstall]}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
SSH=(ssh -o BatchMode=yes "$HOST")

RHOME="$("${SSH[@]}" 'echo $HOME')"
R_DATA="$RHOME/.local/share/Steam/steamapps/common/Evertried/Evertried_Release_Data"
R_BACKUP="$RHOME/.local/share/evertried-mods/backup"

if [ "${2:-}" = "--uninstall" ]; then
  "${SSH[@]}" "set -e
    cp -f '$R_BACKUP/Assembly-CSharp.dll.orig' '$R_DATA/Managed/Assembly-CSharp.dll'
    rm -f '$R_DATA/Managed/EvertriedQoL.dll'
    cp -f '$R_BACKUP/libfmodstudio.so' '$R_BACKUP/libfmodstudioL.so' '$R_DATA/Plugins/'
    echo 'restored originals on $HOST'"
  exit 0
fi

# The [E] keeps pgrep from matching this very command line.
if "${SSH[@]}" "pgrep -f '[E]vertried_Release.x86_64' >/dev/null"; then
  echo "the game is running on $HOST; close it first" >&2
  exit 1
fi

# Remote backups of the pristine files (first run only; never overwritten).
"${SSH[@]}" "set -e
  mkdir -p '$R_BACKUP'
  if [ ! -f '$R_BACKUP/Assembly-CSharp.dll.orig' ]; then
    if grep -q EvertriedQoL '$R_DATA/Managed/Assembly-CSharp.dll'; then
      echo 'Assembly-CSharp.dll is already patched and there is no pristine backup; verify files in Steam first' >&2; exit 1
    fi
    cp '$R_DATA/Managed/Assembly-CSharp.dll' '$R_BACKUP/Assembly-CSharp.dll.orig'
  fi
  cp -n '$R_DATA/Plugins/libfmodstudio.so' '$R_DATA/Plugins/libfmodstudioL.so' '$R_BACKUP/'"

# Stage a copy of the remote game data, with the pristine Assembly-CSharp.dll and FMOD libraries.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/game"
scp -q -r -o BatchMode=yes "$HOST:$R_DATA" "$STAGE/game/"
scp -q -o BatchMode=yes "$HOST:$R_BACKUP/Assembly-CSharp.dll.orig" "$STAGE/game/Evertried_Release_Data/Managed/Assembly-CSharp.dll"
scp -q -o BatchMode=yes "$HOST:$R_BACKUP/libfmodstudio.so" "$HOST:$R_BACKUP/libfmodstudioL.so" "$STAGE/game/Evertried_Release_Data/Plugins/"
rm -f "$STAGE/game/Evertried_Release_Data/Managed/EvertriedQoL.dll"

# Build + patch into the staging copy.
GAME="$STAGE/game" BACKUP="$STAGE/backup" "$ROOT/install.sh"

OUT="$STAGE/game/Evertried_Release_Data"
scp -q -o BatchMode=yes "$OUT/Managed/Assembly-CSharp.dll" "$OUT/Managed/EvertriedQoL.dll" \
  "$OUT/Plugins/libfmodstudio.so" "$OUT/Plugins/libfmodstudioL.so" "$HOST:/tmp/"
"${SSH[@]}" "set -e
  cd '$R_DATA'
  install -m 755 /tmp/Assembly-CSharp.dll /tmp/EvertriedQoL.dll Managed/
  install -m 755 /tmp/libfmodstudio.so /tmp/libfmodstudioL.so Plugins/
  rm -f /tmp/Assembly-CSharp.dll /tmp/EvertriedQoL.dll /tmp/libfmodstudio.so /tmp/libfmodstudioL.so"

echo "verifying $HOST against the local build:"
( cd "$OUT" && sha256sum Managed/Assembly-CSharp.dll Managed/EvertriedQoL.dll Plugins/libfmodstudio.so Plugins/libfmodstudioL.so ) > "$STAGE/sums"
"${SSH[@]}" "cd '$R_DATA' && sha256sum -c -" < "$STAGE/sums"
