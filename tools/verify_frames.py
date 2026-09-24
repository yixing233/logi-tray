"""Verify the rebuilt native reader keeps its protocol contract.

The frame layout was byte-for-byte verified against a Python reference, so any
change to BuildFrame is a real regression risk. This diffs the current
--dump-frames output against the expected values computed independently here
(i.e. the frames are re-derived from the HID++ spec, not copied from the binary).
"""
import re
import subprocess

EXE = r"C:\code\chat-records\mouse-tray\native\build\mouse-tray.exe"
CLIENT_ID = 0x0A
HIDPP_LONG = 0x11


def frame(device_index, feature_index, function, params=()):
    out = [HIDPP_LONG, device_index, feature_index,
           ((function << 4) & 0xF0) | CLIENT_ID]
    out += list(params)
    out += [0] * (20 - len(out))
    return out


EXPECTED = {
    "probe_dev1":     frame(1, 0x00, 0x0),
    "probe_dev7":     frame(7, 0x00, 0x0),
    "feature_1004":   frame(1, 0x00, 0x0, (0x10, 0x04)),
    "feature_0005":   frame(1, 0x00, 0x0, (0x00, 0x05)),
    "get_status":     frame(1, 0x06, 0x1),
    "get_caps":       frame(1, 0x06, 0x0),
    "name_length":    frame(1, 0x05, 0x0),
    "name_chunk16":   frame(1, 0x05, 0x1, (0x10,)),
    "battery_voltage": frame(1, 0x07, 0x0),
    "receiver_self":  frame(0xFF, 0x00, 0x0, (0x00, 0x05)),
}

r = subprocess.run([EXE, "--dump-frames"], capture_output=True, text=True,
                   encoding="utf-8", errors="replace", timeout=30)
print(f"exit={r.returncode}")

actual = {}
for line in (r.stdout or "").splitlines():
    if "|" not in line:
        continue
    tag, hexbytes = line.split("|", 1)
    actual[tag.strip()] = [int(b, 16) for b in hexbytes.split()]

fails = []
for tag, want in EXPECTED.items():
    got = actual.get(tag)
    if got is None:
        fails.append(f"{tag}: MISSING from output")
        print(f"  FAIL {tag:18s} missing")
        continue
    if got == want:
        print(f"  OK   {tag:18s} {len(got)} bytes match")
    else:
        diff = [i for i in range(20) if got[i] != want[i]]
        fails.append(f"{tag}: differs at {diff}")
        print(f"  FAIL {tag:18s} differs at byte(s) {diff}")
        print(f"        got  {' '.join(f'{b:02X}' for b in got)}")
        print(f"        want {' '.join(f'{b:02X}' for b in want)}")

extra = set(actual) - set(EXPECTED)
if extra:
    print(f"  note: extra tags in output: {sorted(extra)}")

print()
print("RESULT:", "PASS - frame contract unchanged" if not fails else
      f"FAIL - {len(fails)} frame(s) changed")
for f in fails:
    print("  " + f)
