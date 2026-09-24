// ============================================================================
//  main.cpp — HID++ reader CLI used by the WPF application
// ============================================================================

#include "battery.h"
#include "hidpp.h"
#include "util.h"

#include <cstring>

namespace {

void PrintUsage() {
    util::Print(
        u8"Logitech HID++ battery reader\n\n"
        u8"Usage:\n"
        u8"  mouse-tray.exe [--once]        Read battery and print one snapshot\n"
        u8"  mouse-tray.exe --all           Like --once but lists every device on a receiver\n"
        u8"  mouse-tray.exe --test          Run HID++ protocol self-test\n"
        u8"  mouse-tray.exe --test-battery  Run offline battery-parse unit tests\n"
        u8"  mouse-tray.exe --probe         List every battery feature each device exposes\n"
        u8"  mouse-tray.exe --dump-frames   Print protocol test frames\n"
        u8"  mouse-tray.exe --help          Show this help\n");
}

int RunOnce(bool allDevices = false) {
    // 默认只读每个接收器上的第一台设备：应用以 --once 每轮新起进程，
    // 槽位发现缓存无法保留，继续扫空槽位会平白增加每次轮询的耗时。
    // --all 用于枚举同一接收器上的多台设备。
    const auto readings = hidpp::ReadAll(2.0, allDevices);
    if (readings.empty()) {
        util::Print(u8"未读到电量（鼠标可能正在休眠，动一下再试）\n");
        return 2;
    }

    for (const auto& reading : readings) {
        util::Print(u8"%s: %d%% · %s", reading.name.c_str(),
                    reading.percent, reading.chargingText.c_str());
        if (!reading.level.empty())
            util::Print(u8" · %s", reading.level.c_str());
        // 百分比若是推算出来的（设备只报档位或只有电压），标注出来，
        // 免得使用者以为这是设备直报的精确值。
        if (reading.percentInferred)
            util::Print(u8"（推算）");
        util::Print(u8"\n");
    }
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    util::InitConsole();

    if (argc == 1 || (argc == 2 && std::strcmp(argv[1], "--once") == 0))
        return RunOnce();
    if (argc == 2 && (std::strcmp(argv[1], "--help") == 0 ||
                      std::strcmp(argv[1], "-h") == 0)) {
        PrintUsage();
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "--test") == 0)
        return hidpp::RunSelfTest();
    if (argc == 2 && std::strcmp(argv[1], "--test-battery") == 0)
        return battery::RunParseTests();
    if (argc == 2 && std::strcmp(argv[1], "--probe") == 0)
        return hidpp::ProbeFeatures();
    if (argc == 2 && std::strcmp(argv[1], "--all") == 0)
        return RunOnce(/*allDevices=*/true);
    if (argc == 2 && std::strcmp(argv[1], "--dump-frames") == 0)
        return hidpp::DumpFrames();

    util::Print(u8"未知参数\n\n");
    PrintUsage();
    return 1;
}
