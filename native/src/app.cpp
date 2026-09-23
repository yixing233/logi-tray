// ============================================================================
//  app.cpp — 托盘应用主体
//
//  逐条对应 Python 版 mouse_tray.py 的行为。关键设计（都是踩坑得来的）：
//
//   1. **读不到不等于归零**：鼠标休眠时 read_all 返回空，此时必须保留
//      "最后一次已知读数"并标记为 stale，而不是显示"--"。
//      鼠标休眠时电量本来也不会变，显示历史值比显示"--"有用得多。
//
//   2. **超过宽限期才真正离线**：stale_grace 默认 300 秒。刚读不到就标
//      离线会让图标频繁闪烁。
//
//   3. **轮询在工作线程里跑**：读一次电量最坏 1.5 秒（设备唤醒），
//      放在消息循环里会让菜单卡住/无响应。
//
//   4. **消息循环不阻塞**：用 PeekMessage 而不是 GetMessage，
//      这样可以在空闲时处理定时逻辑。
// ============================================================================

#include "app.h"
#include "alerts.h"
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
#include <shellapi.h>   // NOTIFYICONDATAW / NIF_INFO / ShellExecuteW

#include <algorithm>
#include <atomic>
#include <cmath>
#include <string>
#include <thread>
#include <vector>

namespace app {

namespace {

// 菜单命令 ID（固定值，便于分发）
enum : uint32_t {
    kCmdDetails = 1,
    kCmdRefresh,
    kCmdSettings,
    kCmdIntervalBase = 100,     // +index
    kCmdLowBase = 200,          // +index
    kCmdCriticalBase = 300,     // +index
    kCmdMaterialBase = 400,     // +index（已弃用：只保留亚克力）
    kCmdToggleNotify = 500,
    kCmdToggleRealtime,
    kCmdToggleAutostart,
    kCmdOpenDataDir,
    kCmdQuit,
};

const int kIntervals[] = {10, 15, 30, 60, 120, 300};
const int kLowValues[] = {10, 15, 20, 25, 30, 40, 50};
const int kCriticalValues[] = {3, 5, 8, 10, 15};

struct State {
    config::Settings cfg;
    battery::History* history = nullptr;
    alerts::Notifier* notifier = nullptr;
    tray::TrayIcon* trayIcon = nullptr;

    // 最后一次成功的读数
    bool haveReading = false;
    hidpp::Reading last;
    double lastSeenEpoch = 0;

    // 最新显示值（可能是历史值）
    int shownPercent = -1;
    bool shownCharging = false;
    bool shownStale = false;

    popup::Popup* popup = nullptr;
    double popupShownAt = 0;
    settingswin::SettingsWindow* settings = nullptr;

    std::atomic<bool> quit{false};
    std::atomic<bool> refreshNow{false};
    double nextPollAt = 0;
};

State* g_state = nullptr;

// ---------------------------------------------------------------- 开机自启

const wchar_t* kRunKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
const wchar_t* kRunValue = L"MouseBatteryTrayNative";

std::wstring ExePath() {
    wchar_t buf[MAX_PATH] = {0};
    GetModuleFileNameW(nullptr, buf, MAX_PATH);
    return buf;
}

bool AutostartEnabled() {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKey, 0, KEY_READ, &key)
        != ERROR_SUCCESS)
        return false;
    wchar_t buf[1024] = {0};
    DWORD size = sizeof(buf), type = 0;
    const LONG rc = RegQueryValueExW(key, kRunValue, nullptr, &type,
                                     reinterpret_cast<LPBYTE>(buf), &size);
    RegCloseKey(key);
    return rc == ERROR_SUCCESS && size > 2;
}

bool SetAutostart(bool on) {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKey, 0, KEY_SET_VALUE, &key)
        != ERROR_SUCCESS)
        return false;
    bool ok = false;
    if (on) {
        const std::wstring cmd = L"\"" + ExePath() + L"\"";
        ok = RegSetValueExW(key, kRunValue, 0, REG_SZ,
                            reinterpret_cast<const BYTE*>(cmd.c_str()),
                            static_cast<DWORD>((cmd.size() + 1)
                                               * sizeof(wchar_t)))
             == ERROR_SUCCESS;
    } else {
        const LONG rc = RegDeleteValueW(key, kRunValue);
        ok = (rc == ERROR_SUCCESS || rc == ERROR_FILE_NOT_FOUND);
    }
    RegCloseKey(key);
    return ok;
}

// ---------------------------------------------------------------- 通知

// 用 Shell_NotifyIcon 的气泡通知（原生、无需额外依赖）
void ShowBalloon(const std::string& title, const std::string& message) {
    if (!g_state || !g_state->trayIcon || !g_state->trayIcon->hwnd()) return;

    NOTIFYICONDATAW nid{};
    nid.cbSize = sizeof(nid);
    nid.hWnd = static_cast<HWND>(g_state->trayIcon->hwnd());
    nid.uID = 1;
    nid.uFlags = NIF_INFO;
    nid.dwInfoFlags = NIIF_INFO;
    const std::wstring wt = util::Utf8ToWide(title);
    const std::wstring wm = util::Utf8ToWide(message);
    wcsncpy_s(nid.szInfoTitle, wt.c_str(), _TRUNCATE);
    wcsncpy_s(nid.szInfo, wm.c_str(), _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &nid);
}

// ---------------------------------------------------------------- 刷新

// 读一次电量并更新界面。返回是否读到新值。
bool RefreshOnce(bool verbose) {
    State* st = g_state;
    if (!st) return false;

    const auto readings = hidpp::ReadAll(1.5);
    const double now = battery::NowUnix();

    if (!readings.empty()) {
        st->last = readings.front();
        st->haveReading = true;
        st->lastSeenEpoch = now;
        st->shownStale = false;

        // 记录历史（相同秒内的重复读数会被 History 内部忽略）
        if (st->history) {
            st->history->Add(st->last.percent,
                             st->last.chargingState != 0, now);
            st->history->Save();
        }

        // 通知判定
        if (st->notifier && st->cfg.notifyEnabled) {
            battery::Estimate est;
            const bool hasEst = st->history &&
                st->history->EstimateRemaining(st->last.percent, est);
            const auto v = st->notifier->Evaluate(
                st->last.percent, st->last.chargingState != 0,
                hasEst ? est.hoursRemaining : -1.0);
            if (v.fire) ShowBalloon(v.title, v.message);
        }
        if (verbose) {
            util::Print(u8"%s：%d%% · %s\n", st->last.name.c_str(),
                        st->last.percent, st->last.chargingText.c_str());
        }
        return true;
    }

    // 读不到：保留最后读数。超过宽限期才真正标为离线。
    if (st->haveReading) {
        const double age = now - st->lastSeenEpoch;
        st->shownStale = age >= st->cfg.staleGrace;
    } else {
        st->shownStale = true;
    }
    if (verbose) {
        util::Print(u8"未读到电量（鼠标可能休眠）%s\n",
                    st->haveReading ? u8"——保留最后读数" : "");
    }
    return false;
}

// 计算当前应显示的状态并更新托盘图标
void UpdateTray() {
    State* st = g_state;
    if (!st || !st->trayIcon) return;

    tray::TrayState ts;
    if (st->haveReading) {
        ts.percent = st->last.percent;
        ts.charging = st->last.chargingState != 0;
        ts.stale = st->shownStale;
        ts.name = st->last.name;
        ts.chargingText = st->last.chargingText;
        ts.level = st->last.level;
        ts.voltageMv = st->last.voltageMv;
        ts.lastSeenEpoch = st->lastSeenEpoch;

        battery::Estimate est;
        if (st->history && st->history->EstimateRemaining(ts.percent, est))
            ts.hoursRemaining = est.hoursRemaining;
    } else {
        ts.percent = -1;
        ts.stale = true;
    }
    ts.tooltip = tray::BuildTooltip(
        ts.name, ts.percent, ts.chargingText, ts.level, ts.voltageMv,
        ts.hoursRemaining, ts.lastSeenEpoch, ts.stale);

    // 浅色/深色任务栏：读注册表 SystemUsesLightTheme
    bool lightTheme = true;
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(HKEY_CURRENT_USER,
                          L"Software\\Microsoft\\Windows\\CurrentVersion\\"
                          L"Themes\\Personalize",
                          0, KEY_READ, &key) == ERROR_SUCCESS) {
            DWORD val = 1, size = sizeof(val), type = 0;
            if (RegQueryValueExW(key, L"SystemUsesLightTheme", nullptr, &type,
                                 reinterpret_cast<LPBYTE>(&val),
                                 &size) == ERROR_SUCCESS)
                lightTheme = (val != 0);
            RegCloseKey(key);
        }
    }
    // 浅色任务栏 -> 用深色前景（dark_taskbar=false）
    st->trayIcon->Update(ts, /*darkTaskbar=*/!lightTheme);
}

// ---------------------------------------------------------------- 详情浮窗

// 由当前读数与历史预测组装浮窗内容
popup::Content MakePopupContent() {
    State* st = g_state;
    popup::Content c;
    if (!st) return c;

    if (st->haveReading) {
        c.percent = st->last.percent;
        c.charging = st->last.chargingState != 0;
        c.name = st->last.name;
        c.chargingText = st->last.chargingText;
        c.level = st->last.level;
        c.voltageMv = st->last.voltageMv;
        c.stale = st->shownStale;
        c.lastSeenEpoch = st->lastSeenEpoch;

        battery::Estimate est;
        if (st->history &&
            st->history->EstimateRemaining(c.percent, est)) {
            c.hoursRemaining = est.hoursRemaining;
            c.rate = est.ratePerHour;
            c.confidence = est.confidence;
        }

        // ---- 按小时的电量 + 简单统计
        if (st->history) {
            const int kWindow = 24;
            const auto buckets = st->history->RecentHours(kWindow);
            c.hours.reserve(buckets.size());
            for (const auto& b : buckets) {
                popup::Content::HourPoint hp;
                hp.hourStart = b.hourStart;
                hp.count = b.count;
                // 用该小时的最后读数作为该时段的代表值
                hp.percent = (b.count > 0) ? b.lastPercent : -1;
                c.hours.push_back(hp);
            }
            const auto sum = st->history->Analyze(kWindow);
            c.windowHours = kWindow;
            c.hoursWithData = sum.hoursWithData;
            c.minPercent = (sum.sampleCount > 0) ? sum.minPercent : -1;
            c.maxPercent = (sum.sampleCount > 0) ? sum.maxPercent : -1;
            c.chargeSamples = sum.chargedSamples;
        }
    } else {
        c.percent = -1;
        c.stale = true;
    }
    return c;
}

// 浮窗位置：优先放在托盘上方（右下角），找不到就放屏幕中央偏下
void ComputePopupPos(int& x, int& y) {
    HWND tray = FindWindowW(L"Shell_TrayWnd", nullptr);
    int screenW = GetSystemMetrics(SM_CXSCREEN);
    int screenH = GetSystemMetrics(SM_CYSCREEN);
    if (tray) {
        RECT tr{};
        GetWindowRect(tray, &tr);
        // 面板画布比面板本体大 SHADOW_PAD，所以右边多留一点
        x = tr.right - popup::kCanvasW - popup::kShadowPad - 12;
        y = tr.top - popup::kCanvasH - 8;
    } else {
        x = screenW - popup::kCanvasW - 24;
        y = screenH - popup::kCanvasH - 80;
    }
    if (x < 0) x = 0;
    if (y < 0) y = 0;
}

void TogglePopup() {
    State* st = g_state;
    if (!st || !st->popup) return;

    if (st->popup->visible()) {
        st->popup->Close();
        return;
    }

    popup::Popup::Options opt;
    ComputePopupPos(opt.x, opt.y);
    opt.material = st->cfg.material;
    opt.realtime = st->cfg.realtime;
    opt.fps = st->cfg.realtimeFps;
    opt.autoCloseMs = 12000;

    // 深色主题跟随系统
    {
        HKEY key = nullptr;
        bool light = true;
        if (RegOpenKeyExW(HKEY_CURRENT_USER,
                          L"Software\\Microsoft\\Windows\\CurrentVersion\\"
                          L"Themes\\Personalize", 0, KEY_READ, &key)
            == ERROR_SUCCESS) {
            DWORD val = 1, size = sizeof(val), type = 0;
            if (RegQueryValueExW(key, L"AppsUseLightTheme", nullptr, &type,
                                 reinterpret_cast<LPBYTE>(&val), &size)
                == ERROR_SUCCESS)
                light = (val != 0);
            RegCloseKey(key);
        }
        opt.dark = !light;
    }

    std::string err;
    if (!st->popup->Show(MakePopupContent(), opt, err)) {
        util::Print(u8"显示详情浮窗失败：%s\n", err.c_str());
    }
    st->popupShownAt = util::NowSec();
}

// 打开设置窗口。保存时写盘并同步到通知器。
void OpenSettings() {
    State* st = g_state;
    if (!st || !st->settings) return;

    if (st->settings->visible()) {
        // 已打开则置前
        SetForegroundWindow(static_cast<HWND>(st->settings->hwnd()));
        return;
    }

    st->settings->setOnSave([](const config::Settings& s) {
        State* st2 = g_state;
        if (!st2) return;
        st2->cfg = s;
        st2->cfg.Save();
        // 阈值变了要重新武装通知，让新阈值立即生效
        if (st2->notifier)
            st2->notifier->SetThresholds(s.lowThreshold,
                                         s.criticalThreshold);
        util::Print(u8"设置已保存：阈值 %d%%/%d%%，间隔 %d 秒，材质 %s\n",
                    s.lowThreshold, s.criticalThreshold, s.interval,
                    s.material.c_str());
    });

    std::string err;
    if (!st->settings->Show(st->cfg, err)) {
        util::Print(u8"打开设置窗口失败：%s\n", err.c_str());
        MessageBoxW(nullptr, util::Utf8ToWide(err).c_str(), L"设置",
                    MB_OK | MB_ICONWARNING);
    }
}

// ---------------------------------------------------------------- 菜单

std::vector<tray::MenuItem> BuildMenu() {
    State* st = g_state;
    std::vector<tray::MenuItem> menu;

    auto item = [](const char* label, uint32_t id, bool checked = false,
                   bool radio = false) {
        tray::MenuItem m;
        m.label = label;
        m.id = id;
        m.checked = checked;
        m.radio = radio;
        return m;
    };
    auto sep = []() {
        tray::MenuItem m;
        m.kind = tray::MenuItem::Kind::Separator;
        return m;
    };
    auto submenu = [](const char* label,
                      std::vector<tray::MenuItem> children) {
        tray::MenuItem m;
        m.kind = tray::MenuItem::Kind::Submenu;
        m.label = label;
        m.children = std::move(children);
        return m;
    };

    menu.push_back(item(u8"查看详情", kCmdDetails));
    menu.push_back(item(u8"立即刷新", kCmdRefresh));
    menu.push_back(sep());
    menu.push_back(item(u8"设置…", kCmdSettings));
    menu.push_back(sep());

    // 低电量提醒
    {
        std::vector<tray::MenuItem> kids;
        for (size_t i = 0; i < sizeof(kLowValues) / sizeof(int); ++i) {
            char buf[32];
            std::snprintf(buf, sizeof(buf), "%d%%", kLowValues[i]);
            kids.push_back(item(buf, kCmdLowBase + static_cast<uint32_t>(i),
                                st->cfg.lowThreshold == kLowValues[i], true));
        }
        menu.push_back(submenu(u8"低电量提醒", std::move(kids)));
    }
    // 严重低电量
    {
        std::vector<tray::MenuItem> kids;
        for (size_t i = 0; i < sizeof(kCriticalValues) / sizeof(int); ++i) {
            char buf[32];
            std::snprintf(buf, sizeof(buf), "%d%%", kCriticalValues[i]);
            kids.push_back(item(buf,
                                kCmdCriticalBase + static_cast<uint32_t>(i),
                                st->cfg.criticalThreshold == kCriticalValues[i],
                                true));
        }
        menu.push_back(submenu(u8"严重低电量提醒", std::move(kids)));
    }
    menu.push_back(item(u8"启用通知", kCmdToggleNotify,
                        st->cfg.notifyEnabled));
    menu.push_back(sep());

    // 刷新间隔
    {
        std::vector<tray::MenuItem> kids;
        for (size_t i = 0; i < sizeof(kIntervals) / sizeof(int); ++i) {
            char buf[32];
            if (kIntervals[i] < 60)
                std::snprintf(buf, sizeof(buf), u8"%d 秒", kIntervals[i]);
            else
                std::snprintf(buf, sizeof(buf), u8"%d 分钟",
                              kIntervals[i] / 60);
            kids.push_back(item(buf,
                                kCmdIntervalBase + static_cast<uint32_t>(i),
                                st->cfg.interval == kIntervals[i], true));
        }
        menu.push_back(submenu(u8"刷新间隔", std::move(kids)));
    }
    // 窗口材质子菜单已移除（只保留亚克力）。
    // 实时材质仍然保留：它控制的是"材质是否跟随背景实时重绘"，与具体
    // 材质种类无关。
    menu.push_back(item(u8"实时材质", kCmdToggleRealtime, st->cfg.realtime));
    menu.push_back(item(u8"开机自启", kCmdToggleAutostart,
                        AutostartEnabled()));
    menu.push_back(sep());
    menu.push_back(item(u8"打开数据目录", kCmdOpenDataDir));
    menu.push_back(item(u8"退出", kCmdQuit));
    return menu;
}

void OnCommand(uint32_t id) {
    State* st = g_state;
    if (!st) return;

    // 间隔
    if (id >= kCmdIntervalBase &&
        id < kCmdIntervalBase + sizeof(kIntervals) / sizeof(int)) {
        st->cfg.interval = kIntervals[id - kCmdIntervalBase];
        st->cfg.Save();
        st->nextPollAt = util::NowSec();
        return;
    }
    // 低电量阈值
    if (id >= kCmdLowBase &&
        id < kCmdLowBase + sizeof(kLowValues) / sizeof(int)) {
        st->cfg.lowThreshold = kLowValues[id - kCmdLowBase];
        st->cfg.Sanitise();
        st->cfg.Save();
        if (st->notifier)
            st->notifier->SetThresholds(st->cfg.lowThreshold,
                                        st->cfg.criticalThreshold);
        return;
    }
    // 严重阈值
    if (id >= kCmdCriticalBase &&
        id < kCmdCriticalBase + sizeof(kCriticalValues) / sizeof(int)) {
        st->cfg.criticalThreshold = kCriticalValues[id - kCmdCriticalBase];
        st->cfg.Sanitise();
        st->cfg.Save();
        if (st->notifier)
            st->notifier->SetThresholds(st->cfg.lowThreshold,
                                        st->cfg.criticalThreshold);
        return;
    }
    switch (id) {
        case kCmdDetails:
            TogglePopup();
            break;
        case kCmdRefresh:
            RefreshOnce(true);
            UpdateTray();
            break;
        case kCmdSettings:
            OpenSettings();
            break;
        case kCmdToggleNotify:
            st->cfg.notifyEnabled = !st->cfg.notifyEnabled;
            st->cfg.Save();
            break;
        case kCmdToggleRealtime:
            st->cfg.realtime = !st->cfg.realtime;
            st->cfg.Save();
            break;
        case kCmdToggleAutostart: {
            const bool want = !AutostartEnabled();
            if (!SetAutostart(want)) {
                MessageBoxW(nullptr, L"设置开机自启失败（无法写入注册表）",
                            L"错误", MB_OK | MB_ICONWARNING);
            } else {
                st->cfg.autostart = want;
                st->cfg.Save();
            }
            break;
        }
        case kCmdOpenDataDir: {
            const std::wstring dir = util::DataDir();
            ShellExecuteW(nullptr, L"open", dir.c_str(), nullptr, nullptr,
                          SW_SHOWNORMAL);
            break;
        }
        case kCmdQuit:
            st->quit = true;
            break;
        default:
            break;
    }
}

}  // namespace

// ---------------------------------------------------------------- 入口

int RunTrayApp(bool showDetailsOnStart, bool showSettingsOnStart) {
    config::Settings cfg;
    cfg.Load(config::DefaultPath());

    battery::History history(util::DataDirUtf8() + "\\history.json");
    alerts::Notifier notifier(util::DataDirUtf8() + "\\alert_state.json",
                              cfg.lowThreshold, cfg.criticalThreshold);

    State st;
    st.cfg = cfg;
    st.history = &history;
    st.notifier = &notifier;
    g_state = &st;

    // ---- 建托盘
    tray::TrayIcon trayIcon;
    st.trayIcon = &trayIcon;

    const std::string initialTip = tray::BuildTooltip("", -1, "", "", 0, -1,
                                                      0, true);
    std::string err;
    if (!trayIcon.Create(
            initialTip,
            []() {          // 左键单击 -> 详情
                if (g_state) OnCommand(kCmdDetails);
            },
            []() {          // 双击 -> 立即刷新
                if (g_state) OnCommand(kCmdRefresh);
            },
            [](uint32_t id) { OnCommand(id); },
            err)) {
        util::Print(u8"创建托盘图标失败：%s\n", err.c_str());
        util::Print(u8"（请确认 explorer.exe 正在运行）\n");
        return 1;
    }
    trayIcon.SetMenu(BuildMenu());

    popup::Popup details;
    st.popup = &details;

    settingswin::SettingsWindow settingsWin;
    st.settings = &settingsWin;

    // 首次读取（异步，避免启动卡顿）
    std::thread worker([&st]() {
        while (!st.quit.load()) {
            if (util::NowSec() >= st.nextPollAt || st.refreshNow.load()) {
                st.refreshNow = false;
                // 读之前先算出下次时间，避免读失败后忙转
                st.nextPollAt = util::NowSec() + st.cfg.interval;
                RefreshOnce(false);
                // 图标更新必须在 UI 线程之外也没问题（Shell_NotifyIcon 线程安全）
                UpdateTray();
            }
            Sleep(120);
        }
    });

    util::Print(u8"托盘程序已启动。右键托盘图标可打开菜单。\n");
    util::Print(u8"轮询间隔 %d 秒，材质 %s，通知 %s\n", st.cfg.interval,
                st.cfg.material.c_str(),
                st.cfg.notifyEnabled ? u8"开启" : u8"关闭");

    // 立刻做一次首轮
    st.nextPollAt = util::NowSec();

    // 集成测试用：启动后立刻显示详情浮窗
    if (showDetailsOnStart) {
        Sleep(400);          // 等首轮读取
        TogglePopup();
    }
    if (showSettingsOnStart) {
        Sleep(400);
        OpenSettings();
    }

    // ---- 消息循环（PeekMessage 便于处理退出标志）
    MSG msg;
    while (!st.quit.load()) {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) {
                st.quit = true;
                break;
            }
            // 托盘回调
            if (trayIcon.ProcessMessage(&msg)) continue;
            // 设置窗口
            if (settingsWin.ProcessMessage(&msg)) continue;
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        // 浮窗维护：自动关闭 + 定期刷新（材质跟随背景变化）
        if (st.popup && st.popup->visible()) {
            if (st.popup->Expired()) {
                st.popup->Close();
            } else {
                st.popup->Update(MakePopupContent());
            }
        }

        // 菜单内容会随配置变化，定期重建（代价很低）
        if (st.trayIcon) st.trayIcon->SetMenu(BuildMenu());
        Sleep(30);
    }

    st.quit = true;
    if (worker.joinable()) worker.join();
    details.Close();
    st.popup = nullptr;
    settingsWin.Close();
    st.settings = nullptr;
    history.Save();
    notifier.Save();
    trayIcon.Destroy();
    g_state = nullptr;
    util::Print(u8"已退出。\n");
    return 0;
}

int RunOnce() {
    const auto readings = hidpp::ReadAll(2.0);
    if (readings.empty()) {
        util::Print(u8"未读到电量（鼠标可能正在休眠，动一下再试）\n");
        return 2;
    }
    for (const auto& r : readings) {
        util::Print(u8"%s：%d%% · %s\n", r.name.c_str(), r.percent,
                    r.chargingText.c_str());
    }
    return 0;
}

}  // namespace app
