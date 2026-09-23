# Native HID++ reader

The desktop tray interface is the WPF application in `wpf/`. This native executable is its HID++ helper: `BatteryService` invokes `mouse-tray.exe --once` and parses the device name, battery percentage, and charging state from stdout.

The reader dynamically enumerates Logitech HID devices, queries `0x1004` (UnifiedBattery) first, and falls back to `0x1000` (BatteryStatus) when needed. The fallback covers devices such as the G304 that do not expose UnifiedBattery.

## Build

On Windows with Visual Studio 2022 C++ tools and the Windows SDK:

```bat
cd native
build.bat
```

The executable is written to `native/build/mouse-tray.exe`.

## Commands

```bat
build\mouse-tray.exe --once         :: read one battery snapshot
build\mouse-tray.exe --test         :: run HID++ protocol self-test
build\mouse-tray.exe --dump-frames  :: print protocol test frames
build\mouse-tray.exe --help         :: show command usage
```

Running the executable with no arguments is equivalent to `--once`.
