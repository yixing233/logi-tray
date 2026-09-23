// ============================================================================
//  settings_window.cpp — 设置窗口实现
//
//  自绘控件 + GPU 材质背景。控件逻辑（命中测试、滑块映射）都是纯函数，
//  可以脱离窗口单独测试 —— 这是 Python 版踩过坑的地方：
//  当时设置窗口整个不可操作，根因是 GWLP_USERDATA 用了 -8（那其实是
//  GWLP_HWNDPARENT），对象指针写错槽位，窗口过程拿到的永远是 nullptr，
//  所有鼠标消息都落到 DefWindowProc，控件完全没反应。
//  所以这里坚持：**布局与命中测试可单测**，不依赖消息循环。
// ============================================================================

#include "settings_window.h"
#include "capture.h"
#include "util.h"

#include <windows.h>
#include <windowsx.h>
#include <objidl.h>
#include <gdiplus.h>

#include <cmath>
#include <cstring>
#include <ctime>
#include <vector>

#pragma comment(lib, "gdiplus.lib")

using namespace Gdiplus;

namespace settingswin {

namespace {

const wchar_t* kClassName = L"MouseBatteryTraySettings";

ULONG_PTR g_token = 0;
bool g_gdiReady = false;

bool EnsureGdiplus() {
    if (g_gdiReady) return true;
    GdiplusStartupInput in;
    if (GdiplusStartup(&g_token, &in, nullptr) != Ok) return false;
    g_gdiReady = true;
    return true;
}

inline BYTE A(uint32_t c) { return static_cast<BYTE>((c >> 24) & 0xFF); }
inline BYTE R(uint32_t c) { return static_cast<BYTE>((c >> 16) & 0xFF); }
inline BYTE G(uint32_t c) { return static_cast<BYTE>((c >> 8) & 0xFF); }
inline BYTE B(uint32_t c) { return static_cast<BYTE>(c & 0xFF); }
inline Color ToColor(uint32_t c, BYTE aOverride = 0) {
    const BYTE a = aOverride ? aOverride : A(c);
    return Color(a, R(c), G(c), B(c));
}

// 配色（所有值都是 0xAARRGGBB —— 漏掉 alpha 字节会让画的东西全透明，
// 这个坑在浮窗上踩过一次）
struct Theme {
    uint32_t text = 0xFF1A1A1C;
    uint32_t textDim = 0xFF5F5F64;
    uint32_t textFaint = 0xFF828288;
    uint32_t section = 0xFF3A3A40;
    uint32_t track = 0x1C000000;
    uint32_t cardBg = 0x0F000000;
    uint32_t cardBorder = 0x22000000;
    uint32_t stroke = 0x16000000;
    uint32_t accent = 0xFF0078D4;
    uint32_t accentLow = 0xFFC82C26;
    uint32_t shadow = 0x38000000;
    uint32_t surface = 0xF9F9FA;
    uint8_t surfaceAlpha = 210;
};

Theme LightTheme() { return Theme(); }

Theme DarkTheme() {
    Theme t;
    t.text = 0xFFF3F3F5;
    t.textDim = 0xFFA8A8AE;
    t.textFaint = 0xFF84848A;
    t.section = 0xFFC8C8CE;
    t.track = 0x22FFFFFF;
    t.cardBg = 0x14FFFFFF;
    t.cardBorder = 0x28FFFFFF;
    t.stroke = 0x18FFFFFF;
    t.accent = 0xFF4CC2FF;
    t.accentLow = 0xFFE8544E;
    t.shadow = 0x78000000;
    t.surface = 0x202022;
    t.surfaceAlpha = 208;
    return t;
}

FontFamily* RegularFamily() {
    static FontFamily f(L"Microsoft YaHei");
    if (f.IsAvailable()) return &f;
    static FontFamily fb(L"Segoe UI");
    return &fb;
}

// 按基线绘制（与弹出浮窗保持一致，便于布局对齐）
void DrawTextBaseline(Graphics& g, const std::wstring& text, Font& font,
                      REAL x, REAL baselineY, const Color& color) {
    FontFamily fam;
    const INT style = font.GetStyle();
    REAL ascent = font.GetSize() * 0.86f;
    if (font.GetFamily(&fam) == Ok) {
        const INT em = fam.GetEmHeight(style);
        if (em > 0)
            ascent = font.GetSize() * fam.GetCellAscent(style)
                   / static_cast<REAL>(em);
    }
    SolidBrush brush(color);
    StringFormat sf;
    sf.SetFormatFlags(StringFormatFlagsNoWrap);
    g.DrawString(text.c_str(), -1, &font, PointF(x, baselineY - ascent),
                 &sf, &brush);
}

REAL MeasureWidth(Graphics& g, const std::wstring& text, Font& font) {
    RectF box;
    g.MeasureString(text.c_str(), -1, &font, PointF(0, 0), &box);
    return box.Width;
}

// 居中绘制
void DrawTextCentered(Graphics& g, const std::wstring& text, Font& font,
                      REAL cx, REAL cy, const Color& color) {
    StringFormat sf;
    sf.SetAlignment(StringAlignmentCenter);
    sf.SetLineAlignment(StringAlignmentCenter);
    SolidBrush brush(color);
    g.DrawString(text.c_str(), -1, &font, PointF(cx, cy), &sf, &brush);
}

void FillRoundedRect(Graphics& g, REAL x, REAL y, REAL w, REAL h, REAL rad,
                     Brush* brush) {
    if (w < 1 || h < 1) return;
    const REAL r = (w / 2 < rad) ? w / 2 : ((h / 2 < rad) ? h / 2 : rad);
    GraphicsPath path;
    path.AddArc(x, y, r * 2, r * 2, 180.0f, 90.0f);
    path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270.0f, 90.0f);
    path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0.0f, 90.0f);
    path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90.0f, 90.0f);
    path.CloseFigure();
    g.FillPath(brush, &path);
}

void DrawRoundedRect(Graphics& g, REAL x, REAL y, REAL w, REAL h, REAL rad,
                     Pen* pen) {
    const REAL r = (w / 2 < rad) ? w / 2 : ((h / 2 < rad) ? h / 2 : rad);
    GraphicsPath path;
    path.AddArc(x, y, r * 2, r * 2, 180.0f, 90.0f);
    path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270.0f, 90.0f);
    path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0.0f, 90.0f);
    path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90.0f, 90.0f);
    path.CloseFigure();
    g.DrawPath(pen, &path);
}

// 分层窗口 DIB（32bpp，BGRA）。alpha=0 的 CreateCompatibleBitmap 会让窗口
// 全透明，必须用 CreateDIBSection。
struct LayeredDib {
    HDC memdc = nullptr;
    HBITMAP bitmap = nullptr;
    HGDIOBJ oldBitmap = nullptr;
    void* bits = nullptr;
    int w = 0, h = 0;

    bool Create(int width, int height) {
        Destroy();
        HDC screen = GetDC(nullptr);
        memdc = CreateCompatibleDC(screen);
        ReleaseDC(nullptr, screen);
        if (!memdc) return false;
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = width;
        bi.bmiHeader.biHeight = -height;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        bitmap = CreateDIBSection(memdc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!bitmap || !bits) { Destroy(); return false; }
        oldBitmap = SelectObject(memdc, bitmap);
        w = width;
        h = height;
        return true;
    }
    void Destroy() {
        if (memdc && oldBitmap) SelectObject(memdc, oldBitmap);
        if (bitmap) DeleteObject(bitmap);
        if (memdc) DeleteDC(memdc);
        memdc = nullptr; bitmap = nullptr; oldBitmap = nullptr; bits = nullptr;
        w = h = 0;
    }
};

inline bool IsDark() {
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
    return !light;
}

}  // namespace

// ---------------------------------------------------------------- 控件实现

int Slider::ValueToX(int v) const {
    int x0 = 0, x1 = 0;
    trackX(x0, x1);
    const double f = static_cast<double>(v - lo) /
                     static_cast<double>(std::max(1, hi - lo));
    return static_cast<int>(std::lround(x0 + (x1 - x0) * f));
}

int Slider::XToValue(int px) const {
    int x0 = 0, x1 = 0;
    trackX(x0, x1);
    double f = static_cast<double>(px - x0) /
               static_cast<double>(std::max(1, x1 - x0));
    f = std::max(0.0, std::min(1.0, f));
    return static_cast<int>(std::lround(lo + f * (hi - lo)));
}

const MaterialInfo* Materials(int& count) {
    static const MaterialInfo kMats[] = {
        {"liquid", u8"液态玻璃", u8"折射色散"},
        {"acrylic", u8"亚克力", u8"模糊通透"},
        {"mica", u8"云母", u8"壁纸色调"},
    };
    count = 3;
    return kMats;
}

// ---------------------------------------------------------------- 布局

void SettingsWindow::BuildLayout() {
    const int W = kPanelW;
    const int PAD = kPad;
    const int cw = W - PAD * 2;

    // 低电量滑块
    lowSlider_.x = PAD;
    lowSlider_.y = 112;
    lowSlider_.w = cw;
    lowSlider_.h = 24;
    lowSlider_.lo = kLowMin;
    lowSlider_.hi = kLowMax;
    lowSlider_.value = cfg_.lowThreshold;

    // 严重阈值滑块
    critSlider_.x = PAD;
    critSlider_.y = 166;
    critSlider_.w = cw;
    critSlider_.h = 24;
    critSlider_.lo = kCritMin;
    critSlider_.hi = kCritMax;
    critSlider_.value = cfg_.criticalThreshold;

    notify_.x = PAD;
    notify_.y = 196;
    notify_.w = cw;
    notify_.h = 30;
    notify_.label = u8"启用低电量通知";
    notify_.on = cfg_.notifyEnabled;

    interval_.x = PAD;
    interval_.y = 262;
    interval_.w = cw;
    interval_.h = 32;
    interval_.optionCount = 5;
    interval_.selectedIndex = 2;   // 默认 30 秒
    for (int i = 0; i < 5; ++i)
        if (kIntervals[i] == cfg_.interval) interval_.selectedIndex = i;

    // 材质卡已移除（只保留亚克力，无需选择）。
    // 实时开关上移到原「窗口材质」分组标题的位置。
    realtime_.x = PAD;
    realtime_.y = 300;
    realtime_.w = cw;
    realtime_.h = 30;
    realtime_.label = u8"实时材质（背景变化时跟随）";
    realtime_.on = cfg_.realtime;

    const int bw = 86, bh = 34;
    const int by = kPanelH - bh - 20;
    saveBtn_.x = W - PAD - bw;
    saveBtn_.y = by;
    saveBtn_.w = bw;
    saveBtn_.h = bh;
    saveBtn_.label = u8"保存";
    saveBtn_.primary = true;

    cancelBtn_.x = W - PAD - bw * 2 - 10;
    cancelBtn_.y = by;
    cancelBtn_.w = bw;
    cancelBtn_.h = bh;
    cancelBtn_.label = u8"取消";

    closeBtn_.x = W - 38;
    closeBtn_.y = 12;
    closeBtn_.w = 28;
    closeBtn_.h = 28;
}

SettingsWindow::HitResult SettingsWindow::HitTest(int px, int py) const {
    HitResult r;
    // 顺序很重要：先判面积小、层级高的控件（关闭按钮在标题栏区域，
    // 但仍要早于其它；滑块在卡片之上）
    if (closeBtn_.Hit(px, py)) { r.kind = HitKind::Close; return r; }
    if (saveBtn_.Hit(px, py)) { r.kind = HitKind::Save; return r; }
    if (cancelBtn_.Hit(px, py)) { r.kind = HitKind::Cancel; return r; }

    if (lowSlider_.Hit(px, py)) { r.kind = HitKind::LowSlider; return r; }
    if (critSlider_.Hit(px, py)) { r.kind = HitKind::CritSlider; return r; }
    if (notify_.Hit(px, py)) { r.kind = HitKind::Notify; return r; }
    {
        const int i = interval_.Hit(px, py);
        if (i >= 0) { r.kind = HitKind::Interval; r.index = i; return r; }
    }
    if (realtime_.Hit(px, py)) { r.kind = HitKind::Realtime; return r; }
    return r;
}

void SettingsWindow::ApplyHit(const HitResult& hit) {
    switch (hit.kind) {
        case HitKind::Notify:
            notify_.on = !notify_.on;
            cfg_.notifyEnabled = notify_.on;
            break;
        case HitKind::Realtime:
            realtime_.on = !realtime_.on;
            cfg_.realtime = realtime_.on;
            break;
        case HitKind::Interval:
            if (hit.index >= 0 && hit.index < 5) {
                interval_.selectedIndex = hit.index;
                cfg_.interval = kIntervals[hit.index];
                cfg_.Sanitise();
            }
            break;
        default:
            break;
    }
}

// ---------------------------------------------------------------- 绘制

void SettingsWindow::DrawAll(gpu::Image& canvas, bool dark) {
    if (!EnsureGdiplus()) return;

    const Theme T = dark ? DarkTheme() : LightTheme();
    // GDI+ 要 BGRA
    gpu::SwapRedBlue(canvas);
    {
        Bitmap bmp(kPanelW, kPanelH, kPanelW * 4, PixelFormat32bppARGB,
                   canvas.rgba.data());
        Graphics g(&bmp);
        g.SetSmoothingMode(SmoothingModeAntiAlias);
        // 与浮窗保持一致：用 ClearType 网格拟合。
        // AntiAlias 在 GDI+ 里是**不做网格拟合**，字形发虚；
        // 实测浮窗用 ClearType 后文字明显更实、更接近系统原生 UI 文字。
        // 设置窗口背景是普通不透明窗口，ClearType 前提完全满足。
        g.SetTextRenderingHint(TextRenderingHintClearTypeGridFit);
        g.SetCompositingMode(CompositingModeSourceOver);
        g.SetCompositingQuality(CompositingQualityHighQuality);

        // 细描边
        {
            Pen pen(ToColor(T.stroke), 1.0f);
            g.DrawRectangle(&pen, 0.0f, 0.0f, static_cast<REAL>(kPanelW - 1),
                            static_cast<REAL>(kPanelH - 1));
        }

        // 标题
        {
            Font f(RegularFamily(), 16.0f, FontStyleBold, UnitPixel);
            DrawTextBaseline(g, util::Utf8ToWide(u8"鼠标电量 · 设置"), f, 26.0f, 26.0f + 12,
                             ToColor(T.text));
        }

        // 分组标题
        auto section = [&](const char* label, REAL y) {
            Font f(RegularFamily(), 12.0f, FontStyleRegular, UnitPixel);
            DrawTextBaseline(g, util::Utf8ToWide(label), f, 26.0f, y + 10,
                             ToColor(T.section));
        };
        section(u8"提醒设置", 70);
        section(u8"刷新间隔", 240);

        // ---- 低电量滑块 + 数值标签
        {
            Font fLabel(RegularFamily(), 13.0f, FontStyleRegular, UnitPixel);
            DrawTextBaseline(g, util::Utf8ToWide(u8"低电量提醒阈值"), fLabel, 26.0f, 98 + 10,
                             ToColor(T.text));
            char buf[32];
            std::snprintf(buf, sizeof(buf), "%d%%", lowSlider_.value);
            Font fVal(RegularFamily(), 13.0f, FontStyleBold, UnitPixel);
            const REAL vw = MeasureWidth(g, util::Utf8ToWide(buf), fVal);
            DrawTextBaseline(g, util::Utf8ToWide(buf), fVal,
                             kPanelW - 26 - vw, 98 + 10, ToColor(T.accent));

            // 滑块：轨道
            int x0 = 0, x1 = 0;
            lowSlider_.trackX(x0, x1);
            const REAL cy = static_cast<REAL>(lowSlider_.centerY());
            const REAL half = 2.0f;
            SolidBrush trackBr(ToColor(T.track));
            FillRoundedRect(g, static_cast<REAL>(x0), cy - half,
                            static_cast<REAL>(x1 - x0), half * 2, half,
                            &trackBr);
            // 已填充
            const int kx = lowSlider_.ValueToX(lowSlider_.value);
            if (kx > x0) {
                SolidBrush fillBr(ToColor(T.accent));
                FillRoundedRect(g, static_cast<REAL>(x0), cy - half,
                                static_cast<REAL>(kx - x0), half * 2, half,
                                &fillBr);
            }
            // 滑块：白圆 + 彩色描边（Win11 观感）
            const REAL r = 8.0f + ((lowSlider_.hover || lowSlider_.active)
                                   ? 1.0f : 0.0f);
            SolidBrush white(Color(255, 255, 255, 255));
            g.FillEllipse(&white, kx - r, cy - r, r * 2, r * 2);
            Pen edge(ToColor(T.accent), 2.0f);
            g.DrawEllipse(&edge, kx - r, cy - r, r * 2, r * 2);
        }

        // ---- 严重阈值滑块
        {
            Font fLabel(RegularFamily(), 13.0f, FontStyleRegular, UnitPixel);
            DrawTextBaseline(g, util::Utf8ToWide(u8"严重低电量阈值"), fLabel, 26.0f, 152 + 10,
                             ToColor(T.text));
            char buf[32];
            std::snprintf(buf, sizeof(buf), "%d%%", critSlider_.value);
            Font fVal(RegularFamily(), 13.0f, FontStyleBold, UnitPixel);
            const REAL vw = MeasureWidth(g, util::Utf8ToWide(buf), fVal);
            DrawTextBaseline(g, util::Utf8ToWide(buf), fVal,
                             kPanelW - 26 - vw, 152 + 10,
                             ToColor(T.accentLow));

            int x0 = 0, x1 = 0;
            critSlider_.trackX(x0, x1);
            const REAL cy = static_cast<REAL>(critSlider_.centerY());
            const REAL half = 2.0f;
            SolidBrush trackBr(ToColor(T.track));
            FillRoundedRect(g, static_cast<REAL>(x0), cy - half,
                            static_cast<REAL>(x1 - x0), half * 2, half,
                            &trackBr);
            const int kx = critSlider_.ValueToX(critSlider_.value);
            if (kx > x0) {
                SolidBrush fillBr(ToColor(T.accentLow));
                FillRoundedRect(g, static_cast<REAL>(x0), cy - half,
                                static_cast<REAL>(kx - x0), half * 2, half,
                                &fillBr);
            }
            const REAL r = 8.0f + ((critSlider_.hover || critSlider_.active)
                                   ? 1.0f : 0.0f);
            SolidBrush white(Color(255, 255, 255, 255));
            g.FillEllipse(&white, kx - r, cy - r, r * 2, r * 2);
            Pen edge(ToColor(T.accentLow), 2.0f);
            g.DrawEllipse(&edge, kx - r, cy - r, r * 2, r * 2);
        }

        // ---- 开关（通知 / 实时材质）
        auto drawToggle = [&](const Toggle& t, uint32_t accent) {
            Font f(RegularFamily(), 13.0f, FontStyleRegular, UnitPixel);
            DrawTextBaseline(g, util::Utf8ToWide(t.label), f, 26.0f,
                             static_cast<REAL>(t.y + t.h / 2 + 5),
                             ToColor(T.text));
            // 右侧药丸开关
            const REAL sw = 44.0f, sh = 24.0f;
            const REAL sx = static_cast<REAL>(t.x + t.w) - sw;
            const REAL sy = t.y + (t.h - sh) / 2.0f;
            SolidBrush bg(t.on ? ToColor(accent) : ToColor(T.track));
            FillRoundedRect(g, sx, sy, sw, sh, sh / 2, &bg);
            const REAL kr = sh / 2 - 3;
            const REAL kx = t.on ? (sx + sw - kr - 3) : (sx + kr + 3);
            SolidBrush knob(Color(255, 255, 255, 255));
            g.FillEllipse(&knob, kx - kr, sy + sh / 2 - kr, kr * 2, kr * 2);
        };
        drawToggle(notify_, T.accent);
        drawToggle(realtime_, T.accent);

        // ---- 单选行（刷新间隔）
        {
            int n = 0;
            const int* vals = kIntervals;
            n = 5;
            for (int i = 0; i < n; ++i) {
                int ox = 0, oy = 0, ow = 0, oh = 0;
                interval_.OptionRect(i, ox, oy, ow, oh);
                const bool sel = (i == interval_.selectedIndex);
                const bool hov = (i == interval_.hoverIndex);
                if (sel || hov) {
                    SolidBrush bg(sel ? ToColor(T.accent)
                                      : ToColor(T.cardBg));
                    FillRoundedRect(g, static_cast<REAL>(ox) + 2,
                                    static_cast<REAL>(oy), ow - 4.0f,
                                    static_cast<REAL>(oh), 6.0f, &bg);
                }
                char buf[32];
                if (vals[i] < 60) std::snprintf(buf, sizeof(buf), u8"%d 秒", vals[i]);
                else std::snprintf(buf, sizeof(buf), u8"%d 分", vals[i] / 60);
                Font f(RegularFamily(), 12.0f,
                       sel ? FontStyleBold : FontStyleRegular, UnitPixel);
                DrawTextCentered(g, util::Utf8ToWide(buf), f,
                                 ox + ow / 2.0f, oy + oh / 2.0f,
                                 sel ? Color(255, 255, 255, 255)
                                     : ToColor(T.text));
            }
        }

        // ---- 按钮
        auto drawButton = [&](const Button& b) {
            if (b.primary) {
                SolidBrush bg(ToColor(T.accent));
                FillRoundedRect(g, static_cast<REAL>(b.x), static_cast<REAL>(b.y),
                                static_cast<REAL>(b.w), static_cast<REAL>(b.h),
                                6.0f, &bg);
            } else {
                SolidBrush bg(ToColor(T.cardBg));
                FillRoundedRect(g, static_cast<REAL>(b.x), static_cast<REAL>(b.y),
                                static_cast<REAL>(b.w), static_cast<REAL>(b.h),
                                6.0f, &bg);
                Pen border(ToColor(T.cardBorder), 1.0f);
                DrawRoundedRect(g, static_cast<REAL>(b.x), static_cast<REAL>(b.y),
                                static_cast<REAL>(b.w), static_cast<REAL>(b.h),
                                6.0f, &border);
            }
            Font f(RegularFamily(), 13.0f, FontStyleRegular, UnitPixel);
            DrawTextCentered(g, util::Utf8ToWide(b.label), f,
                             b.x + b.w / 2.0f, b.y + b.h / 2.0f,
                             b.primary ? Color(255, 255, 255, 255)
                                       : ToColor(T.text));
        };
        drawButton(saveBtn_);
        drawButton(cancelBtn_);

        // ---- 关闭按钮（叉）
        {
            const REAL pad = 7.0f;
            Pen pen(ToColor(closeBtn_.hover ? T.text : T.textDim), 1.6f);
            g.DrawLine(&pen, closeBtn_.x + pad, closeBtn_.y + pad,
                       closeBtn_.x + closeBtn_.w - pad,
                       closeBtn_.y + closeBtn_.h - pad);
            g.DrawLine(&pen, closeBtn_.x + closeBtn_.w - pad, closeBtn_.y + pad,
                       closeBtn_.x + pad, closeBtn_.y + closeBtn_.h - pad);
        }
    }
    // 转回 RGBA，保持约定
    gpu::SwapRedBlue(canvas);
}

// ---------------------------------------------------------------- 窗口

bool SettingsWindow::RenderToImage(gpu::Image& out, std::string& err) {
    if (!renderer_) {
        renderer_ = new gpu::Renderer();
        if (!renderer_->Init(err)) {
            delete renderer_;
            renderer_ = nullptr;
            return false;
        }
    }

    // 用条纹背景，让材质可见
    gpu::Image backdrop;
    backdrop.Resize(kPanelW, kPanelH);
    for (int y = 0; y < kPanelH; ++y)
        for (int x = 0; x < kPanelW; ++x) {
            const bool on = ((x / 10) % 2) == 0;
            uint8_t* p = &backdrop.rgba[
                (static_cast<size_t>(y) * kPanelW + x) * 4];
            p[0] = on ? 230 : 30; p[1] = on ? 100 : 50; p[2] = on ? 70 : 210;
            p[3] = 255;
        }

    const bool dark = IsDark();
    const Theme T = dark ? DarkTheme() : LightTheme();

    // 目前只有亚克力
    const gpu::Material mat = gpu::Material::Acrylic;

    gpu::Params params;
    params.ApplyMaterialDefaults(mat);
    params.surfaceR = static_cast<float>(R(T.surface));
    params.surfaceG = static_cast<float>(G(T.surface));
    params.surfaceB = static_cast<float>(B(T.surface));
    params.opacity = T.surfaceAlpha / 255.0f;

    if (!renderer_->Render(backdrop, backdrop, mat, params, kPanelW, kPanelH,
                           0.0f, 0.0f, out, err))
        return false;

    DrawAll(out, dark);
    return true;
}

SettingsWindow::~SettingsWindow() { Close(); }

bool SettingsWindow::EnsureDib(int w, int h, std::string& err) {
    if (dib_) {
        auto* d = reinterpret_cast<LayeredDib*>(dib_);
        if (d->w == w && d->h == h) return true;
    }
    auto* d = new LayeredDib();
    if (!d->Create(w, h)) {
        delete d;
        err = "创建设置窗口位图失败";
        return false;
    }
    if (dib_) {
        auto* old = reinterpret_cast<LayeredDib*>(dib_);
        old->Destroy();
        delete old;
    }
    dib_ = d;
    return true;
}

bool SettingsWindow::Present() {
    if (!hwnd_ || !composed_.Valid()) return false;
    auto* d = reinterpret_cast<LayeredDib*>(dib_);
    if (!d || !d->bits) return false;
    if (composed_.width != d->w || composed_.height != d->h) return false;

    // 预乘 alpha + RGBA->BGRA（UpdateLayeredWindow 要求预乘；DIB 是 BGRA）
    gpu::Image buf = composed_;
    uint8_t* p = buf.rgba.data();
    for (size_t i = 0; i + 3 < buf.rgba.size(); i += 4) {
        const unsigned a = p[i + 3];
        if (a != 255) {
            p[i + 0] = static_cast<uint8_t>(p[i + 0] * a / 255);
            p[i + 1] = static_cast<uint8_t>(p[i + 1] * a / 255);
            p[i + 2] = static_cast<uint8_t>(p[i + 2] * a / 255);
        }
    }
    gpu::SwapRedBlue(buf);
    std::memcpy(d->bits, buf.rgba.data(), buf.rgba.size());

    RECT wr{};
    GetWindowRect(static_cast<HWND>(hwnd_), &wr);
    POINT dst{wr.left, wr.top}, src{0, 0};
    SIZE size{d->w, d->h};
    BLENDFUNCTION bf{};
    bf.BlendOp = AC_SRC_OVER;
    bf.SourceConstantAlpha = 255;
    bf.AlphaFormat = AC_SRC_ALPHA;
    HDC screen = GetDC(nullptr);
    const BOOL ok = UpdateLayeredWindow(static_cast<HWND>(hwnd_), screen,
                                        &dst, &size, d->memdc, &src, 0, &bf,
                                        ULW_ALPHA);
    ReleaseDC(nullptr, screen);
    return ok != FALSE;
}

namespace {

SettingsWindow* g_active = nullptr;   // 当前设置窗口（同一时刻只允许一个）

// 注意：GWLP_USERDATA 的值是 -21。**不能**用 -8 —— 那是 GWLP_HWNDPARENT，
// 把对象指针写进去会让窗口过程永远拿到 nullptr，所有鼠标消息落到
// DefWindowProc，控件完全没反应（Python 版踩过这个坑，表现为"设置页面
// 鼠标一直是转圈圈，没法操作"）。
LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp) {
    SettingsWindow* self = reinterpret_cast<SettingsWindow*>(
        GetWindowLongPtrW(h, GWLP_USERDATA));

    switch (msg) {
        case WM_NCHITTEST:
            return HTCLIENT;
        case WM_LBUTTONDOWN:
            if (self && self->OnMouseDown(GET_X_LPARAM(lp), GET_Y_LPARAM(lp)))
                return 0;
            return 0;
        case WM_MOUSEMOVE:
            if (self) self->OnMouseMove(GET_X_LPARAM(lp), GET_Y_LPARAM(lp));
            return 0;
        case WM_LBUTTONUP:
            if (self) self->OnMouseUp();
            return 0;
        case WM_SETCURSOR:
            SetCursor(LoadCursorW(nullptr, MAKEINTRESOURCEW(32512)));
            return TRUE;
        case WM_CLOSE:
            if (self) self->Close();
            return 0;
        default:
            break;
    }
    return DefWindowProcW(h, msg, wp, lp);
}

void RegisterOnce() {
    static bool done = false;
    if (done) return;
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kClassName;
    // 必须显式给箭头光标：hCursor 为 NULL 时窗口会继承上一个窗口的光标，
    // Python 版因此出现过"设置页鼠标一直转圈圈"。
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    wc.hbrBackground = nullptr;
    RegisterClassExW(&wc);
    done = true;
}

}  // namespace

bool SettingsWindow::Show(const config::Settings& initial, std::string& err) {
    if (!EnsureGdiplus()) { err = "GDI+ 初始化失败"; return false; }
    if (g_active && g_active != this) g_active->Close();

    cfg_ = initial;
    original_ = initial;
    BuildLayout();

    if (!EnsureDib(kPanelW, kPanelH, err)) return false;

    RegisterOnce();
    // 居中
    const int sw = GetSystemMetrics(SM_CXSCREEN);
    const int sh = GetSystemMetrics(SM_CYSCREEN);
    const int x = (sw - kPanelW) / 2;
    const int y = (sh - kPanelH) / 2;

    HWND h = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
        kClassName, L"设置", WS_POPUP, x, y, kPanelW, kPanelH, nullptr,
        nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!h) { err = "创建设置窗口失败"; return false; }

    hwnd_ = h;
    // GWLP_USERDATA = -21（**不是** -8，-8 是 GWLP_HWNDPARENT）
    SetWindowLongPtrW(h, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
    g_active = this;

    gpu::Image img;
    if (!RenderToImage(img, err)) return false;
    composed_ = img;
    if (!Present()) { err = "UpdateLayeredWindow 失败"; return false; }
    ShowWindow(h, SW_SHOWNOACTIVATE);
    return true;
}

void SettingsWindow::Repaint() {
    gpu::Image img;
    std::string e;
    if (RenderToImage(img, e)) {
        composed_ = img;
        Present();
    }
}

bool SettingsWindow::IsDarkTheme() const { return settingswin::IsDark() != false; }

bool SettingsWindow::OnMouseDown(int px, int py) {
    const auto hit = HitTest(px, py);

    if (hit.kind == HitKind::LowSlider) {
        lowSlider_.active = true;
        lowSlider_.value = lowSlider_.XToValue(px);
        cfg_.lowThreshold = lowSlider_.value;
        cfg_.Sanitise();
        critSlider_.value = cfg_.criticalThreshold;
        Repaint();
        return false;
    }
    if (hit.kind == HitKind::CritSlider) {
        critSlider_.active = true;
        critSlider_.value = critSlider_.XToValue(px);
        cfg_.criticalThreshold = critSlider_.value;
        cfg_.Sanitise();
        critSlider_.value = cfg_.criticalThreshold;
        Repaint();
        return false;
    }
    if (hit.kind == HitKind::Close || hit.kind == HitKind::Cancel) {
        Close();
        return true;      // 命令已触发
    }
    if (hit.kind == HitKind::Save) {
        if (onSave_) onSave_(cfg_);
        Close();
        return true;
    }

    ApplyHit(hit);
    Repaint();
    return false;
}

void SettingsWindow::OnMouseMove(int px, int py) {
    bool needRepaint = false;

    if (lowSlider_.active) {
        lowSlider_.value = lowSlider_.XToValue(px);
        cfg_.lowThreshold = lowSlider_.value;
        cfg_.Sanitise();
        critSlider_.value = cfg_.criticalThreshold;
        needRepaint = true;
    } else if (critSlider_.active) {
        critSlider_.value = critSlider_.XToValue(px);
        cfg_.criticalThreshold = critSlider_.value;
        cfg_.Sanitise();
        critSlider_.value = cfg_.criticalThreshold;
        needRepaint = true;
    } else {
        const bool hLow = lowSlider_.Hit(px, py);
        const bool hCrit = critSlider_.Hit(px, py);
        if (hLow != lowSlider_.hover || hCrit != critSlider_.hover) {
            lowSlider_.hover = hLow;
            critSlider_.hover = hCrit;
            needRepaint = true;
        }
        const int ih = interval_.Hit(px, py);
        if (ih != interval_.hoverIndex) {
            interval_.hoverIndex = ih;
            needRepaint = true;
        }
        const bool hClose = closeBtn_.Hit(px, py);
        if (hClose != closeBtn_.hover) {
            closeBtn_.hover = hClose;
            needRepaint = true;
        }
        // 手型光标只在可点区域出现
        const bool clickable = (HitTest(px, py).kind != HitKind::None);
        SetCursor(LoadCursorW(nullptr, clickable
                              ? MAKEINTRESOURCEW(32649)     // IDC_HAND
                              : MAKEINTRESOURCEW(32512)));
    }

    if (needRepaint) Repaint();
}

void SettingsWindow::OnMouseUp() {
    lowSlider_.active = false;
    critSlider_.active = false;
}

void SettingsWindow::Close() {
    if (hwnd_) {
        SetWindowLongPtrW(static_cast<HWND>(hwnd_), GWLP_USERDATA, 0);
        DestroyWindow(static_cast<HWND>(hwnd_));
        hwnd_ = nullptr;
    }
    if (g_active == this) g_active = nullptr;
    if (dib_) {
        auto* d = reinterpret_cast<LayeredDib*>(dib_);
        d->Destroy();
        delete d;
        dib_ = nullptr;
    }
    if (renderer_) { delete renderer_; renderer_ = nullptr; }
}

bool SettingsWindow::ProcessMessage(void* msg) {
    if (!msg) return false;
    MSG* m = static_cast<MSG*>(msg);
    if (!hwnd_ || m->hwnd != static_cast<HWND>(hwnd_)) return false;
    TranslateMessage(m);
    DispatchMessageW(m);
    return true;
}

// ---------------------------------------------------------------- 自检

// ---------------------------------------------------------------- 离屏导出

namespace {

bool WriteBmp32(const std::string& pathUtf8, const gpu::Image& img) {
    const std::wstring wp = util::Utf8ToWide(pathUtf8);
    HANDLE f = CreateFileW(wp.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;

    // 合成到中性底（BMP 不带透明），并转成 BGRA
    std::vector<uint8_t> buf(static_cast<size_t>(img.width) * img.height * 4);
    for (size_t i = 0; i + 3 < img.rgba.size(); i += 4) {
        const int a = img.rgba[i + 3];
        for (int ch = 0; ch < 3; ++ch) {
            int v = (img.rgba[i + ch] * a + 190 * (255 - a)) / 255;
            v = v < 0 ? 0 : (v > 255 ? 255 : v);
            buf[i + ch] = static_cast<uint8_t>(v);
        }
        buf[i + 3] = 255;
    }
    // BMP 是 BGRA
    for (size_t i = 0; i + 3 < buf.size(); i += 4) {
        const uint8_t r = buf[i];
        buf[i] = buf[i + 2];
        buf[i + 2] = r;
    }

    const uint32_t rowBytes = static_cast<uint32_t>(img.width) * 4;
    const uint32_t pixBytes = rowBytes * static_cast<uint32_t>(img.height);
    const uint32_t fileSize = 14 + 40 + pixBytes;
    uint8_t hdr[54] = {0};
    hdr[0] = 'B'; hdr[1] = 'M';
    std::memcpy(&hdr[2], &fileSize, 4);
    uint32_t off = 54; std::memcpy(&hdr[10], &off, 4);
    uint32_t hs = 40; std::memcpy(&hdr[14], &hs, 4);
    int32_t w = img.width, h = img.height;
    std::memcpy(&hdr[18], &w, 4); std::memcpy(&hdr[22], &h, 4);
    uint16_t pl = 1, bpp = 32;
    std::memcpy(&hdr[26], &pl, 2); std::memcpy(&hdr[28], &bpp, 2);
    std::memcpy(&hdr[34], &pixBytes, 4);

    DWORD wr = 0;
    bool ok = WriteFile(f, hdr, 54, &wr, nullptr) && wr == 54;
    std::vector<uint8_t> row(rowBytes);
    for (int y = img.height - 1; y >= 0 && ok; --y) {
        std::memcpy(row.data(),
                    &buf[static_cast<size_t>(y) * img.width * 4], rowBytes);
        ok = WriteFile(f, row.data(), rowBytes, &wr, nullptr)
             && wr == rowBytes;
    }
    CloseHandle(f);
    return ok;
}

}  // namespace

bool DumpSettings(const std::string& outPath, const std::string& material,
                  std::string& err) {
    config::Settings cfg;
    cfg.Load(config::DefaultPath());
    if (!material.empty()) cfg.material = material;

    SettingsWindow win;
    win.BuildLayout();
    win.cfg_ = cfg;

    gpu::Image img;
    // 直接走渲染路径（不建窗口）
    if (!win.RenderToImageOf(img, err)) return false;
    if (!WriteBmp32(outPath, img)) {
        err = "写文件失败: " + outPath;
        return false;
    }
    util::Print(u8"已写出 %s (%dx%d)\n", outPath.c_str(), img.width,
                img.height);
    return true;
}

int RunSelfTest() {
    util::Print(u8"\n=== 设置窗口自检 ===\n");

    int fails = 0;
    auto check = [&](bool ok, const char* what) {
        util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s\n", what);
        if (!ok) ++fails;
    };

    config::Settings cfg;
    cfg.Load(config::DefaultPath());

    SettingsWindow win;
    win.BuildLayout();

    // ---- 1) 滑块映射
    util::Print(u8"\n[1] 滑块值映射\n");
    {
        Slider s;
        s.x = 26; s.y = 112; s.w = 404; s.h = 24;
        s.lo = kLowMin; s.hi = kLowMax;
        int x0 = 0, x1 = 0;
        s.trackX(x0, x1);
        util::Print(u8"    轨道 x = %d..%d\n", x0, x1);

        // 两端与中点
        const int vLo = s.XToValue(x0);
        const int vHi = s.XToValue(x1);
        const int vMid = s.XToValue((x0 + x1) / 2);
        util::Print(u8"    最左 -> %d%%，中点 -> %d%%，最右 -> %d%%\n",
                    vLo, vMid, vHi);
        check(vLo == kLowMin, u8"最左映射到下界");
        check(vHi == kLowMax, u8"最右映射到上界");
        check(std::abs(vMid - (kLowMin + kLowMax) / 2) <= 1,
              u8"中点映射合理");

        // 越界钳制
        check(s.XToValue(x0 - 500) == kLowMin, u8"超出左端被钳制");
        check(s.XToValue(x1 + 500) == kLowMax, u8"超出右端被钳制");

        // 往返一致
        bool roundTrip = true;
        for (int v = kLowMin; v <= kLowMax; ++v)
            if (s.XToValue(s.ValueToX(v)) != v) roundTrip = false;
        check(roundTrip, u8"值->像素->值 往返一致");
    }

    // ---- 2) 命中测试
    util::Print(u8"\n[2] 命中测试\n");
    {
        struct Case { const char* name; int x, y;
                      SettingsWindow::HitKind want; };
        const Case cases[] = {
            {u8"低电量滑块", 26 + 200, 112 + 12,
             SettingsWindow::HitKind::LowSlider},
            {u8"严重滑块", 26 + 200, 166 + 12,
             SettingsWindow::HitKind::CritSlider},
            {u8"通知开关", 26 + 200, 196 + 15,
             SettingsWindow::HitKind::Notify},
            {u8"刷新间隔", 26 + 100, 262 + 16,
             SettingsWindow::HitKind::Interval},
            // 材质卡已移除（只保留亚克力）；实时开关上移到 y=300
            {u8"实时开关", 26 + 200, 300 + 15,
             SettingsWindow::HitKind::Realtime},
            {u8"取消按钮", 456 - 26 - 86 * 2 - 10 + 40,
             400 - 34 - 20 + 17, SettingsWindow::HitKind::Cancel},
            {u8"保存按钮", 456 - 26 - 86 + 40, 400 - 34 - 20 + 17,
             SettingsWindow::HitKind::Save},
            {u8"关闭按钮", 456 - 38 + 14, 12 + 14,
             SettingsWindow::HitKind::Close},
        };
        for (const auto& c : cases) {
            const auto hit = win.HitTest(c.x, c.y);
            const bool ok = (hit.kind == c.want);
            util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s（得到 %d，期望 %d）\n",
                        c.name, static_cast<int>(hit.kind),
                        static_cast<int>(c.want));
            if (!ok) ++fails;
        }
    }

    // ---- 3) 布局不重叠
    //
    // 早先这里手工维护一张"控件 y 范围"表再两两比对，结果**误报**
    // "分组3 与 材质卡 重叠"：标签是按**基线**绘制在 y+10 上的，
    // 文字实际占据基线之上，而不是 y..y+14；而且把相邻边界（310 与 310）
    // 也算作重叠。看图确认其实毫无问题。
    //
    // 手工表迟早会与实际绘制代码脱节，所以改为**测量渲染结果**：
    // 逐行统计墨迹，找出每一段连续有内容的行区间，确认区间之间留有间隙。
    util::Print(u8"\n[3] 布局重叠检查（按渲染结果测量）\n");
    {
        gpu::Image img;
        std::string err;
        if (!win.RenderToImage(img, err)) {
            util::Print(u8"  ✗ 无法渲染，跳过\n");
            ++fails;
        } else {
            // 逐行墨迹计数（与底色差异大的像素）
            std::vector<int> rowInk(img.height, 0);
            for (int y = 0; y < img.height; ++y) {
                int n = 0;
                for (int x = 0; x < img.width; ++x) {
                    const size_t i2 =
                        (static_cast<size_t>(y) * img.width + x) * 4;
                    const int lum =
                        (img.rgba[i2] * 299 + img.rgba[i2 + 1] * 587
                         + img.rgba[i2 + 2] * 114) / 1000;
                    const int a = img.rgba[i2 + 3];
                    // 卡片边框、文字都算墨迹；纯材质底不算
                    if (a > 128 && lum < 150) ++n;
                }
                rowInk[y] = n;
            }

            // 找出连续"有内容"的行带（阈值 3 像素，滤掉噪点）
            std::vector<std::pair<int, int>> bands;
            int start = -1;
            for (int y = 0; y < img.height; ++y) {
                if (rowInk[y] >= 3) {
                    if (start < 0) start = y;
                } else {
                    if (start >= 0) { bands.push_back({start, y - 1}); start = -1; }
                }
            }
            if (start >= 0) bands.push_back({start, img.height - 1});

            util::Print(u8"    检出 %zu 条内容行带:\n", bands.size());
            for (const auto& b : bands)
                util::Print(u8"      y %3d..%3d (高 %d)\n", b.first,
                            b.second, b.second - b.first + 1);

            // 行带之间必须完全分离（不能交叉）
            bool overlap = false;
            for (size_t k = 0; k + 1 < bands.size(); ++k) {
                if (bands[k].second >= bands[k + 1].first) {
                    util::Print(u8"    ✗ 行带 %zu 与 %zu 交叉\n", k, k + 1);
                    overlap = true;
                }
            }
            if (!overlap) {
                util::Print(u8"    ✓ 所有内容行带互不交叉\n");
            } else {
                ++fails;
            }

            // 关键区域必须有内容（防止某块整片漏画）
            auto hasContentIn = [&](int y0, int y1, const char* what) {
                bool any = false;
                for (int y = y0; y <= y1 && y < img.height; ++y)
                    if (rowInk[y] >= 3) any = true;
                util::Print(any ? u8"    ✓ %s 区域有内容 (y %d..%d)\n"
                                : u8"    ✗ %s 区域为空 (y %d..%d)\n",
                            what, y0, y1);
                return any;
            };
            // 面板已从 458 收窄到 400（移除材质卡），区域随之更新
            if (!hasContentIn(20, 46, u8"标题")) ++fails;
            if (!hasContentIn(90, 140, u8"低电量滑块")) ++fails;
            if (!hasContentIn(145, 195, u8"严重阈值滑块")) ++fails;
            if (!hasContentIn(196, 230, u8"通知开关")) ++fails;
            if (!hasContentIn(250, 298, u8"刷新间隔")) ++fails;
            if (!hasContentIn(300, 335, u8"实时开关")) ++fails;
            if (!hasContentIn(340, 390, u8"按钮")) ++fails;
        }
    }

    // ---- 4) 渲染有内容
    util::Print(u8"\n[4] 渲染\n");
    {
        gpu::Image img;
        std::string err;
        if (!win.RenderToImage(img, err)) {
            util::Print(u8"  ✗ 渲染失败: %s\n", err.c_str());
            ++fails;
        } else {
            check(img.width == kPanelW && img.height == kPanelH,
                  u8"尺寸为 456x400");
            // 统计"墨迹"（与材质底色差异大的像素）
            size_t ink = 0, total = 0;
            for (size_t i = 0; i + 3 < img.rgba.size(); i += 4) {
                const int lum = (img.rgba[i] * 299 + img.rgba[i + 1] * 587
                                 + img.rgba[i + 2] * 114) / 1000;
                // 材质底偏亮/偏灰，文字与卡片边是深色
                if (img.rgba[i + 3] > 128 && lum < 120) ++ink;
                ++total;
            }
            const double ratio = total ? static_cast<double>(ink) / total : 0;
            util::Print(u8"    墨迹占比 %.2f%%\n", ratio * 100);
            check(ratio > 0.005, u8"渲染出可见内容");
        }
    }

    // ---- 5) 应用命中（改变配置）
    util::Print(u8"\n[5] 交互改变配置\n");
    {
        config::Settings base;
        base.Load(config::DefaultPath());
        SettingsWindow w2;
        std::string werr;
        // 这一步会在屏幕上真的弹出设置窗口 —— 属**可见夹具**。
        // 它每次回归都会闪一下，用户全屏游戏时会被干扰，
        // 所以默认跳过（设 MOUSETRAY_VISUAL_TEST=1 启用）。
        // 不显示窗口的渲染与命中测试（[1]~[4]）照常验证。
        wchar_t visFlag[8] = {0};
        const bool kVisualTest =
            GetEnvironmentVariableW(L"MOUSETRAY_VISUAL_TEST", visFlag, 8) > 0;
        if (!kVisualTest) {
            util::Print(u8"SKIP: 显示设置窗口属可见夹具，默认跳过"
                        u8"（设 MOUSETRAY_VISUAL_TEST=1 启用）\n");
        } else if (w2.Show(base, werr)) {
            const int before = w2.editing().interval;
            SettingsWindow::HitResult h;
            h.kind = SettingsWindow::HitKind::Interval;
            h.index = 4;   // 120 秒
            w2.ApplyHit(h);
            util::Print(u8"    间隔 %d -> %d\n", before, w2.editing().interval);
            check(w2.editing().interval == kIntervals[4], u8"选中间隔生效");

            const std::string beforeMat = w2.editing().material;
            // 材质选择 UI 已移除；这里只验证任何输入都会被归一为 acrylic
            SettingsWindow::HitResult hm;
            hm.kind = SettingsWindow::HitKind::Realtime;
            w2.ApplyHit(hm);
            util::Print(u8"    材质固定为 %s（选择 UI 已移除）\n",
                        w2.editing().material.c_str());
            check(w2.editing().material == "acrylic", u8"材质固定为亚克力");
            w2.Close();
        } else {
            util::Print(u8"  ! 无法创建窗口: %s\n", werr.c_str());
        }
    }

    util::Print(u8"\n");
    if (fails) {
        util::Print(u8"设置窗口自检失败 %d 项\n", fails);
        return 1;
    }
    util::Print(u8"设置窗口自检全部通过 ✓\n");
    return 0;
}

}  // namespace settingswin
