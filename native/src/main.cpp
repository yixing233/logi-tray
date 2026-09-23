// ============================================================================
//  main.cpp — 原生版 mouse-tray 入口
//
//  当前阶段（G1）：先打通 HID++ 协议层，能读到真实电量。
//  后续阶段会加入历史/预测、托盘、材质渲染、浮窗与设置窗口。
// ============================================================================

#include "alerts.h"
#include "app.h"
#include "battery_history.h"
#include "capture.h"
#include "config.h"
#include "glass_renderer.h"
#include "hidpp.h"
#include "popup.h"
#include "settings_window.h"
#include "tray.h"
#include "util.h"

#include <windows.h>

#include <cstring>
#include <string>
#include <vector>

namespace {

void PrintUsage() {
    util::Print(
        u8"原生版鼠标电量托盘程序\n"
        u8"\n"
        u8"用法:\n"
        u8"  mouse-tray.exe --once     读一次电量并打印\n"
        u8"  mouse-tray.exe --test     运行协议层自检\n"
        u8"  mouse-tray.exe --debug    打印调试信息\n"
        u8"  mouse-tray.exe --dump-frames   输出 HID++ 帧（供与 Python 对拍）\n"
        u8"  mouse-tray.exe --test-alerts   通知策略自检\n"
        u8"  mouse-tray.exe --test-gpu       GPU 材质渲染自检\n"
        u8"  mouse-tray.exe --test-capture   实时抓屏自检\n"
        u8"  mouse-tray.exe --test-realtime  实时跟随验证\n"
        u8"  mouse-tray.exe --test-tray      托盘图标与菜单自检\n"
        u8"  mouse-tray.exe --test-config [--config <路径>]\n"
        u8"  mouse-tray.exe --test-popup     详情浮窗自检\n"
        u8"  mouse-tray.exe --show-details   启动并立即显示详情浮窗\n"
        u8"  mouse-tray.exe --show-settings  启动并立即打开设置窗口\n"
        u8"  mouse-tray.exe --test-settings 设置窗口自检\n"
        u8"  mouse-tray.exe --test-history  电量历史与聚合自检\n"
        u8"  mouse-tray.exe --dump-settings --out <bmp> [--material M]\n"
        u8"  mouse-tray.exe --dump-popup --out <bmp> [--percent N] [--charging] [--stale] [--dark] [--material M]\n"
        u8"  mouse-tray.exe --dump-material <liquid|acrylic|mica> --out <bmp>\n"
        u8"  mouse-tray.exe --estimate --history <路径> --percent <N>\n"
        u8"  mouse-tray.exe            启动托盘程序（默认）\n");
}

int RunOnce() {
    util::Print(u8"读取电量...\n");
    const double t0 = util::NowSec();
    const auto readings = hidpp::ReadAll(2.0);
    const double dt = util::NowSec() - t0;

    if (readings.empty()) {
        util::Print(u8"未读到电量（鼠标可能正在休眠，动一下再试）"
                    u8"  耗时 %.0f ms\n", dt * 1000);
        return 2;
    }
    for (const auto& r : readings) {
        util::Print(u8"%s: %d%% · %s", r.name.c_str(), r.percent,
                    r.chargingText.c_str());
        if (!r.level.empty())
            util::Print(u8" · %s", r.level.c_str());
        util::Print(u8"\n");
    }
    util::Print(u8"耗时 %.0f ms\n", dt * 1000);
    return 0;
}

int RunDebug() {
    util::Print(u8"=== 调试信息 ===\n");
    util::Print(u8"数据目录: %s\n", util::DataDirUtf8().c_str());
    util::Print(u8"进程位数: %s\n",
                sizeof(void*) == 8 ? u8"64 位" : u8"32 位");
    return hidpp::RunSelfTest();
}

// 供对拍测试使用：从指定 history 文件算预测并输出成 key=value 形式。
// 输出格式刻意做成易解析的，便于与 Python 版做数值比对。
//   rate=2.00 hours=42.0 span=6.0 drop=12 samples=13 conf=高
// 数据不足时输出 "NONE"。
int RunEstimate(const std::string& historyPath, int percent) {
    battery::History h(historyPath);
    battery::Estimate est;
    if (!h.EstimateRemaining(percent, est)) {
        util::Print("NONE\n");
        return 0;
    }
    util::Print("rate=%.2f hours=%.1f span=%.1f drop=%d samples=%d conf=%s\n",
                est.ratePerHour, est.hoursRemaining, est.spanHours,
                est.dropPercent, est.sampleCount, est.confidence.c_str());
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    util::InitConsole();

    bool once = false, test = false, debug = false, estimate = false;
    bool testConfig = false, showDetails = false, dumpSettings = false;
    bool showSettings = false;
    bool dumpPopup = false, dumpCharging = false, dumpStale = false;
    bool dumpDark = false;
    int dumpPercent = 94;
    std::string dumpMaterial2 = "acrylic";
    std::string dumpBackdrop = "stripes";
    std::string dumpMaterial, dumpOut;
    std::string configPath;
    std::string estimateHistory;
    int estimatePercent = -1;

    for (int i = 1; i < argc; ++i) {
        const char* a = argv[i];
        if (std::strcmp(a, "--once") == 0) once = true;
        else if (std::strcmp(a, "--test") == 0) test = true;
        else if (std::strcmp(a, "--debug") == 0) debug = true;
        else if (std::strcmp(a, "--estimate") == 0) {
            // 纯开关，路径由 --history 给出。
            // 早先这里顺手吃掉下一个参数，导致 `--estimate --history X`
            // 把 "--history" 当成了路径，真正的路径反被当成未知参数。
            estimate = true;
        }
        else if (std::strcmp(a, "--history") == 0) {
            if (i + 1 < argc) estimateHistory = argv[++i];
        }
        else if (std::strcmp(a, "--percent") == 0) {
            // 注意：这个参数**同时**给 --estimate 与 --dump-popup 用。
            // 早先写了两份处理分支，第一个分支永远先命中并吃掉参数，
            // 第二个永远不执行 —— 结果 --dump-popup --percent 15 拿到的
            // 仍是默认值 94，三种状态渲染出来一模一样（看图才发现）。
            if (i + 1 < argc) {
                const int v = std::atoi(argv[++i]);
                estimatePercent = v;
                dumpPercent = v;
            }
        }
        else if (std::strcmp(a, "--dump-frames") == 0) {
            // 帧对拍：输出格式与 Python 版一致，供自动化 diff。
            // 这条**不需要鼠标在线**就能验证协议层正确性。
            return hidpp::DumpFrames();
        }
        else if (std::strcmp(a, "--test-alerts") == 0) {
            return alerts::RunSelfTest();
        }
        else if (std::strcmp(a, "--test-gpu") == 0) {
            return gpu::RunSelfTest();
        }
        else if (std::strcmp(a, "--test-capture") == 0) {
            return capture::RunSelfTest();
        }
        else if (std::strcmp(a, "--test-realtime") == 0) {
            return capture::RunRealtimeTest();
        }
        else if (std::strcmp(a, "--test-tray") == 0) {
            return tray::RunSelfTest();
        }
        else if (std::strcmp(a, "--test-popup") == 0) {
            return popup::RunSelfTest();
        }
        else if (std::strcmp(a, "--test-settings") == 0) {
            return settingswin::RunSelfTest();
        }
        else if (std::strcmp(a, "--test-history") == 0) {
            return battery::RunSelfTest();
        }
        else if (std::strcmp(a, "--dump-settings") == 0) {
            dumpSettings = true;
        }
        else if (std::strcmp(a, "--dump-popup") == 0) {
            dumpPopup = true;
        }
        else if (std::strcmp(a, "--show-details") == 0) {
            showDetails = true;
        }
        else if (std::strcmp(a, "--show-settings") == 0) {
            showSettings = true;
        }
        else if (std::strcmp(a, "--charging") == 0) {
            dumpCharging = true;
        }
        else if (std::strcmp(a, "--stale") == 0) {
            dumpStale = true;
        }
        else if (std::strcmp(a, "--dark") == 0) {
            dumpDark = true;
        }
        else if (std::strcmp(a, "--material") == 0) {
            if (i + 1 < argc) dumpMaterial2 = argv[++i];
        }
        else if (std::strcmp(a, "--backdrop") == 0) {
            if (i + 1 < argc) dumpBackdrop = argv[++i];
        }
        else if (std::strcmp(a, "--config") == 0) {
            if (i + 1 < argc) configPath = argv[++i];
        }
        else if (std::strcmp(a, "--test-config") == 0) {
            testConfig = true;
        }
        else if (std::strcmp(a, "--dump-material") == 0) {
            if (i + 1 < argc) dumpMaterial = argv[++i];
        }
        else if (std::strcmp(a, "--out") == 0) {
            if (i + 1 < argc) dumpOut = argv[++i];
        }
        else if (std::strcmp(a, "--help") == 0 ||
                 std::strcmp(a, "-h") == 0) { PrintUsage(); return 0; }
        else {
            util::Print(u8"未知参数: %s\n\n", a);
            PrintUsage();
            return 1;
        }
    }

    if (!dumpMaterial.empty()) {
        if (dumpOut.empty()) dumpOut = "material.bmp";
        return gpu::DumpMaterial(dumpMaterial, dumpOut, "") ? 0 : 1;
    }
    if (dumpSettings) {
        const std::string path = dumpOut.empty() ? "settings.bmp" : dumpOut;
        std::string e;
        return settingswin::DumpSettings(path, dumpMaterial2, e) ? 0 : 1;
    }
    if (dumpPopup) {
        popup::DumpOptions o;
        o.percent = dumpPercent;
        o.charging = dumpCharging;
        o.stale = dumpStale;
        o.dark = dumpDark;
        o.material = dumpMaterial2;
        o.outPath = dumpOut.empty() ? "popup.bmp" : dumpOut;
        o.historyPath = estimateHistory;   // 复用 --history
        o.backdrop = dumpBackdrop;
        std::string e;
        return popup::DumpPopup(o, e) ? 0 : 1;
    }
    if (testConfig) {
        config::Settings s;
        s.Load(configPath.empty() ? config::DefaultPath() : configPath);
        util::Print("effective|interval=%d low_threshold=%d "
                    "critical_threshold=%d stale_grace=%d material=%s "
                    "notify_enabled=%d realtime=%d realtime_fps=%d\n",
                    s.interval, s.lowThreshold, s.criticalThreshold,
                    s.staleGrace, s.material.c_str(),
                    s.notifyEnabled ? 1 : 0, s.realtime ? 1 : 0,
                    s.realtimeFps);
        return 0;
    }
    if (estimate) {
        if (estimateHistory.empty()) {
            util::Print(u8"--estimate 需要 --history <路径>\n");
            return 1;
        }
        if (estimatePercent < 0) estimatePercent = 50;
        return RunEstimate(estimateHistory, estimatePercent);
    }
    if (test) return hidpp::RunSelfTest();
    if (debug) return RunDebug();
    if (once) return RunOnce();

    // 默认：启动托盘应用
    return app::RunTrayApp(showDetails, showSettings);
}
