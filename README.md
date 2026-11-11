# evertried-dx

Quality-of-life and rebalance patches for **Evertried** (native Linux Steam build, Unity 2019.4 Mono,
Steam app id `1607040`). Nothing from the game is redistributed: the tools here modify your own
installed copy and keep backups of every file they touch.

## How the mods work
BepInEx cannot load on this build: the game's `mscorlib.dll` is stripped (no `File.WriteAllText`,
...), so the preloader dies before it can log. Instead `tools/Patcher` (Mono.Cecil) injects calls into
`Assembly-CSharp.dll` that run code from `src/EvertriedQoL`. It first checks that every game member the
helper uses still exists in the stripped assemblies, and always patches a fresh copy of the pristine
`Assembly-CSharp.dll` it saved to the backup folder, so reinstalling is safe.

## Features
- **Mastery** - every skill-mastery tier needs half the casts (`MasteryScaler.Factor`). Each skill cast
  adds 1 xp and tiers are cumulative thresholds on `SkillTreeData`, so the thresholds are scaled once per
  skill tree before any of its data is read (this includes save loading).

- **Flawless** - finish a combat floor without taking damage and the next combat floor clears itself.
  After `AutoClearDelay` (1.5 s) input is locked, every hostile enemy is killed through the normal damage
  path (so gold and xp are awarded; gold enemies first, because they pay nothing when they are the last one
  standing) and the level victory is triggered once. An auto-cleared floor never earns the bonus, so at best
  every other floor is skipped. Hits that are dodged or blocked by a shield do not count, and neither does
  self-damage. Skipped: boss, store and special floors (the bonus carries over), endless mode, custom
  floors, the tutorial and reloaded saved floors. Tunables in `Flawless.cs`: `AutoClearDelay`, `MaxFloor`.
  Hooks: `PlayerStatus.ApplyDamage` (epilogue), `LevelManager.EndCurrentLevel` and `StartLevel`.

## Linux build fix
The native Linux build does not start properly on current distros (glibc >= 2.41, e.g. Fedora 43+,
SteamOS 3.7+): FMOD's libraries are flagged as needing an executable stack, glibc refuses to load
them, and the game runs without audio (`DllNotFoundException: fmodstudio` in `Player.log`).
`tools/fix_fmod.py` clears that flag.

    ./install.sh              # needs the .NET SDK (DOTNET_ROOT, default ~/.dotnet) and python3
    ./install.sh uninstall    # restores the original files

Backups of every touched file live in `~/.local/share/evertried-mods/backup`. A Steam update or
"verify files" reverts the patches; run `./install.sh` again.

## Moving a Proton/Wine save to the native build
Under Proton, Unity keeps PlayerPrefs in the Wine registry. `tools/reg2prefs.py` converts the
`[Software\\Dan Domingues & Lunic Games\\Evertried]` section of `<prefix>/pfx/user.reg` into the
native `~/.config/unity3d/Dan Domingues & Lunic Games/Evertried/prefs` file. Close the game first;
the target is backed up as `prefs.bak-<timestamp>` and device-specific keys are kept from it.

    python3 tools/reg2prefs.py ~/.local/share/Steam/steamapps/compatdata/1607040/pfx/user.reg \
        "$HOME/.config/unity3d/Dan Domingues & Lunic Games/Evertried/prefs" [--dry-run]

The game has no Steam Cloud support, so saves are never synced; back up `prefs` yourself.
