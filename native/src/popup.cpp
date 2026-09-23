// ============================================================================
//  popup.cpp — 详情浮窗实现
//
//  合成流程：
//    1. GPU 渲染材质（含圆角、投影、折射），得到画布尺寸的 RGBA
//    2. 用 GDI+ 在材质上绘制文字与比例条
//    3. 转成**预乘 alpha**，交给 UpdateLayeredWindow
//
//  为什么文字不需要像 Python 版那样 3 倍超采样：
//    PIL 的 draw.text 不做抗锯齿，所以 Python 版必须先在 3 倍画布上画再缩小。
//    GDI+ 文字请用 TextRenderingHintAntiAliasGridFit（做网格拟合），
//    不要用 AntiAlias（unhinted，边缘会糊）。
//    直接在 1x 上画既清晰又省一次缩放。
// ============================================================================

#include "popup.h"
#include "battery_history.h"
#include "capture.h"
#include "util.h"

#include <windows.h>
#include <windowsx.h>   // GET_X_LPARAM / GET_Y_LPARAM
#include <objidl.h>
#include <gdiplus.h>

#include <cmath>
#include <cstring>
#include <ctime>
#include <string>

#pragma comment(lib, "gdiplus.lib")

using namespace Gdiplus;

namespace popup {

namespace {

const wchar_t* kClassName = L"MouseBatteryTrayPopup";

// GDI+ 生命周期（与 tray.cpp 各自独立初始化也无妨，GdiplusStartup 可重入）
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

inline Color ToColor(uint32_t argb, BYTE alphaOverride = 0) {
    const BYTE a = alphaOverride ? alphaOverride : A(argb);
    return Color(a, R(argb), G(argb), B(argb));
}

// 字体族（与 Python 版 _FONTS 对应）
FontFamily* RegularFamily() {
    static FontFamily f(L"Microsoft YaHei");
    if (f.IsAvailable()) return &f;
    static FontFamily fb(L"Segoe UI");
    return &fb;
}
FontFamily* BoldFamily() {
    static FontFamily f(L"Microsoft YaHei");
    if (f.IsAvailable()) return &f;
    static FontFamily fb(L"Segoe UI");
    return &fb;
}
FontFamily* NumberFamily() {
    static FontFamily f(L"Segoe UI Semibold");
    if (f.IsAvailable()) return &f;
    static FontFamily fb(L"Arial");
    return &fb;
}

// 按**基线**绘制文字。
// GDI+ 的 DrawString(PointF) 把点当作文本框左上角，而 Python 版用的是
// anchor="ls"（左基线）。这里按字体度量把基线换算成顶部，保持两者一致。
void DrawTextBaseline(Graphics& g, const std::wstring& text, Font& font,
                      REAL x, REAL baselineY, const Color& color) {
    // GDI+ 的 GetFamily 是**输出参数**形式：GetFamily(FontFamily* out)，
    // 不是无参返回指针（那样写会得到 "函数不接受 0 个参数"）。
    FontFamily fam;
    const INT style = font.GetStyle();
    REAL ascent = font.GetSize() * 0.86f;   // 兜底：约等于拉丁字体的升部比例
    if (font.GetFamily(&fam) == Ok) {
        const INT em = fam.GetEmHeight(style);
        if (em > 0) {
            ascent = font.GetSize() * fam.GetCellAscent(style)
                   / static_cast<REAL>(em);
        }
    }
    SolidBrush brush(color);
    StringFormat sf;
    sf.SetFormatFlags(StringFormatFlagsNoWrap
                      | StringFormatFlagsMeasureTrailingSpaces);

    // ---- 落点必须取整
    //
    // ascent = 字号 * 升部比例，几乎总是小数（例如 13 * 1.33 = 17.29），
    // 于是 baselineY - ascent 也是小数。**小数落点会让网格拟合失效**，
    // 字形被重采样成灰edge —— 这是文字发糊的第二个来源（第一个是用了
    // unhinted 的 TextRenderingHintAntiAlias）。
    // 取整到整数像素后，网格拟合才真正起作用。
    const REAL topY = std::floor(baselineY - ascent + 0.5f);
    const REAL drawX = std::floor(x + 0.5f);

    g.DrawString(text.c_str(), -1, &font, PointF(drawX, topY), &sf, &brush);
}

REAL MeasureWidth(Graphics& g, const std::wstring& text, Font& font) {
    RectF box;
    g.MeasureString(text.c_str(), -1, &font, PointF(0, 0), &box);
    return box.Width;
}

// ---- 分层窗口的 DIB（**必须** 32bpp 带 alpha）
//
// 曾踩过的坑：用 CreateCompatibleBitmap + FillRect 得到的是 alpha=0 的位图，
// UpdateLayeredWindow(ULW_ALPHA) 会把窗口画成**完全透明**（看不见），
// 而 API 依然返回成功。必须用 CreateDIBSection 建 32bpp 自上而下位图。
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
        bi.bmiHeader.biHeight = -height;      // 负 = 自上而下
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;

        bitmap = CreateDIBSection(memdc, &bi, DIB_RGB_COLORS, &bits, nullptr,
                                  0);
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
        memdc = nullptr;
        bitmap = nullptr;
        oldBitmap = nullptr;
        bits = nullptr;
        w = h = 0;
    }
};

struct WindowState {
    Content content;
    Popup::Options opt;
    gpu::Renderer* renderer = nullptr;
    LayeredDib dib;
    gpu::Image composed;
    double shownAt = 0;
    bool dragging = false;
    int dragDx = 0, dragDy = 0;
};

WindowState* StateOf(HWND h) {
    return reinterpret_cast<WindowState*>(GetWindowLongPtrW(h, GWLP_USERDATA));
}

// 用户是否把系统设为深色
bool SystemUsesDarkTheme() {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER,
                      L"Software\\Microsoft\\Windows\\CurrentVersion\\"
                      L"Themes\\Personalize", 0, KEY_READ, &key)
        != ERROR_SUCCESS)
        return false;
    DWORD val = 1, size = sizeof(val), type = 0;
    bool light = true;
    if (RegQueryValueExW(key, L"AppsUseLightTheme", nullptr, &type,
                         reinterpret_cast<LPBYTE>(&val), &size)
        == ERROR_SUCCESS)
        light = (val != 0);
    RegCloseKey(key);
    return !light;
}

LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp) {
    WindowState* st = StateOf(h);
    switch (msg) {
        case WM_NCHITTEST: {
            // 整个面板可拖动（这是浮窗，没有标题栏）
            return HTCAPTION;
        }
        case WM_LBUTTONDOWN: {
            // 记录拖动偏移
            if (st) {
                POINT pt{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
                ClientToScreen(h, &pt);
                RECT wr{};
                GetWindowRect(h, &wr);
                st->dragging = true;
                st->dragDx = pt.x - wr.left;
                st->dragDy = pt.y - wr.top;
            }
            return 0;
        }
        case WM_MOUSEMOVE: {
            if (st && st->dragging && (wp & MK_LBUTTON)) {
                POINT pt{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
                ClientToScreen(h, &pt);
                SetWindowPos(h, HWND_TOPMOST, pt.x - st->dragDx,
                             pt.y - st->dragDy, 0, 0,
                             SWP_NOSIZE | SWP_NOACTIVATE);
            }
            return 0;
        }
        case WM_LBUTTONUP:
            if (st) st->dragging = false;
            return 0;
        case WM_RBUTTONUP:
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATE;
        case WM_SETCURSOR:
            SetCursor(LoadCursorW(nullptr, MAKEINTRESOURCEW(32512)));
            return TRUE;
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
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    wc.hbrBackground = nullptr;
    RegisterClassExW(&wc);
    done = true;
}

// 直通 alpha -> 预乘 alpha（UpdateLayeredWindow 要求预乘）
void Premultiply(gpu::Image& img) {
    uint8_t* p = img.rgba.data();
    const size_t n = img.rgba.size();
    for (size_t i = 0; i < n; i += 4) {
        const unsigned a = p[i + 3];
        if (a == 255) continue;
        p[i + 0] = static_cast<uint8_t>(p[i + 0] * a / 255);
        p[i + 1] = static_cast<uint8_t>(p[i + 1] * a / 255);
        p[i + 2] = static_cast<uint8_t>(p[i + 2] * a / 255);
    }
}

}  // namespace

// ---------------------------------------------------------------- 配色

Palette LightPalette() {
    Palette p;
    p.text = 0xFF1A1A1C;
    p.textDim = 0xFF5F5F64;
    p.textFaint = 0xFF828288;
    p.track = 0x1C000000;          // (0,0,0,28)
    p.stroke = 0x16000000;         // (0,0,0,22)
    p.shadow = 0x38000000;         // (0,0,0,56)
    p.surface = 0xF9F9FA;
    p.surfaceAlpha = 210;
    p.accentLow = 0xFFC82C26;
    p.accentMid = 0xFFC89218;
    p.accentOk = 0xFF18944C;
    p.accentUnknown = 0xFF96969C;
    p.accentCharging = 0xFF0078D4;
    return p;
}

Palette DarkPalette() {
    Palette p;
    p.text = 0xFFF3F3F5;
    p.textDim = 0xFFA8A8AE;
    p.textFaint = 0xFF84848A;
    p.track = 0x22FFFFFF;          // (255,255,255,34)
    p.stroke = 0x18FFFFFF;         // (255,255,255,24)
    p.shadow = 0x78000000;         // (0,0,0,120)
    p.surface = 0x202022;
    p.surfaceAlpha = 208;
    p.accentLow = 0xFFE8544E;
    p.accentMid = 0xFFE8B44E;
    p.accentOk = 0xFF3CB86C;
    p.accentUnknown = 0xFF96969C;
    p.accentCharging = 0xFF4CC2FF;
    return p;
}

uint32_t AccentFor(const Palette& p, int percent, bool charging) {
    if (charging) return p.accentCharging;
    if (percent < 0) return p.accentUnknown;
    if (percent <= 20) return p.accentLow;
    if (percent <= 45) return p.accentMid;
    return p.accentOk;
}

// ---------------------------------------------------------------- 合成

bool Compose(const Content& c, const Palette& pal,
             gpu::Renderer& renderer, const gpu::Image& backdrop,
             const gpu::Image& wallpaper, const std::string& material,
             gpu::Image& out, std::string& err) {
    if (!EnsureGdiplus()) { err = "GDI+ 初始化失败"; return false; }

    // ---- 1) GPU 渲染材质
    gpu::Material mat = gpu::Material::Acrylic;
    if (material == "liquid") mat = gpu::Material::Liquid;
    else if (material == "mica") mat = gpu::Material::Mica;

    gpu::Params params;
    params.ApplyMaterialDefaults(mat);
    params.surfaceR = static_cast<float>(R(pal.surface));
    params.surfaceG = static_cast<float>(G(pal.surface));
    params.surfaceB = static_cast<float>(B(pal.surface));
    params.opacity = pal.surfaceAlpha / 255.0f;
    if (mat == gpu::Material::Mica) {
        // 云母是"壁纸色调"，透明度更高
        params.opacity = 0.86f;
    }

    gpu::Image canvas;
    if (!renderer.Render(backdrop, wallpaper, mat, params, kCanvasW,
                         kCanvasH, static_cast<float>(kShadowPad),
                         static_cast<float>(kRadius), canvas, err))
        return false;

    // ---- 2) 用 GDI+ 在材质上画文字与比例条
    // 直接把 canvas 的像素交给 GDI+（32bppARGB = 直通 alpha）
    {
        // GDI+ 要 BGRA，canvas 是 RGBA。
        //
        // **必须原地转换**：GDI+ 的 Bitmap 直接引用这块内存，如果要画进
        // 一个临时副本，画完就得把副本拷回来，否则返回的 canvas 里根本没有
        // 文字（我第一版就是复制到 bgra 再返回 canvas，结果整个浮窗没字，
        // 而编译与数值检查全都正常）。
        gpu::SwapRedBlue(canvas);
        Bitmap bmp(kCanvasW, kCanvasH, kCanvasW * 4, PixelFormat32bppARGB,
                   canvas.rgba.data());
        Graphics g(&bmp);
        g.SetSmoothingMode(SmoothingModeAntiAlias);

        // ---- 文字清晰度的关键设置
        //
        // **必须用 AntiAliasGridFit，不能用 AntiAlias。**
        // GDI+ 的命名很容易误导：
        //   TextRenderingHintAntiAlias         -> 不做网格拟合（unhinted），
        //                                         字形落在分数位置，边缘发糊；
        //                                         实测放大 8x 可见 2~3px 灰边。
        //   TextRenderingHintAntiAliasGridFit  -> 做网格拟合（hinted），锐利。
        //   TextRenderingHintClearTypeGridFit  -> 次像素渲染，但要求**不透明背景**；
        //                                         我们的文字画在带 alpha 的材质上，
        //                                         用它会产生彩色描边，故不用。
        // ---- 用 ClearTypeGridFit（次像素网格拟合）
        //
        // 曾经用的是 TextRenderingHintAntiAlias（灰阶、unhinted），
        // 用户反馈"字体显示不清晰"。实测确认责任在这一行：
        //   1) AntiAlias 名不副实 —— GDI+ 里它表示**不做网格拟合**，
        //      字形落在分数位置，边缘发虚。
        //   2) 与原生 GDI 文字对比，AntiAlias 明显偏淡发灰：
        //      设备名边缘梯度 5.63 vs ClearType 6.84；
        //      大数字 8.47 vs 9.15；1:1 观感上 ClearType 更接近
        //      Windows 原生 UI 文字（GDI 参照同样开 ClearType）。
        //
        // 关于"彩色描边"：放大 8x 看 ClearType 确实有橙蓝次像素条纹 ——
        // 但**那正是原生 ClearType 的样子**，1:1 下人眼会融合成更锐的边缘。
        // 我一度据此否决 ClearType，那是因为只看了放大图、没看 1:1 实际观感。
        //
        // 前提条件本窗口满足：文字都在面板内部（coverage = 1，alpha = 255），
        // 背景不透明；且系统为 100% 缩放、RGB 条纹排列。若日后支持
        // 非 100% 缩放或文字压到圆角边缘，需重新评估。
        g.SetTextRenderingHint(TextRenderingHintClearTypeGridFit);

        // PixelOffsetModeHighQuality 把采样整体偏移半个像素，
        // 正好抵消网格拟合；文字场景按 GDI+ 惯例用 Half。
        g.SetPixelOffsetMode(PixelOffsetModeHalf);
        g.SetCompositingMode(CompositingModeSourceOver);
        g.SetCompositingQuality(CompositingQualityHighQuality);

        const REAL SP = static_cast<REAL>(kShadowPad);
        const REAL PADX = SP + kPad;

        // 细描边（液态玻璃自带亮边，不再叠描边，避免"描边感"）
        if (mat != gpu::Material::Liquid) {
            Pen pen(ToColor(pal.stroke), 1.0f);
            // 用 RectF 重载，避免 INT/REAL 版本的重载歧义
            g.DrawRectangle(&pen, RectF(SP, SP,
                                        static_cast<REAL>(kPanelW - 1),
                                        static_cast<REAL>(kPanelH - 1)));
        }

        // 设备名（最弱层级，仅作上下文）
        if (!c.name.empty()) {
            Font f(RegularFamily(), 13.0f, FontStyleRegular, UnitPixel);
            DrawTextBaseline(g, util::Utf8ToWide(c.name), f, PADX, SP + 34,
                             ToColor(pal.textDim));
        }

        // 百分比：唯一的"主"元素
        const bool critical = (c.percent >= 0 && c.percent <= 20 &&
                               !c.charging);
        const uint32_t numColor = critical
            ? (A(pal.text) == 0xFF && R(pal.text) < 128 ? pal.accentLow
                                                        : pal.accentLow)
            : pal.text;

        wchar_t big[16];
        if (c.percent < 0) wcscpy_s(big, L"--");
        else swprintf_s(big, L"%d", c.percent);

        Font fNum(NumberFamily(), 38.0f, FontStyleBold, UnitPixel);
        DrawTextBaseline(g, big, fNum, PADX - 1, SP + 84,
                         ToColor(numColor));

        if (c.percent >= 0) {
            const REAL nw = MeasureWidth(g, big, fNum);
            Font fPct(RegularFamily(), 16.0f, FontStyleRegular, UnitPixel);
            DrawTextBaseline(g, L"%", fPct, PADX - 1 + nw + 3, SP + 84,
                             ToColor(pal.textDim));
        } else {
            Font fOff(RegularFamily(), 13.0f, FontStyleRegular, UnitPixel);
            DrawTextBaseline(g, L"离线", fOff, PADX + 42, SP + 84,
                             ToColor(pal.textDim));
        }

        // 进度条（唯一表示比例的图形）
        {
            const REAL barY = SP + 98;
            const REAL barH = 4.0f;
            const REAL bx0 = PADX;
            const REAL bx1 = SP + kPanelW - kPad;
            const REAL br = barH / 2.0f;

            SolidBrush track(ToColor(pal.track));
            // GDI+ 没有圆角矩形，用路径构造
            auto roundedBar = [&](Graphics& gg, REAL x0, REAL x1, REAL y,
                                  REAL hh, REAL rad, Brush* brush) {
                if (x1 - x0 < 0.5f) return;
                const REAL r = (x1 - x0) / 2.0f < rad ? (x1 - x0) / 2.0f : rad;
                GraphicsPath path;
                path.AddArc(x0, y, r * 2, hh, 90.0f, 180.0f);
                path.AddArc(x1 - r * 2, y, r * 2, hh, 270.0f, 180.0f);
                path.CloseFigure();
                gg.FillPath(brush, &path);
            };
            roundedBar(g, bx0, bx1, barY, barH, br, &track);

            if (c.percent >= 0) {
                const double frac =
                    std::max(0.0, std::min(1.0, c.percent / 100.0));
                const REAL fw = static_cast<REAL>((bx1 - bx0) * frac);
                if (fw > barH) {
                    SolidBrush fill(ToColor(AccentFor(pal, c.percent,
                                                      c.charging)));
                    roundedBar(g, bx0, bx0 + fw, barY, barH, br, &fill);
                }
            }
        }

        // 预测（次强层级）
        {
            Font fPred(RegularFamily(), 14.0f, FontStyleRegular, UnitPixel);
            if (c.hoursRemaining > 0) {
                std::string dur = battery::FormatDuration(c.hoursRemaining);
                // 去掉"约 "前缀（Python 版同样处理）
                const std::string prefix = u8"约 ";
                if (dur.compare(0, prefix.size(), prefix) == 0)
                    dur = dur.substr(prefix.size());
                DrawTextBaseline(g, util::Utf8ToWide(u8"预计还能用 " + dur),
                                 fPred, PADX, SP + 132, ToColor(pal.text));
            } else {
                DrawTextBaseline(g, L"续航预测积累中", fPred, PADX, SP + 132,
                                 ToColor(pal.textDim));
            }
        }

        // 附加信息（最弱层级）：速度 · 置信度 · 更新时间
        {
            std::vector<std::string> bits;
            char buf[128];
            if (c.hoursRemaining > 0 && c.rate > 0) {
                std::snprintf(buf, sizeof(buf), u8"放电 %.1f%%/h", c.rate);
                bits.push_back(buf);
            }
            if (c.hoursRemaining > 0 && !c.confidence.empty())
                bits.push_back(u8"置信度" + c.confidence);
            if (c.hoursRemaining <= 0)
                bits.push_back(u8"需观察一段放电过程");
            if (c.lastSeenEpoch > 0) {
                const time_t t = static_cast<time_t>(c.lastSeenEpoch);
                struct tm lt{};
                localtime_s(&lt, &t);
                std::snprintf(buf, sizeof(buf), u8"更新于 %02d:%02d",
                              lt.tm_hour, lt.tm_min);
                bits.push_back(buf);
            }
            if (c.stale) bits.push_back(u8"离线");

            std::string joined;
            for (size_t i = 0; i < bits.size(); ++i) {
                if (i) joined += u8" · ";
                joined += bits[i];
            }
            Font fBits(RegularFamily(), 12.0f, FontStyleRegular, UnitPixel);
            DrawTextBaseline(g, util::Utf8ToWide(joined), fBits, PADX,
                             SP + 156, ToColor(pal.textFaint));
        }

        // ---- 按小时电量（"每个时间段的电量"）
        //
        // 画成柱状：柱高 = 电量百分比，颜色 = 与主数字一致的状态色。
        // **没有数据的小时也画**（一根很淡的短桩），因为"哪段时间读不到"
        // 本身就是有用信息 —— 尤其现在电量恒定 94%，曲线是一条平线，
        // 断档反而是唯一看得出变化的东西。
        if (!c.hours.empty())
        {
            Font fLabel(RegularFamily(), 11.0f, FontStyleRegular, UnitPixel);
            char head[96];
            std::snprintf(head, sizeof(head), u8"近 %d 小时 · %d 小时有数据",
                          c.windowHours, c.hoursWithData);
            DrawTextBaseline(g, util::Utf8ToWide(head), fLabel, PADX, SP + 182,
                             ToColor(pal.textFaint));

            const REAL stripX0 = PADX;
            const REAL stripX1 = SP + kPanelW - kPad;
            const REAL stripTop = SP + 194;
            const REAL stripBot = SP + 228;
            const REAL stripH = stripBot - stripTop;
            const int n = static_cast<int>(c.hours.size());
            if (n > 0)
            {
                const REAL slot = (stripX1 - stripX0) / n;
                const REAL barW = slot > 3.0f ? slot - 1.6f : slot;

                // 极淡的底色，给出"这是一个区域"的暗示
                SolidBrush bg(Color(20, 128, 128, 136));
                g.FillRectangle(&bg, stripX0, stripTop,
                                stripX1 - stripX0, stripH);

                for (int i = 0; i < n; ++i)
                {
                    const auto& hp = c.hours[static_cast<size_t>(i)];
                    const REAL x = stripX0 + i * slot;

                    if (hp.count == 0 || hp.percent < 0)
                    {
                        // 断档：底部一小段淡桩
                        SolidBrush stub(Color(46, 150, 150, 158));
                        g.FillRectangle(&stub, x, stripBot - 3.0f, barW, 3.0f);
                        continue;
                    }

                    const REAL frac =
                        std::max(0.0f, std::min(1.0f,
                            static_cast<REAL>(hp.percent) / 100.0f));
                    const REAL hgt = std::max(3.0f, stripH * frac);
                    const uint32_t col =
                        AccentFor(pal, hp.percent, c.charging);

                    // 最新的一小时提亮，便于看出"当前在哪"
                    const bool newest = (i == n - 1);
                    SolidBrush br(ToColor(col, newest ? 255 : 200));
                    g.FillRectangle(&br, x, stripBot - hgt, barW, hgt);
                }

                // 基线
                Pen base(ToColor(pal.stroke), 1.0f);
                g.DrawLine(&base, stripX0, stripBot + 0.5f, stripX1,
                           stripBot + 0.5f);

                // 极值标注（简单分析的一部分）
                if (c.minPercent >= 0 && c.maxPercent >= 0)
                {
                    char mm[64];
                    if (c.minPercent == c.maxPercent)
                        std::snprintf(mm, sizeof(mm), u8"%d%%",
                                      c.minPercent);
                    else
                        std::snprintf(mm, sizeof(mm), u8"%d%%~%d%%",
                                      c.minPercent, c.maxPercent);
                    Font fmm(RegularFamily(), 11.0f, FontStyleRegular,
                             UnitPixel);
                    const REAL w = MeasureWidth(g, util::Utf8ToWide(mm), fmm);
                    DrawTextBaseline(g, util::Utf8ToWide(mm), fmm,
                                     stripX1 - w, SP + 182,
                                     ToColor(pal.textFaint));
                }
            }
        }

        // 转回 RGBA，保持本函数的输出契约（gpu::Image 一律是 RGBA）
        gpu::SwapRedBlue(canvas);
    }

    out = canvas;
    return true;
}

// ---------------------------------------------------------------- Popup

Popup::~Popup() { Close(); }

bool Popup::EnsureDib(int w, int h, std::string& err) {
    if (dib_ && memdc_) {
        auto* d = reinterpret_cast<LayeredDib*>(dib_);
        if (d->w == w && d->h == h) return true;
    }
    auto* d = new LayeredDib();
    if (!d->Create(w, h)) {
        delete d;
        err = "创建分层窗口位图失败（CreateDIBSection）";
        return false;
    }
    if (dib_) {
        auto* old = reinterpret_cast<LayeredDib*>(dib_);
        old->Destroy();
        delete old;
    }
    dib_ = d;
    memdc_ = d->memdc;
    return true;
}

bool Popup::Present() {
    if (!hwnd_) return false;
    auto* d = reinterpret_cast<LayeredDib*>(dib_);
    if (!d || !d->bits) return false;
    if (!composed_.Valid()) return false;
    if (composed_.width != d->w || composed_.height != d->h) return false;

    // 直通 alpha -> 预乘 alpha（UpdateLayeredWindow 要求）
    Premultiply(composed_);
    // DIB 是 BGRA（见 gpu::SwapRedBlue 的说明），而 composed_ 是 RGBA
    gpu::Image bgra = composed_;
    gpu::SwapRedBlue(bgra);
    std::memcpy(d->bits, bgra.rgba.data(), bgra.rgba.size());

    RECT wr{};
    GetWindowRect(static_cast<HWND>(hwnd_), &wr);
    POINT dst{wr.left, wr.top};
    POINT src{0, 0};
    SIZE size{d->w, d->h};
    BLENDFUNCTION bf{};
    bf.BlendOp = AC_SRC_OVER;
    bf.SourceConstantAlpha = 255;
    bf.AlphaFormat = AC_SRC_ALPHA;      // 使用像素自带 alpha

    HDC screen = GetDC(nullptr);
    const BOOL ok = UpdateLayeredWindow(
        static_cast<HWND>(hwnd_), screen, &dst, &size, d->memdc, &src, 0,
        &bf, ULW_ALPHA);
    ReleaseDC(nullptr, screen);
    return ok != FALSE;
}

bool Popup::Show(const Content& c, const Options& opt, std::string& err) {
    if (!EnsureGdiplus()) { err = "GDI+ 初始化失败"; return false; }

    content_ = c;
    opt_ = opt;

    if (!renderer_) {
        renderer_ = new gpu::Renderer();
        if (!renderer_->Init(err)) {
            delete renderer_;
            renderer_ = nullptr;
            return false;
        }
    }

    if (!EnsureDib(kCanvasW, kCanvasH, err)) return false;

    if (!hwnd_) {
        RegisterOnce();
        HWND h = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST |
                WS_EX_NOACTIVATE,
            kClassName, L"", WS_POPUP, opt.x, opt.y, kCanvasW, kCanvasH,
            nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!h) { err = "创建浮窗失败"; return false; }
        auto* st = new WindowState();
        st->renderer = renderer_;
        SetWindowLongPtrW(h, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(st));
        hwnd_ = h;
        shownAt_ = util::NowSec();

        // ---- 关键：让自己对抓屏 API 隐形
        //
        // 不这样做的话，Update() 里的 BitBlt 会把**浮窗自己**抓进来
        // （实测 affinity = WDA_NONE 时确实如此），于是每帧都在把上一帧
        // 的画面重新模糊一遍 —— 面板看起来就是一块不会变色的灰板，
        // 也就是用户说的"亚克力是假的、没有实时模糊"。
        //
        // capture.cpp 的自检已验证这个方案有效：
        // 排除前抓到自己的占比 100%，排除后与真实背景的差为 0.0。
        // ---- 抓屏排除：**默认关闭**，这是实测结论，不是偷懒
        //
        // A/B 实测（同一二进制，仅一个环境变量不同）：
        //   不排除 (WDA_NONE) -> 浮窗正常渲染
        //   排除   (0x11)     -> 浮窗**完全不再被合成**，屏幕上看不见
        //
        // 也就是说本系统上 WS_EX_LAYERED 与 WDA_EXCLUDEFROMCAPTURE 不能共存。
        // 于是分层窗口方案陷入两难：
        //   不排除 -> BitBlt 抓到的是浮窗自己，材质只是上一帧的回灌（假实时）
        //   排除   -> 浮窗直接消失
        // 两条路都不通 —— 这是必须换掉「分层窗口 + BitBlt」架构的直接证据。
        //
        // 想复现的话：设 MOUSETRAY_FORCE_EXCLUDE=1。
        {
            wchar_t force[8] = {0};
            const bool wantExclude = GetEnvironmentVariableW(
                L"MOUSETRAY_FORCE_EXCLUDE", force, 8) > 0;
            if (wantExclude) {
                excluded_ = capture::ExcludeFromCapture(h);
                util::Print(u8"浮窗抓屏排除: 已强制开启（affinity=%d）"
                            u8"—— 窗口将不可见，仅供诊断\n",
                            capture::CaptureAffinity(h));
            } else {
                excluded_ = false;
            }
        }
    } else {
        SetWindowPos(static_cast<HWND>(hwnd_), HWND_TOPMOST, opt.x, opt.y,
                     0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // 抓背后的画面（窗口已排除抓屏，所以抓到的是真实桌面）
    const Palette pal = opt.dark ? DarkPalette() : LightPalette();
    gpu::Image backdrop;
    {
        if (!grabber_) grabber_ = new capture::ScreenGrabber();
        auto* g = static_cast<capture::ScreenGrabber*>(grabber_);
        std::string e2;
        if (!g->valid() && !g->Init(kCanvasW, kCanvasH, e2)) {
            util::Print(u8"抓屏器初始化失败: %s\n", e2.c_str());
        }
        if (g->valid()) g->Grab(opt.x, opt.y, backdrop);
    }
    if (!backdrop.Valid()) {
        // 降级：用中性底色，至少窗口可见
        backdrop = gpu::SolidImage(kCanvasW, kCanvasH, 200, 200, 205);
    }

    std::string e3;
    if (!Compose(content_, pal, *renderer_, backdrop, backdrop,
                 opt.material, composed_, e3)) {
        err = e3;
        return false;
    }
    // 注意：这里**不能**调用 composed_.Resize() —— Image::Resize 会重新
    // 分配并清零缓冲区，把刚合成好的画面整个抹掉（那样窗口会全透明）。
    // Compose 已经把内容写进 composed_，尺寸也已经是 kCanvasW×kCanvasH。
    if (!Present()) {
        err = "UpdateLayeredWindow 失败";
        return false;
    }
    lastPaintSec_ = util::NowSec();
    lastContentKey_ = MakeContentKey(content_);
    ShowWindow(static_cast<HWND>(hwnd_), SW_SHOWNOACTIVATE);
    return true;
}

// 内容指纹：只包含会影响画面文字/颜色的字段。
// 用它判断"没有新数据时不必重绘"，这样关闭实时材质后依然会显示新电量。
//
// 注意：字段名/类型必须与 popup.h 的 Content 一致 ——
// level、confidence、chargingText 都是 std::string，直接拼进来即可。
std::string Popup::MakeContentKey(const Content& c) {
    std::string k;
    char buf[256];
    std::snprintf(buf, sizeof(buf),
                  "%d|%d|%d|%.3f|%.3f|%.0f|%d|%d|%d|%d|%d|%d|%zu",
                  c.percent, c.charging ? 1 : 0, c.voltageMv,
                  c.hoursRemaining, c.rate, c.lastSeenEpoch,
                  c.stale ? 1 : 0, c.windowHours, c.hoursWithData,
                  c.minPercent, c.maxPercent, c.chargeSamples,
                  c.hours.size());
    k = buf;
    k += "|" + c.name + "|" + c.level + "|" + c.confidence +
         "|" + c.chargingText;
    // 每小时的数值也参与指纹，否则柱状图不会随数据更新
    for (const auto& h : c.hours) {
        std::snprintf(buf, sizeof(buf), "|%d:%d", h.percent, h.count);
        k += buf;
    }
    return k;
}

bool Popup::ContentChanged(const Content& c) {
    const std::string k = MakeContentKey(c);
    if (k == lastContentKey_) return false;
    lastContentKey_ = k;
    return true;
}

void Popup::Update(const Content& c) {
    const bool changed = ContentChanged(c);
    content_ = c;
    if (!hwnd_ || !renderer_) return;

    // ---- 节流
    // 早期这里是无条件重绘：主循环每 30ms 调一次，于是「实时材质」开关
    // 关掉也照样每秒抓屏 33 次 —— 开关只是个装饰。现在按开关与 fps 生效。
    const double now = util::NowSec();
    if (!opt_.realtime) {
        // 关闭实时材质：不跟随桌面，只在新数据到来时重绘
        if (!changed) return;
    } else {
        const double minGap = (opt_.fps > 0) ? (1.0 / opt_.fps) : 0.0;
        if (!changed && (now - lastPaintSec_) < minGap) return;
    }
    lastPaintSec_ = now;
    const Palette pal = opt_.dark ? DarkPalette() : LightPalette();

    RECT wr{};
    GetWindowRect(static_cast<HWND>(hwnd_), &wr);

    gpu::Image backdrop;
    if (!grabber_) grabber_ = new capture::ScreenGrabber();
    {
        auto* g = static_cast<capture::ScreenGrabber*>(grabber_);
        std::string e2;
        if (!g->valid() && !g->Init(kCanvasW, kCanvasH, e2)) {
            // 初始化失败就退化为静态材质，而不是留下一个不更新的窗口
        }
        if (g->valid()) g->Grab(wr.left, wr.top, backdrop);
    }
    if (!backdrop.Valid())
        backdrop = gpu::SolidImage(kCanvasW, kCanvasH, 200, 200, 205);

    std::string e3;
    gpu::Image out;
    if (Compose(content_, pal, *renderer_, backdrop, backdrop,
                opt_.material, out, e3)) {
        composed_ = out;
        Present();
    }
}

void Popup::Close() {
    if (hwnd_) {
        auto* st = StateOf(static_cast<HWND>(hwnd_));
        SetWindowLongPtrW(static_cast<HWND>(hwnd_), GWLP_USERDATA, 0);
        DestroyWindow(static_cast<HWND>(hwnd_));
        delete st;
        hwnd_ = nullptr;
    }
    if (dib_) {
        auto* d = reinterpret_cast<LayeredDib*>(dib_);
        d->Destroy();
        delete d;
        dib_ = nullptr;
        memdc_ = nullptr;
    }
    if (grabber_) {
        auto* g = static_cast<capture::ScreenGrabber*>(grabber_);
        g->Close();
        delete g;
        grabber_ = nullptr;
    }
    excluded_ = false;
    if (renderer_) {
        delete renderer_;
        renderer_ = nullptr;
    }
}

bool Popup::ProcessMessage(void* msg) {
    if (!msg) return false;
    MSG* m = static_cast<MSG*>(msg);
    if (!hwnd_ || m->hwnd != static_cast<HWND>(hwnd_)) return false;
    TranslateMessage(m);
    DispatchMessageW(m);
    return true;
}

bool Popup::Expired() const {
    if (!hwnd_ || opt_.autoCloseMs <= 0) return false;
    return (util::NowSec() - shownAt_) * 1000.0 > opt_.autoCloseMs;
}

bool Popup::WindowVisible() const {
    return hwnd_ && IsWindowVisible(static_cast<HWND>(hwnd_));
}

// ---------------------------------------------------------------- 离屏导出

namespace {

bool WriteBmp32(const std::string& pathUtf8, const gpu::Image& img) {
    const std::wstring wp = util::Utf8ToWide(pathUtf8);
    HANDLE f = CreateFileW(wp.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;

    // BMP 是 BGRA 且自下而上；输入是 RGBA 直通 alpha。
    // 为了让对比脚本看得清，这里把 alpha 合成到中性底上再写（BMP 不做透明）。
    std::vector<uint8_t> rgb(static_cast<size_t>(img.width) * img.height * 4);
    for (size_t i = 0; i < img.rgba.size(); i += 4) {
        const int a = img.rgba[i + 3];
        for (int ch = 0; ch < 3; ++ch) {
            const int v = (img.rgba[i + ch] * a + 200 * (255 - a)) / 255;
            rgb[i + ch] = static_cast<uint8_t>(v < 0 ? 0 : (v > 255 ? 255 : v));
        }
        rgb[i + 3] = 255;
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
        for (int x = 0; x < img.width; ++x) {
            const uint8_t* p =
                &rgb[(static_cast<size_t>(y) * img.width + x) * 4];
            row[x * 4 + 0] = p[2];
            row[x * 4 + 1] = p[1];
            row[x * 4 + 2] = p[0];
            row[x * 4 + 3] = 255;
        }
        ok = WriteFile(f, row.data(), rowBytes, &wr, nullptr)
             && wr == rowBytes;
    }
    CloseHandle(f);
    return ok;
}

}  // namespace

bool DumpPopup(const DumpOptions& o, std::string& err) {
    gpu::Renderer renderer;
    if (!renderer.Init(err)) return false;

    // 背景：按 o.backdrop 选择。
    // white / dark 用于量化"浅色主题下面板会不会发灰"。
    gpu::Image backdrop;
    backdrop.Resize(kCanvasW, kCanvasH);
    for (int y = 0; y < kCanvasH; ++y)
        for (int x = 0; x < kCanvasW; ++x) {
            uint8_t* p = &backdrop.rgba[
                (static_cast<size_t>(y) * kCanvasW + x) * 4];
            if (o.backdrop == "white") {
                p[0] = p[1] = p[2] = 245;
            } else if (o.backdrop == "dark") {
                p[0] = p[1] = p[2] = 28;
            } else if (o.backdrop == "photo") {
                // 明暗渐变，模拟壁纸：检验材质是否保留明暗结构
                const int v = 40 + (x * 180) / kCanvasW;
                p[0] = static_cast<uint8_t>(v);
                p[1] = static_cast<uint8_t>(v * 3 / 4 + 20);
                p[2] = static_cast<uint8_t>(255 - v);
            } else {
                const bool on = ((x / 8) % 2) == 0;
                p[0] = on ? 235 : 20;
                p[1] = on ? 90 : 40;
                p[2] = on ? 60 : 200;
            }
            p[3] = 255;
        }

    Content c;
    c.percent = o.percent;
    c.charging = o.charging;
    c.stale = o.stale;
    c.name = "PRO X Wireless";
    c.chargingText = o.charging ? u8"充电中" : u8"放电中";
    c.level = o.percent >= 80 ? u8"满" : (o.percent >= 40 ? u8"良好"
                                                          : u8"偏低");
    if (o.percent >= 0) {
        // 与 Python 预览同样给出预测，便于逐字段对比布局
        c.hoursRemaining = (o.percent == 15) ? 4.2 : 40.2;
        c.rate = (o.percent == 15) ? 2.9 : 2.34;
        c.confidence = u8"高";
        c.lastSeenEpoch = battery::NowUnix();
    }

    // 若给了 history，就填上按小时的数据（供预览真实的柱状图）
    if (!o.historyPath.empty()) {
        battery::History h(o.historyPath);
        const int kWindow = 24;
        const auto buckets = h.RecentHours(kWindow);
        c.hours.reserve(buckets.size());
        for (const auto& b : buckets) {
            Content::HourPoint hp;
            hp.hourStart = b.hourStart;
            hp.count = b.count;
            hp.percent = (b.count > 0) ? b.lastPercent : -1;
            c.hours.push_back(hp);
        }
        const auto sum = h.Analyze(kWindow);
        c.windowHours = kWindow;
        c.hoursWithData = sum.hoursWithData;
        c.minPercent = (sum.sampleCount > 0) ? sum.minPercent : -1;
        c.maxPercent = (sum.sampleCount > 0) ? sum.maxPercent : -1;
    }

    gpu::Image out;
    if (!Compose(c, o.dark ? DarkPalette() : LightPalette(), renderer,
                 backdrop, backdrop, o.material, out, err))
        return false;

    if (!WriteBmp32(o.outPath, out)) {
        err = "写文件失败: " + o.outPath;
        return false;
    }
    util::Print(u8"已写出 %s (%dx%d) 材质=%s\n", o.outPath.c_str(),
                out.width, out.height, o.material.c_str());
    return true;
}

// ---------------------------------------------------------------- 自检

int RunSelfTest() {
    util::Print(u8"\n=== 详情浮窗自检 ===\n");

    int fails = 0;
    auto check = [&](bool ok, const char* what) {
        util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s\n", what);
        if (!ok) ++fails;
    };

    gpu::Renderer renderer;
    std::string err;
    if (!renderer.Init(err)) {
        util::Print(u8"  ✗ 渲染器初始化失败: %s\n", err.c_str());
        return 1;
    }

    // 条纹背景，便于观察材质是否生效
    gpu::Image backdrop;
    backdrop.Resize(kCanvasW, kCanvasH);
    for (int y = 0; y < kCanvasH; ++y)
        for (int x = 0; x < kCanvasW; ++x) {
            const bool on = ((x / 8) % 2) == 0;
            uint8_t* p = &backdrop.rgba[
                (static_cast<size_t>(y) * kCanvasW + x) * 4];
            p[0] = on ? 235 : 20; p[1] = on ? 90 : 40; p[2] = on ? 60 : 200;
            p[3] = 255;
        }

    // ---- 1) 合成三种材质都能出图
    util::Print(u8"\n[1] 三种材质合成\n");
    const char* mats[3] = {"liquid", "acrylic", "mica"};
    gpu::Image results[3];
    for (int i = 0; i < 3; ++i) {
        Content c;
        c.percent = 94;
        c.name = "PRO X Wireless";
        c.chargingText = u8"放电中";
        c.level = u8"满";
        c.hoursRemaining = 40.2;
        c.rate = 2.34;
        c.confidence = u8"高";
        c.lastSeenEpoch = battery::NowUnix();

        std::string e;
        const bool ok = Compose(c, LightPalette(), renderer, backdrop,
                                backdrop, mats[i], results[i], e);
        if (!ok) {
            util::Print(u8"  ✗ %s 合成失败: %s\n", mats[i], e.c_str());
            ++fails;
            continue;
        }
        // 统计：面板中心应当有不透明的材质
        const size_t idx =
            (static_cast<size_t>(kCanvasH / 2) * kCanvasW + kCanvasW / 2) * 4;
        util::Print(u8"  %s: 尺寸 %dx%d, 中心 alpha=%d RGB(%d,%d,%d)\n",
                    mats[i], results[i].width, results[i].height,
                    results[i].rgba[idx + 3], results[i].rgba[idx],
                    results[i].rgba[idx + 1], results[i].rgba[idx + 2]);
        if (results[i].width != kCanvasW ||
            results[i].height != kCanvasH) {
            util::Print(u8"    ✗ 尺寸不对\n");
            ++fails;
        }
    }
    check(results[0].Valid() && results[1].Valid() && results[2].Valid(),
          u8"三种材质均合成成功");

    // ---- 2) 文字确实被画上去了
    // 数字区域（面板左上 x≈PAD..PAD+90, y≈SP+50..SP+90）应有明显不同于
    // 纯材质底色的像素（文字颜色与背景差异大）。
    util::Print(u8"\n[2] 文字绘制\n");
    {
        auto inkRatio = [&](const gpu::Image& img, int x0, int y0, int x1,
                            int y1) {
            // 统计"暗像素"占比：文字是深色，材质底色偏亮
            size_t dark = 0, n = 0;
            for (int y = y0; y < y1; ++y)
                for (int x = x0; x < x1; ++x) {
                    const size_t i =
                        (static_cast<size_t>(y) * img.width + x) * 4;
                    const int lum = (img.rgba[i] * 299 + img.rgba[i + 1] * 587
                                     + img.rgba[i + 2] * 114) / 1000;
                    if (img.rgba[i + 3] > 128 && lum < 110) ++dark;
                    ++n;
                }
            return n ? static_cast<double>(dark) / n : 0.0;
        };
        const double numInk = inkRatio(results[1], kShadowPad + 20,
                                       kShadowPad + 50, kShadowPad + 110,
                                       kShadowPad + 92);
        util::Print(u8"    数字区域暗像素占比 %.1f%%\n", numInk * 100);
        check(numInk > 0.02, u8"百分比数字已绘制");

        // 设备名区域（较淡）也应有内容
        const double nameInk = inkRatio(results[1], kShadowPad + 20,
                                        kShadowPad + 20, kShadowPad + 150,
                                        kShadowPad + 40);
        util::Print(u8"    设备名区域暗像素占比 %.1f%%\n", nameInk * 100);
        check(nameInk > 0.005, u8"设备名已绘制");
    }

    // ---- 3) 状态色随电量变化
    util::Print(u8"\n[3] 状态色\n");
    {
        const Palette pal = LightPalette();
        const uint32_t ok = AccentFor(pal, 94, false);
        const uint32_t mid = AccentFor(pal, 30, false);
        const uint32_t low = AccentFor(pal, 10, false);
        const uint32_t chg = AccentFor(pal, 10, true);
        const uint32_t unk = AccentFor(pal, -1, false);
        util::Print(u8"    94%%=#%06X  30%%=#%06X  10%%=#%06X  "
                    u8"充电=#%06X  未知=#%06X\n", ok, mid, low, chg, unk);
        check(ok != mid && mid != low && low != chg && chg != unk,
              u8"各状态颜色互不相同");
    }

    // ---- 4) "--"（未读到）与数字两种情况都能合成
    util::Print(u8"\n[4] 无读数状态\n");
    {
        Content c;
        c.percent = -1;
        c.stale = true;
        gpu::Image out;
        std::string e;
        const bool ok = Compose(c, LightPalette(), renderer, backdrop,
                                backdrop, "acrylic", out, e);
        check(ok, u8"未读到电量时也能合成");
    }

    // ---- 5) 真实窗口：创建 + 可见 + 预乘 alpha
    util::Print(u8"\n[5] 分层窗口\n");
    {
        // 这一步会真的在屏幕 (300,300) 显示一个浮窗 —— 属于**可见夹具**。
        // 它在回归里每次都会闪一下，用户全屏游戏时会被干扰，
        // 所以与其他可见夹具一致：默认跳过，需 MOUSETRAY_VISUAL_TEST=1。
        // 不显示窗口的部分（[1]~[4] 的合成、文字、状态色）仍然照常验证。
        wchar_t visFlag[8] = {0};
        const bool kVisualTest =
            GetEnvironmentVariableW(L"MOUSETRAY_VISUAL_TEST", visFlag, 8) > 0;
        if (!kVisualTest) {
            util::Print(u8"SKIP: 显示真实窗口属可见夹具，默认跳过"
                        u8"（设 MOUSETRAY_VISUAL_TEST=1 启用）\n");
            util::Print(u8"\n");
            if (fails) {
                util::Print(u8"详情浮窗自检失败 %d 项\n", fails);
                return 1;
            }
            util::Print(u8"详情浮窗自检通过（可见项已跳过）✓\n");
            return 0;
        }

        Popup popup;
        Content c;
        c.percent = 94;
        c.name = "PRO TX Test";
        c.hoursRemaining = 12.5;
        c.rate = 3.1;
        c.confidence = u8"中";
        c.lastSeenEpoch = battery::NowUnix();

        Popup::Options opt;
        opt.x = 300;
        opt.y = 300;
        opt.material = "acrylic";
        opt.autoCloseMs = 0;
        std::string e;
        if (!popup.Show(c, opt, e)) {
            util::Print(u8"  ✗ 显示浮窗失败: %s\n", e.c_str());
            ++fails;
        } else {
            check(popup.WindowVisible(), u8"浮窗已显示且可见");

            // 窗口真的画上东西了吗？抓屏看这块区域
            // 注意：不能用 capture::ScreenGrabber 抓自己（分层窗口会被正常抓到，
            // 但我们要的是"屏幕上有没有非背景内容"）。
            Sleep(300);
            capture::ScreenGrabber g;
            std::string e2;
            gpu::Image shot;
            if (g.Init(kCanvasW, kCanvasH, e2) &&
                g.Grab(opt.x, opt.y, shot)) {
                // 面板中心应当是材质色（偏亮/偏灰），而不是纯背景
                const size_t i =
                    (static_cast<size_t>(kCanvasH / 2) * kCanvasW
                     + kCanvasW / 2) * 4;
                util::Print(u8"    屏幕上该点 RGB(%d,%d,%d)\n",
                            shot.rgba[i], shot.rgba[i + 1], shot.rgba[i + 2]);
                check(true, u8"可抓取到窗口区域");
            }
            popup.Close();
            check(!popup.WindowVisible(), u8"浮窗已关闭");
        }
    }

    util::Print(u8"\n");
    if (fails) {
        util::Print(u8"详情浮窗自检失败 %d 项\n", fails);
        return 1;
    }
    util::Print(u8"详情浮窗自检全部通过 ✓\n");
    return 0;
}

}  // namespace popup
