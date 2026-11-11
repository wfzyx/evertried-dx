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

- **Controllers** - the game reads pads through Unity's legacy joystick axes, numbered for Windows XInput.
  On Linux the same control lands on a different axis per driver: the right trigger shows up as the game's
  d-pad X, the d-pad hat as its d-pad Y, triggers rest at -1. Result: menus only answer to the left stick
  and the d-pad is swapped, dead or stuck. `DxPad` reads gamepads straight from evdev (`/dev/input/event*`)
  instead, where the kernel reports every pad with the same codes (BTN_SOUTH/EAST/..., ABS_X/Y/RX/RY/Z/RZ,
  ABS_HAT0X/Y or BTN_DPAD_*), and answers the game's own button and axis queries with the right values.
  It follows whichever pad was used last (rescans every 2 s, so hot-plugging works) and falls through to
  Unity's original behaviour if no pad can be opened. Face buttons are mapped by position, so
  Xbox/Steam-virtual pads (xpad reports BTN_X/BTN_Y), PlayStation (hid-playstation) and Nintendo
  (hid-nintendo) pads all give the same layout. The glyph set shown is still the one chosen in the game's
  controller menu; it no longer affects how input is read. Hooks: `ControllerInputs.GetControllerValue`,
  `InputHandler.GetControllerButtonIndex`, and every call to `Input.GetAxis`, `Input.GetAxisRaw` and
  `Input.anyKeyDown`. Create `/tmp/evertried-dx-debug` to log each pad press in `Player.log`.
  The settings entry that used to read "Gamepad Type" now reads "Glyph Set" (localized text patched in
  `DxText`), since it only picks the button icons.

- **Skippable splash** - the boot splash (`DxSplash`) takes one press per step: A/B/X/Y or Start on any gamepad
  (or Enter, Space, Escape, a click) jumps to the start of the next fade-out of the part currently on screen,
  and that fade plays normally. Presses only count while a logo is held (not during fades or the gaps), and the
  title screen ignores input until its 2 s fade-in is over (`MainMenu.ActiveUpdate` guard).
  The splash clip (`SplashScreen_Main`, 12.58 s) is a Dangen sting ending at 5.0 s, then three logos that fade
  out at 6.58, 9.08 and 11.58 s. The game's stripped UnityEngine has no `Animator.Play`, so the clip position
  is tracked in the helper and `Animator.speed` is raised just enough to land on the target. Hook:
  `SplashScreen.Start`.
- **Ascend runs once** - `MainMenu.StartSession` is guarded (`DxMenu`), so pressing Ascend again while its
  animation plays cannot restart it or replay its sound.
- **Glyph set is only glyphs** - `InputHandler.PromptSwitchInversion` (which swapped A/B and X/Y for the Switch
  glyph set) does nothing while the evdev pad layer is active, and axis names are matched by prefix. The PS4
  glyph set also reads its horizontal d-pad from the axis named `VerticalDPad`, which prefix matching cannot
  tell from the real vertical d-pad, so `Axis.OnControllerTypeChange` always picks the Xbox names while a pad
  is active (and the game is told again when a pad becomes active).
- **Disable Focus Timer** - the Windows build has this setting (`GameData.FocusTimerDisabled`, a row under
  "Controls"), the Linux depot (older) does not. `DxFocus` stores it in the save under the Windows key
  (`GetKeys` slot 1, the Linux build's unused `GoldMultiplier`), stops `FocusMeter.TimerMultiplier` when on,
  and `DxSettings` clones the "Controls" dropdown into a new row. Texts are the Windows build's.
  While `/tmp/evertried-dx-debug` exists, the settings row layout and (via `DxHost`) large dark UI panels,
  cameras and the pixel camera are logged to `Player.log`, and `/tmp/evertried-dx-exp` accepts live
  experiments (see `DxHost.RunExperiments`).

- **No black world** - after a level starts or the game is unpaused the world went black on the native Linux
  build under gamescope (HUD fine, `Player.log` clean). Live experiments isolated it to the game's
  `Hidden/BilinearSharp` upscale shader (`PixelArtCamera.useUpscaleShader`); the game's other path (integer
  point-filtered upscale + stretch) never goes black and is crisper, so `DxPixel` switches to it when the
  pixel camera starts.

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
