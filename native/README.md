# Native HID++ reader

The desktop tray interface is the WPF application in `wpf/`. This native executable is its HID++ helper: `BatteryService` invokes `mouse-tray.exe --once` and parses the device name, battery percentage, and charging state from stdout.

The reader dynamically enumerates Logitech HID devices and tries **every** battery feature, in order of information quality, taking the first that answers:

| Feature | Notes |
| --- | --- |
| `0x1004` UnifiedBattery | Preferred. Its capabilities byte is read first to tell "reports a percentage" from "reports levels only". |
| `0x1000` BatteryStatus | Older devices such as the G304. |
| `0x0104` CenturionBatterySOC | Newer Centurion family. |
| `0x1001` BatteryVoltage | Voltage only; converted with the discharge curve. |
| `0x1F20` ADC_MEASUREMENT | The other voltage measurement. |

Byte layouts follow the reference implementations — Linux
`drivers/hid/hid-logitech-hidpp.c` and Solaar's `hidpp20.py` / `centurion.py` —
rather than assumptions. Where a device reports only levels or only voltage, the
percentage is approximate and the CLI marks it `（推算）`.

Parsing lives in a pure, IO-free layer (`src/battery.cpp`) so it can be tested
without hardware.

## Build

On Windows with Visual Studio 2022 C++ tools and the Windows SDK:

```bat
cd native
build.bat
```

The executable is written to `native/build/mouse-tray.exe`. Both app editions copy
it into their output at build time; the WPF project previously relied on a manual,
stale copy, which meant rebuilt readers did not reach the shipped package.

## Commands

```bat
build\mouse-tray.exe --once         :: read one battery snapshot
build\mouse-tray.exe --all          :: like --once, but list every device on a receiver
build\mouse-tray.exe --test         :: run HID++ protocol self-test
build\mouse-tray.exe --test-battery :: run offline battery-parse unit tests
build\mouse-tray.exe --probe        :: list the battery features each device exposes
build\mouse-tray.exe --dump-frames  :: print protocol test frames
build\mouse-tray.exe --help         :: show command usage
```

Running the executable with no arguments is equivalent to `--once`.

`--once` (the path the app polls with) stops at the first device on each receiver.
The app spawns a **fresh process every poll**, so slot-discovery state cannot carry
across polls; scanning the remaining slots would add roughly 0.7 s of empty-slot
probe timeouts per poll for the common single-device case, and the app only uses
the first device's reading anyway. Measured `--once` time is unchanged.
`--all` and `--probe` do enumerate every slot.

`--test-battery` needs no hardware and covers all five features, both `0x1004`
reporting modes, level masking, charge stages, voltage interpolation boundaries,
truncated frames and clamping.

`--probe` is the tool to reach for when a specific model reads as offline: it
shows which battery features that device actually implements.

Frame construction is covered by `tools/verify_frames.py`, which re-derives the
expected 20-byte frames from the spec and diffs them against `--dump-frames`.
