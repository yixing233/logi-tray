// ============================================================================
//  settings_window.h — 设置窗口
//
//  从已验证的 Python 版 (settings_window.py) 移植。
//
//  布局（456x458，与 Python 版逐项对应）：
//    标题            y=26
//    「提醒设置」     y=70
//    低电量标签      y=98     滑块 y=112
//    严重标签        y=152    滑块 y=166
//    启用通知开关    y=196
//    「刷新间隔」     y=240    单选行 y=262
//    「窗口材质」     y=300    材质卡 y=310，实时开关 y=374
//    按钮            y=404（取消 / 保存），右上角关闭
//
//  这个窗口是**普通分层窗口 + GPU 材质**，交互全部自绘：
//  用 Win32 原生控件会带来主题不一致与高 DPI 布局问题，
//  而这里已有完整的材质渲染与命中测试能力。
// ============================================================================
#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "config.h"
#include "glass_renderer.h"

namespace settingswin {

constexpr int kPanelW = 456;
constexpr int kPanelH = 400;   // 移除材质卡后收窄
constexpr int kPad = 26;

constexpr int kLowMin = 5, kLowMax = 90;     // 低电量阈值范围
constexpr int kCritMin = 1, kCritMax = 50;   // 严重阈值范围

// 单选行的选项（与 Python 版一致）
const int kIntervals[] = {10, 15, 30, 60, 120};

// 材质标签与说明（与 glass_effects.LABELS / DESCRIPTIONS 对应）
struct MaterialInfo {
    const char* id;
    const char* label;
    const char* description;
};
const MaterialInfo* Materials(int& count);

// ---------------------------------------------------------------- 控件

// 滑块：细轨道 + 圆形滑块，值映射到像素
struct Slider {
    int x = 0, y = 0, w = 0, h = 24;
    int lo = 0, hi = 100;
    int value = 0;
    uint32_t fill = 0;
    bool hover = false, active = false;

    static constexpr int kInset = 9;
    int centerY() const { return y + h / 2; }
    void trackX(int& x0, int& x1) const {
        x0 = x + kInset;
        x1 = x + w - kInset;
    }
    int ValueToX(int v) const;
    int XToValue(int px) const;
    bool Hit(int px, int py) const {
        return px >= x && px < x + w && py >= y && py < y + h;
    }
};

// 开关
struct Toggle {
    int x = 0, y = 0, w = 0, h = 30;
    std::string label;
    bool on = false;
    bool hover = false;
    bool Hit(int px, int py) const {
        return px >= x && px < x + w && py >= y && py < y + h;
    }
};

// 单选行
struct RadioRow {
    int x = 0, y = 0, w = 0, h = 32;
    int optionCount = 0;
    int selectedIndex = 0;
    int hoverIndex = -1;
    int OptionWidth() const {
        return optionCount > 0 ? w / optionCount : w;
    }
    void OptionRect(int i, int& ox, int& oy, int& ow, int& oh) const {
        const int owid = OptionWidth();
        ox = x + i * owid;
        oy = y;
        ow = owid;
        oh = h;
    }
    int Hit(int px, int py) const {
        if (py < y || py >= y + h) return -1;
        const int owid = OptionWidth();
        if (owid <= 0) return -1;
        const int i = (px - x) / owid;
        if (i < 0 || i >= optionCount) return -1;
        return i;
    }
};

// 按钮
struct Button {
    int x = 0, y = 0, w = 0, h = 0;
    std::string label;
    bool primary = false;
    bool hover = false;
    bool Hit(int px, int py) const {
        return px >= x && px < x + w && py >= y && py < y + h;
    }
};

// 关闭按钮（右上角叉）
struct CloseButton {
    int x = 0, y = 0, w = 0, h = 0;
    bool hover = false;
    bool Hit(int px, int py) const {
        return px >= x && px < x + w && py >= y && py < y + h;
    }
};

// ---------------------------------------------------------------- 窗口

class SettingsWindow {
public:
    SettingsWindow() = default;
    ~SettingsWindow();

    SettingsWindow(const SettingsWindow&) = delete;
    SettingsWindow& operator=(const SettingsWindow&) = delete;

    // 打开窗口。initial 是当前配置。
    bool Show(const config::Settings& initial, std::string& err);
    void Close();
    bool visible() const { return hwnd_ != nullptr; }
    void* hwnd() const { return hwnd_; }

    // 处理消息；返回是否已处理
    bool ProcessMessage(void* msg);

    // 保存后回调（外部据此写盘并应用）
    using SaveFn = std::function<void(const config::Settings&)>;
    void setOnSave(SaveFn fn) { onSave_ = std::move(fn); }

    // 供测试：当前编辑中的配置
    const config::Settings& editing() const { return cfg_; }

    // 供测试：把整窗渲染成图像（不依赖窗口）
    bool RenderToImage(gpu::Image& out, std::string& err);
    bool RenderToImageOf(gpu::Image& out, std::string& err) {
        return RenderToImage(out, err);
    }

    // 供测试：命中测试（模拟点击某坐标）
    enum class HitKind { None, LowSlider, CritSlider, Notify, Interval,
                         Realtime, Save, Cancel, Close };
    struct HitResult {
        HitKind kind = HitKind::None;
        int index = -1;
    };
    HitResult HitTest(int px, int py) const;

    // 以下三项供自检使用（布局与命中是纯逻辑，理应可单测）
    void BuildLayout();
    void ApplyHit(const HitResult& hit);
    const Slider& lowSlider() const { return lowSlider_; }
    const Slider& critSlider() const { return critSlider_; }

    // ---- 鼠标交互（从窗口过程里提出来，便于单测）
    // 返回 true 表示触发了"关闭/保存"这类一次性命令。
    bool OnMouseDown(int px, int py);
    void OnMouseMove(int px, int py);
    void OnMouseUp();
    // 内部：重新合成并呈现
    void Repaint();
    bool IsDarkTheme() const;

    friend bool DumpSettings(const std::string&, const std::string&,
                            std::string&);

private:
    void* hwnd_ = nullptr;
    config::Settings cfg_;
    config::Settings original_;
    gpu::Renderer* renderer_ = nullptr;
    void* dib_ = nullptr;
    gpu::Image composed_;
    SaveFn onSave_;

    // 控件
    Slider lowSlider_, critSlider_;
    Toggle notify_, realtime_;
    RadioRow interval_;
    Button saveBtn_, cancelBtn_;
    CloseButton closeBtn_;

    // 交互状态
    int dragSlider_ = -1;      // 0=low 1=crit
    bool dragging_ = false;
    int dragDx_ = 0, dragDy_ = 0;
    int hoverKind_ = -1;

    bool EnsureDib(int w, int h, std::string& err);
    bool Present();
    void DrawAll(gpu::Image& canvas, bool dark);
};

// ---------------------------------------------------------------- 自检

// 验证：布局不重叠、命中测试正确、滑块映射、渲染有内容、窗口可创建
int RunSelfTest();

// 把设置窗口离屏渲染成 32 位 BMP（供人工检查与与 Python 版对比）
bool DumpSettings(const std::string& outPath, const std::string& material,
                  std::string& err);

}  // namespace settingswin
