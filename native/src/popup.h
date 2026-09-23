// ============================================================================
//  popup.h — 详情浮窗（分层窗口 + GPU 材质 + 实时跟随）
//
//  从已验证的 Python 版 (popup_ui.py + realtime_glass.py) 移植。
//
//  布局（画布坐标，PANEL 340x172，四周留 16px 作投影与折射采样边距）：
//    画布 = (340+32) x (172+32) = 372 x 204
//    设备名        基线 y=50    字号 12.5
//    百分比数字    基线 y=100   字号 38
//    % 号          基线 y=100   字号 16
//    比例条        y=114 高 4   从 x=40 到 x=332
//    预测          基线 y=148   字号 14
//    附加信息      基线 y=156   字号 11.5
//    「近 24 小时」 y=182
//    每小时电量柱   y=194..228
//
//  为什么用**分层窗口**：
//    材质要在窗口内部逐像素合成（含圆角外的透明与投影），普通窗口做不到。
//    WS_EX_LAYERED + UpdateLayeredWindow 是唯一能做到的方式。
//    代价：分层窗口绕过 DWM 合成，**不能用 DWM 的系统材质** ——
//    所以这里自己抓屏 + GPU 渲染材质（这也是需要 G3/G4 的原因）。
//
//  关键坑（曾导致窗口完全不可见）：
//    UpdateLayeredWindow 配 ULW_ALPHA 需要**真正的 32bpp 位图且带 alpha**。
//    用 CreateCompatibleBitmap + FillRect 得到的位图 alpha 通道是 0，
//    结果窗口**完全透明**。必须用 CreateDIBSection 建 32bpp 位图。
//    另外 UpdateLayeredWindow 要求**预乘 alpha**。
// ============================================================================
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "glass_renderer.h"

namespace popup {

// 浮窗尺寸（与 Python 版一致）
constexpr int kPanelW = 340;
constexpr int kPanelH = 244;   // 172 + 按小时电量条区域
constexpr int kPad = 24;          // 面板内边距
constexpr int kShadowPad = 32;    // 投影 / 折射采样边距
                                  // 必须让阴影在画布边缘衰减到不可见，
                                  // 否则会被裁出一条硬边。sigma=10 时
                                  // pad/sigma=3.2 -> 边缘 alpha 0.00096。
constexpr int kRadius = 8;        // Win11 浮出面板圆角
constexpr int kCanvasW = kPanelW + kShadowPad * 2;   // 372
constexpr int kCanvasH = kPanelH + kShadowPad * 2;   // 204

// 显示内容
struct Content {
    int percent = -1;             // < 0 表示未读到
    bool charging = false;
    std::string name;
    std::string chargingText;
    std::string level;
    int voltageMv = 0;
    double hoursRemaining = -1;   // < 0 表示无预测
    double rate = -1;             // 放电速度 %/小时
    std::string confidence;
    double lastSeenEpoch = 0;
    bool stale = false;

    // ---- 按小时的电量（"每个时间段的电量"）
    // 时间升序，最后一个是当前小时。空的小时会让 count == 0，
    // 用于把"鼠标休眠读不到"的断档如实画出来。
    struct HourPoint {
        int64_t hourStart = 0;
        int percent = -1;    // -1 表示这一小时没有数据
        int count = 0;
    };
    std::vector<HourPoint> hours;

    // ---- 简单分析
    int windowHours = 24;      // 统计窗口
    int hoursWithData = 0;     // 其中有数据的小时数
    int minPercent = -1;
    int maxPercent = -1;
    int chargeSamples = 0;
};

// 配色（与 Python 版 DARK / LIGHT 对应）
//
// **所有颜色字段都是 0xAARRGGBB**，必须显式带 alpha 字节。
// 曾把 accent* 写成纯 RGB（最高字节 0 = 全透明），
// 结果进度条填充与"严重电量"红字全部画不出来，
// 而数值对比完全正常 —— 只有看图才发现。
struct Palette {
    uint32_t text = 0x1A1A1C;
    uint32_t textDim = 0x5F5F64;
    uint32_t textFaint = 0x828288;
    uint32_t track = 0x1C000000;      // 带 alpha 的轨道色
    uint32_t stroke = 0x16000000;
    uint32_t shadow = 0x38000000;
    uint32_t surface = 0xF9F9FA;      // 材质表面色（RGB）
    uint8_t surfaceAlpha = 210;
    uint32_t accentLow = 0xC82C26;    // ≤20%
    uint32_t accentMid = 0xC89218;    // ≤45%
    uint32_t accentOk = 0x18944C;     // >45%
    uint32_t accentUnknown = 0x96969C;
    uint32_t accentCharging = 0x0078D4;   // 系统强调色（浅色主题默认）
};
Palette LightPalette();
Palette DarkPalette();

// 取状态色（与 Python 版 accent_for 一致）
uint32_t AccentFor(const Palette& p, int percent, bool charging);

// 把内容合成为一张画布图（**可单独测试**，不需要窗口）。
//   backdrop  窗口背后的画面（实时抓取）
//   wallpaper 桌面壁纸（云母用）
// 返回 kCanvasW×kCanvasH、**直通 alpha** 的 RGBA。
bool Compose(const Content& c, const Palette& pal,
             gpu::Renderer& renderer, const gpu::Image& backdrop,
             const gpu::Image& wallpaper, const std::string& material,
             gpu::Image& out, std::string& err);

// ---------------------------------------------------------------- 浮窗窗口

class Popup {
public:
    Popup() = default;
    ~Popup();

    Popup(const Popup&) = delete;
    Popup& operator=(const Popup&) = delete;

    struct Options {
        int x = 0, y = 0;             // 屏幕坐标（画布左上角）
        std::string material = "acrylic";
        bool dark = false;
        bool realtime = true;         // 背景变化时跟着变
        int fps = 30;
        int autoCloseMs = 12000;      // 自动关闭（0 = 不自动关）
    };

    // 显示浮窗。返回是否成功。
    bool Show(const Content& c, const Options& opt, std::string& err);

    // 更新内容并重绘
    void Update(const Content& c);

    // 关闭
    void Close();

    // 处理消息（外部消息循环调用）。返回是否已处理。
    bool ProcessMessage(void* msg);

    bool visible() const { return hwnd_ != nullptr; }
    void* hwnd() const { return hwnd_; }

    // 是否已过自动关闭时间
    bool Expired() const;

    // 供测试：窗口是否真的可见（IsWindowVisible）
    bool WindowVisible() const;

    // 是否对抓屏隐形。**正常运行时始终为 false**：
    // 实测 WS_EX_LAYERED + WDA_EXCLUDEFROMCAPTURE 会让分层窗口
    // 完全不再被合成（屏幕上看不见），所以不能用它来避免"抓到自己"。
    // 保留此接口仅为诊断。
    bool captureExcluded() const { return excluded_; }

private:
    void* hwnd_ = nullptr;
    Content content_;
    Options opt_;
    // ---- 实时材质节流
    // realtime 关闭时不再持续抓屏重绘，只在内容变化时刷新；
    // 开启时按 opt_.fps 限制重绘频率（默认 30 会太耗，实测抓屏 1.4~5.5ms）。
    double lastPaintSec_ = 0.0;
    std::string lastContentKey_;   // 内容指纹，用于判断是否需要重绘
    bool ContentChanged(const Content& c);
    static std::string MakeContentKey(const Content& c);
    gpu::Renderer* renderer_ = nullptr;
    // 复用的抓屏器：早期每帧新建 DC/位图，既慢又掩盖了真正的问题。
    // 注意它必须与窗口同寿命，且窗口必须对抓屏隐形（见 Show()）。
    void* grabber_ = nullptr;
    bool excluded_ = false;      // 是否已成功排除抓屏（用于自检/诊断）
    void* dib_ = nullptr;
    void* memdc_ = nullptr;
    void* oldBitmap_ = nullptr;
    gpu::Image composed_;          // 最近一次合成结果
    double shownAt_ = 0;
    bool dragging_ = false;
    int dragOffsetX_ = 0, dragOffsetY_ = 0;

    bool EnsureDib(int w, int h, std::string& err);
    bool Present();
};

// ---------------------------------------------------------------- 自检

// 验证：合成结果合理、分层窗口能创建并可见、预乘 alpha 正确
int RunSelfTest();

// 把浮窗离屏渲染并存成 32 位 BMP（供与 Python 版对比）
struct DumpOptions {
    int percent = 94;
    bool charging = false;
    bool stale = false;
    bool dark = false;
    std::string material = "acrylic";
    std::string outPath;
    // 可选：从该 history.json 读取按小时的电量（用于预览）
    std::string historyPath;
    // 预览用的背景：stripes（默认）/ white / dark / photo
    // 用于量化"材质在不同明暗背景下的观感"
    std::string backdrop = "stripes";
};
bool DumpPopup(const DumpOptions& o, std::string& err);

}  // namespace popup
