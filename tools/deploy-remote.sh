#!/usr/bin/env bash
# Build here and install on another machine over ssh, e.g. a Steam Deck that has no .NET SDK.
#   tools/deploy-remote.sh <ssh-host>               build + install
#   tools/deploy-remote.sh <ssh-host> --uninstall   put the original files back
# The game does not need to be installed locally: the reference assemblies are copied from the
# remote machine, and the remote's pristine Assembly-CSharp.dll backup is what gets patched.
# The Halloween skin is extracted from the Windows build installed here (WINDATA, default: this machine's
# Steam copy when it is the Windows build); without one the skin is skipped.
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
    rm -f '$R_DATA/Managed/halloween.dxskin'
    # The unmodded game has 5 skins: a save still wearing the ported one (index 5) would not load its skin.
    prefs=\"\$HOME/.config/unity3d/Dan Domingues & Lunic Games/Evertried/prefs\"
    if [ -f \"\$prefs\" ]; then sed -i 's|\\(_PlayerSkinsCollection_SelectedSkin\" type=\"string\">\\)NQ==|\\1MA==|' \"\$prefs\"; fi
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
WIN_DEFAULT="$HOME/.local/share/Steam/steamapps/common/Evertried/Evertried_Data"
if [ -z "${WINDATA:-}" ] && [ -f "$WIN_DEFAULT/../GameAssembly.dll" ]; then
  WINDATA="$WIN_DEFAULT"
fi
rm -f "$STAGE/game/Evertried_Release_Data/Managed/halloween.dxskin"
GAME="$STAGE/game" BACKUP="$STAGE/backup" WINDATA="${WINDATA:-}" "$ROOT/install.sh"

OUT="$STAGE/game/Evertried_Release_Data"
FILES=(Managed/Assembly-CSharp.dll Managed/EvertriedQoL.dll Plugins/libfmodstudio.so Plugins/libfmodstudioL.so)
if [ -f "$OUT/Managed/halloween.dxskin" ]; then
  FILES+=(Managed/halloween.dxskin)
else
  echo "no Windows build (WINDATA): the Halloween skin is not installed" >&2
fi
for f in "${FILES[@]}"; do
  scp -q -o BatchMode=yes "$OUT/$f" "$HOST:/tmp/evertried-dx-$(basename "$f")"
done
"${SSH[@]}" "set -e
  cd '$R_DATA'
  for f in ${FILES[*]}; do
    install -m 755 /tmp/evertried-dx-\$(basename \$f) \$f
    rm -f /tmp/evertried-dx-\$(basename \$f)
  done"

echo "verifying $HOST against the local build:"
( cd "$OUT" && sha256sum "${FILES[@]}" ) > "$STAGE/sums"
"${SSH[@]}" "cd '$R_DATA' && sha256sum -c -" < "$STAGE/sums"
