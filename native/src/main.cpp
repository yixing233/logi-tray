// ============================================================================
//  main.cpp — HID++ reader CLI used by the WPF application
// ============================================================================

#include "hidpp.h"
#include "util.h"

#include <cstring>

namespace {

void PrintUsage() {
    util::Print(
        u8"Logitech HID++ battery reader\n\n"
        u8"Usage:\n"
        u8"  mouse-tray.exe [--once]    Read battery and print one snapshot\n"
        u8"  mouse-tray.exe --test      Run HID++ protocol self-test\n"
        u8"  mouse-tray.exe --dump-frames  Print protocol test frames\n"
        u8"  mouse-tray.exe --help      Show this help\n");
}

int RunOnce() {
    const auto readings = hidpp::ReadAll(2.0);
    if (readings.empty()) {
        util::Print(u8"未读到电量（鼠标可能正在休眠，动一下再试）\n");
        return 2;
    }

    for (const auto& reading : readings) {
        util::Print(u8"%s: %d%% · %s", reading.name.c_str(),
                    reading.percent, reading.chargingText.c_str());
        if (!reading.level.empty())
            util::Print(u8" · %s", reading.level.c_str());
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
    if (argc == 2 && std::strcmp(argv[1], "--dump-frames") == 0)
        return hidpp::DumpFrames();

    util::Print(u8"未知参数\n\n");
    PrintUsage();
    return 1;
}
