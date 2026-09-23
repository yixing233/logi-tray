// ============================================================================
//  tray.h — 托盘图标与原生菜单
//
//  从已验证的 Python 版 (icon_render.py + mouse_tray.py 的菜单部分) 移植。
//
//  托盘图标实际只有 16x16 像素（高 DPI 下 20/24/32），要在这么小的画布上
//  同时表达"电量数字"和"是否充电"，只能靠极简造型：
//    * 主视觉是数字本身（粗体、撑满高度），而不是画个电池再往里塞字
//    * 底部一道横条表示电量比例，兼作状态色
//    * 充电时数字用蓝色，并把底条画满
//    * 浅色/深色任务栏使用不同前景色，保证对比度
//  所有绘制先在 4 倍画布上完成再缩小，避免小图标锯齿。
// ============================================================================
#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace tray {

// 状态色（与 Python 版一致）
constexpr uint32_t kColorOk       = 0x18944C;   // 24,148,76
constexpr uint32_t kColorMid      = 0xC89218;   // 200,146,24
constexpr uint32_t kColorLow      = 0xC83430;   // 200,52,48
constexpr uint32_t kColorCharging = 0x0076C8;   // 0,118,200
constexpr uint32_t kColorUnknown  = 0x848488;   // 132,132,136

constexpr int kSupersample = 4;

// 按电量与充电状态选颜色（percent < 0 表示未知）
uint32_t LevelColor(int percent, bool charging);

// 画出托盘图标，返回 HICON（调用方负责 DestroyIcon）。
//
//   percent   电量百分比；< 0 表示从未读到，显示 "--"
//   charging  是否充电中
//   darkTaskbar 深色任务栏（用浅色前景）
//   stale     是否为"最后一次已知读数"：仍显示数字，但用中性灰 +
//             空心比例条，让人一眼看出不是实时值。
//             这比直接显示 "--" 有用得多 —— 鼠标休眠时电量本来也不会变。
void* RenderIcon(int percent, bool charging, bool darkTaskbar, int size,
                 bool stale = false);

// 构造悬停提示（Windows 托盘 tooltip 上限 127 字符）
std::string BuildTooltip(const std::string& name, int percent,
                         const std::string& chargingText,
                         const std::string& level, int voltageMv,
                         double hoursRemaining, double lastSeenEpoch,
                         bool stale);

// ---------------------------------------------------------------- 托盘图标

// 托盘图标的状态
struct TrayState {
    int percent = -1;
    bool charging = false;
    bool stale = false;          // 显示的是历史读数
    std::string name;
    std::string chargingText;
    std::string level;
    int voltageMv = 0;
    double hoursRemaining = -1;
    double lastSeenEpoch = 0;
    std::string tooltip;
};

// 菜单项类型
struct MenuItem {
    enum class Kind { Normal, Separator, Submenu };
    Kind kind = Kind::Normal;
    std::string label;
    uint32_t id = 0;             // 命令 ID（0 表示无）
    bool checked = false;
    bool radio = false;
    bool enabled = true;
    std::vector<MenuItem> children;   // 子菜单
};

class TrayIcon {
public:
    TrayIcon() = default;
    ~TrayIcon();

    TrayIcon(const TrayIcon&) = delete;
    TrayIcon& operator=(const TrayIcon&) = delete;

    // 创建隐藏消息窗口并添加托盘图标。
    //   onClick 左键单击（弹详情）
    //   onRightClick 右键单击（弹菜单）
    //   onCommand 菜单命令（返回 id）
    using ClickFn = std::function<void()>;
    using CommandFn = std::function<void(uint32_t)>;

    bool Create(const std::string& tooltip, ClickFn onClick,
                ClickFn onDoubleClick, CommandFn onCommand,
                std::string& err);
    void Destroy();

    // 更新图标与提示
    void Update(const TrayState& st, bool darkTaskbar);

    // 设置当前菜单（右键时弹出它）
    void SetMenu(std::vector<MenuItem> menu) { currentMenu_ = std::move(menu); }

    // 弹出右键菜单。menu 是顶层项列表。
    void ShowMenu(const std::vector<MenuItem>& menu, int x, int y);

    // 在当前光标处弹出已设置的菜单
    void ShowMenuAtCursor();

    // 处理消息（由外部消息循环调用）。返回是否已处理。
    //
    // 注意：**托盘回调不是队列消息**。Shell_NotifyIcon 用 SendMessage 直接把
    // 回调投给窗口过程，因此它不会出现在 PeekMessage 队列里，本函数在真实
    // 点击时根本不会被调用。真正的处理入口是 TrayIcon::OnCallback()，
    // 由窗口过程调用。本函数保留是为了兼容被显式 PostMessage 的情况
    // （测试会这么做），不要把它当成主路径。
    bool ProcessMessage(void* msg);

    // 托盘回调的**真正入口**：由窗口过程收到 kTrayCallbackMsg 时调用。
    //   v4: wParam 低字 x、高字 y；lParam 低字事件码、高字图标 ID
    void OnCallback(uintptr_t wparam, intptr_t lparam);

    // 供自检：把 v4 标志设为指定值，并注入计数回调，验证事件分发。
    // 这是修复"点击无反应 / 点击触发两次"后补的**纯逻辑**回归测试。
    void SetV4ForTest(bool v4) { v4_ = v4; }
    bool v4ForTest() const { return v4_; }
    void SetClickCounterForTest(int* counter) {
        onClick_ = [counter]() { if (counter) ++(*counter); };
    }
    void SetDoubleClickCounterForTest(int* counter) {
        onDoubleClick_ = [counter]() { if (counter) ++(*counter); };
    }

    void* hwnd() const { return hwnd_; }
    bool created() const { return created_; }
    int lastError() const { return lastError_; }

private:
    void* hwnd_ = nullptr;
    void* icon_ = nullptr;
    bool created_ = false;
    // NIM_SETVERSION(v4) 是否成功。成功后**只能**按 v4 事件码分发，
    // 否则同一次点击会因收到新旧两套事件而被处理两次。
    bool v4_ = false;
    int lastError_ = 0;
    ClickFn onClick_;
    ClickFn onDoubleClick_;
    CommandFn onCommand_;
    std::vector<MenuItem> currentMenu_;
};

// ---------------------------------------------------------------- 自检

// 验证：图标能生成（多种状态）、像素内容合理、tooltip 长度合规
int RunSelfTest();

}  // namespace tray
