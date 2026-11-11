#!/usr/bin/env python3
"""Make Evertried's bundled FMOD libraries loadable on glibc >= 2.41.

libfmodstudio.so / libfmodstudioL.so ship with an executable-stack flag (PT_GNU_STACK = RWE).
Modern glibc refuses to dlopen such libraries ("cannot enable executable stack as shared object
requires: Invalid argument"), so FMOD fails to initialise and the native Linux build starts with
no audio. Clearing the X bit of PT_GNU_STACK fixes it; the libraries do not need an executable stack.

    fix_fmod.py <Evertried_Release_Data/Plugins> <backupDir>            patch (backs up originals first)
    fix_fmod.py <Evertried_Release_Data/Plugins> <backupDir> --restore  put the originals back
"""
import os, shutil, struct, sys

PT_GNU_STACK = 0x6474E551
LIBS = ("libfmodstudio.so", "libfmodstudioL.so")


def clear_exec_stack(path):
    with open(path, "r+b") as f:
        header = f.read(64)
        if header[:4] != b"\x7fELF" or header[4] != 2:
            raise SystemExit(f"{path}: not a 64-bit ELF file")
        phoff = struct.unpack_from("<Q", header, 0x20)[0]
        phentsize, phnum = struct.unpack_from("<HH", header, 0x36)
        for i in range(phnum):
            off = phoff + i * phentsize
            f.seek(off)
            ptype, flags = struct.unpack("<II", f.read(8))
            if ptype == PT_GNU_STACK:
                if flags & 1:
                    f.seek(off + 4)
                    f.write(struct.pack("<I", flags & ~1))
                    return True
                return False
    raise SystemExit(f"{path}: no PT_GNU_STACK header")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) != 2:
        sys.exit(__doc__)
    plugins, backup = args
    os.makedirs(backup, exist_ok=True)
    restore = "--restore" in sys.argv
    for name in LIBS:
        path, bak = os.path.join(plugins, name), os.path.join(backup, name)
        if restore:
            if not os.path.exists(bak):
                sys.exit(f"no backup for {name} in {backup}")
            shutil.copy2(bak, path)
            print(f"{name}: restored")
            continue
        if not os.path.exists(bak):
            shutil.copy2(path, bak)
        print(f"{name}: " + ("cleared exec-stack flag" if clear_exec_stack(path) else "already fixed"))


if __name__ == "__main__":
    main()
