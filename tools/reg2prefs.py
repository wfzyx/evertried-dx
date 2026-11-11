#!/usr/bin/env python3
"""Convert Evertried's Unity PlayerPrefs from a Wine/Proton registry (user.reg) to the native
Linux `prefs` XML file.

    reg2prefs.py <user.reg> <target prefs> [--dry-run]

Registry values: REG_DWORD -> int, REG_BINARY(hex:) -> string (NUL-terminated), hex(4): 8 bytes ->
float (stored as a little-endian double). The native file stores strings as base64 without NUL.
Device-specific keys (screen manager, monitor, unity.* analytics ids) are kept from the target.
The target is backed up next to itself as `prefs.bak-<timestamp>` before it is overwritten.
"""
import base64, os, re, shutil, struct, sys, time
from xml.sax.saxutils import escape, unescape

SECTION = r"[Software\\Dan Domingues & Lunic Games\\Evertried]"
KEEP_FROM_TARGET = re.compile(r"^(Screenmanager |UnitySelectMonitor$|unity\.)")


def parse_registry(path):
    text = open(path, encoding="utf-8", errors="surrogateescape").read()
    # join "\"-continued hex lines
    text = re.sub(r"\\\r?\n\s*", "", text)
    values, inside = {}, False
    for line in text.splitlines():
        if line.startswith("["):
            inside = line.startswith(SECTION)
            continue
        if not inside or not line.startswith('"'):
            continue
        m = re.match(r'^"((?:[^"\\]|\\.)*)"=(.*)$', line)
        if not m:
            continue
        name = re.sub(r"_h\d+$", "", m.group(1).replace('\\\\', '\\').replace('\\"', '"'))
        raw = m.group(2)
        if raw.startswith("dword:"):
            v = struct.unpack("<i", struct.pack("<I", int(raw[6:], 16)))[0]
            values[name] = ("int", str(v))
        elif raw.startswith("hex(4):"):
            data = bytes(int(b, 16) for b in raw[7:].split(",") if b)
            if len(data) == 8:
                f = struct.unpack("<d", data)[0]
            elif len(data) == 4:
                f = struct.unpack("<f", data)[0]
            else:
                raise ValueError(f"unexpected float size for {name}: {len(data)}")
            values[name] = ("float", repr(float(f)).rstrip("0").rstrip(".") if "." in repr(float(f)) else repr(float(f)))
        elif raw.startswith("hex:"):
            data = bytes(int(b, 16) for b in raw[4:].split(",") if b)
            if data.endswith(b"\x00"):
                data = data[:-1]
            values[name] = ("string", base64.b64encode(data).decode("ascii"))
        else:
            raise ValueError(f"unsupported registry value for {name}: {raw[:30]}")
    return values


def parse_prefs(path):
    values = {}
    if not os.path.exists(path):
        return values
    for m in re.finditer(r'<pref name="([^"]*)" type="(\w+)">([^<]*)</pref>', open(path, encoding="utf-8").read()):
        values[unescape(m.group(1), {"&quot;": '"'})] = (m.group(2), m.group(3))
    return values


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dry = "--dry-run" in sys.argv
    if len(args) != 2:
        sys.exit(__doc__)
    reg, target = args
    imported = parse_registry(reg)
    if not imported:
        sys.exit("ERROR: no Evertried section found in " + reg)
    existing = parse_prefs(target)

    merged = {k: v for k, v in existing.items() if KEEP_FROM_TARGET.match(k)}
    skipped = [k for k in imported if KEEP_FROM_TARGET.match(k)]
    for k, v in imported.items():
        if not KEEP_FROM_TARGET.match(k):
            merged[k] = v

    lines = ['<unity_prefs version_major="1" version_minor="1">']
    for k in sorted(merged):
        t, v = merged[k]
        lines.append(f'\t<pref name="{escape(k, {chr(34): "&quot;"})}" type="{t}">{v}</pref>')
    lines.append("</unity_prefs>")
    out = "\n".join(lines)

    slots = sorted({k.split("_")[0] for k in merged if re.match(r"Slot\d+_", k)})
    print(f"imported {len(imported) - len(skipped)} keys ({', '.join(slots)}), kept {len(merged) - (len(imported) - len(skipped))} from target, skipped {len(skipped)} device-specific")
    if dry:
        print(out[:1500])
        return
    if os.path.exists(target):
        bak = f"{target}.bak-{time.strftime('%Y%m%d-%H%M%S')}"
        shutil.copy2(target, bak)
        print("backup:", bak)
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "w", encoding="utf-8") as f:
        f.write(out)
    print("wrote", target)


if __name__ == "__main__":
    main()
