// ============================================================================
//  tray.cpp — 托盘图标与原生菜单实现
//
//  图标用 GDI+ 绘制（抗锯齿文字），先在 4 倍画布上画再缩到目标尺寸。
//  菜单用 Win32 原生 TrackPopupMenu，而不是自绘 —— 系统菜单在
//  高 DPI、深色模式、键盘操作上都自动正确。
// ============================================================================

#include "tray.h"
#include "battery_history.h"
#include "util.h"

// GDI+ 的头文件对包含顺序很敏感：它需要 objidl.h（IStream/PROPID）、
// gdiplus 又用到 min/max。必须在 windows.h 之后、用 <algorithm> 提供
// min/max 之前先把 objidl.h 引进来，否则 GdiplusImaging.h 会报一堆
// "缺少类型说明符"/"标识符 IStream" 之类的连锁错误。
#include <windows.h>
#include <windowsx.h>   // GET_X_LPARAM / GET_Y_LPARAM（v4 右键坐标在 wParam）
#include <objidl.h>
#include <gdiplus.h>
#include <shellapi.h>

#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <map>
#include <vector>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "gdiplus.lib")

using namespace Gdiplus;

namespace tray {

namespace {

// 托盘回调消息。用 WM_APP 之后的编号，避免与系统消息冲突。
constexpr UINT kTrayCallbackMsg = WM_APP + 1;
constexpr UINT kTrayIconId = 1;

// 消息窗口类名
const wchar_t* kTrayClassName = L"MouseBatteryTrayNativeTray";

// GDI+ 生命周期：进程内初始化一次
ULONG_PTR g_gdiplusToken = 0;
bool g_gdiplusReady = false;

bool EnsureGdiplus() {
    if (g_gdiplusReady) return true;
    GdiplusStartupInput input;
    if (GdiplusStartup(&g_gdiplusToken, &input, nullptr) != Ok) return false;
    g_gdiplusReady = true;
    return true;
}

// 全局指针映射：窗口过程通过 GWLP_USERDATA 取回对象
std::map<HWND, TrayIcon*> g_instances;


// 托盘消息窗口的窗口过程。
//
// 这里**只做兜底**：正常路径是 app 的消息循环先调用
// TrayIcon::ProcessMessage（它能看到完整 MSG，包括 v4 的 wParam 坐标）。
// 本函数在 ProcessMessage 之前被调到的情况很少，但必须写对，
// 不能再出现"调用 ProcessMessage(nullptr)"这种永远返回 false 的死代码
// —— 早期版本就是这样：既处理不了事件，又让人误以为已经处理过。
LRESULT CALLBACK TrayWndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == kTrayCallbackMsg) {
        // **这是托盘回调的真正到达点。**
        // Shell_NotifyIcon 用 SendMessage 投递，消息不会进队列，
        // 所以 PeekMessage 循环看不到它 —— 早期把它当成队列消息处理，
        // 导致真实点击被这里静默吞掉（而 PostMessage 的合成测试恰好能过）。
        auto it = g_instances.find(h);
        TrayIcon* self = (it != g_instances.end()) ? it->second : nullptr;
        if (self) self->OnCallback(static_cast<uintptr_t>(wp),
                                   static_cast<intptr_t>(lp));
        return 0;
    }
    if (msg == WM_CLOSE) {
        return 0;      // 托盘消息窗口不该被关闭
    }
    return DefWindowProcW(h, msg, wp, lp);
}

// 从 HBITMAP 建 HICON（带 alpha），并释放中间资源
HICON BitmapToIcon(HBITMAP bmp, int w, int h) {
    // 用 ICONINFO + 掩码：GDI+ 画出的 32 位位图 alpha 需要保留，
    // 所以 mask 用全 0（"全部不透明"），由 alpha 通道负责透明。
    HBITMAP mask = CreateBitmap(w, h, 1, 1, nullptr);
    ICONINFO ii{};
    ii.fIcon = TRUE;
    ii.hbmColor = bmp;
    ii.hbmMask = mask;
    HICON icon = CreateIconIndirect(&ii);
    if (mask) DeleteObject(mask);
    return icon;
}

// 把 GDI+ Bitmap 转成 HBITMAP（32bpp 带 alpha）
HBITMAP BitmapToHBitmap(Bitmap& bmp) {
    HBITMAP result = nullptr;
    if (bmp.GetHBITMAP(Color(0, 0, 0, 0), &result) != Ok) return nullptr;
    return result;
}

}  // namespace

// ---------------------------------------------------------------- 颜色

uint32_t LevelColor(int percent, bool charging) {
    if (charging) return kColorCharging;
    if (percent < 0) return kColorUnknown;
    if (percent <= 20) return kColorLow;
    if (percent <= 45) return kColorMid;
    return kColorOk;
}

namespace {

inline BYTE R(uint32_t c) { return static_cast<BYTE>((c >> 16) & 0xFF); }
inline BYTE G(uint32_t c) { return static_cast<BYTE>((c >> 8) & 0xFF); }
inline BYTE B(uint32_t c) { return static_cast<BYTE>(c & 0xFF); }
inline Color ToColor(uint32_t c, BYTE a = 255) {
    return Color(a, R(c), G(c), B(c));
}

// 找一个可用的粗体界面字体
const wchar_t* PickFontFamily() {
    // Segoe UI Semibold 是 Windows 上数字最清晰的选择；
    // 不存在时由 GDI+ 自行回退。
    return L"Segoe UI Semibold";
}

}  // namespace

// ---------------------------------------------------------------- 图标绘制

void* RenderIcon(int percent, bool charging, bool darkTaskbar, int size,
                 bool stale) {
    if (size < 8) size = 16;
    if (!EnsureGdiplus()) return nullptr;

    const int canvas = size * kSupersample;

    Bitmap bmp(canvas, canvas, PixelFormat32bppARGB);
    Graphics g(&bmp);
    g.SetSmoothingMode(SmoothingModeAntiAlias);
    g.SetTextRenderingHint(TextRenderingHintAntiAliasGridFit);
    g.SetInterpolationMode(InterpolationModeHighQualityBicubic);
    g.Clear(Color(0, 0, 0, 0));

    uint32_t accent = LevelColor(percent, charging);
    if (stale && percent >= 0 && !charging) {
        // 回退到中性灰，明确"这是历史值"
        accent = kColorUnknown;
    }
    const uint32_t fg = darkTaskbar ? 0xF6F6F6 : 0x1A1A1C;

    wchar_t label[16];
    if (percent < 0) wcscpy_s(label, L"--");
    else swprintf_s(label, L"%d", percent);

    const int barH = canvas / 9 > 1 ? canvas / 9 : 1;
    const int gap = canvas / 24 > 1 ? canvas / 24 : 1;
    const int availH = canvas - barH - gap * 2;

    // 数字：先按接近满高取字号，再按宽度收缩
    // FontFamily 不可赋值（拷贝构造/赋值是私有的），
    // 所以用指针选择，而不是先建一个再覆盖。
    FontFamily familySemibold(PickFontFamily());
    FontFamily familyFallback(L"Segoe UI");
    FontFamily* family = familySemibold.IsAvailable() ? &familySemibold
                                                      : &familyFallback;

    REAL fontSize = availH * 1.02f;
    for (int guard = 0; guard < 64 && fontSize > 6; ++guard) {
        Font probe(family, fontSize, FontStyleBold, UnitPixel);
        RectF bounds;
        g.MeasureString(label, -1, &probe, PointF(0, 0), &bounds);
        // GDI+ 的 MeasureString 会留内边距，实际字宽略小于 bounds.Width
        if (bounds.Width <= canvas - gap && bounds.Height <= availH * 1.15f)
            break;
        fontSize -= 1.0f;
    }

    {
        Font font(family, fontSize, FontStyleBold, UnitPixel);
        StringFormat sf;
        sf.SetAlignment(StringAlignmentCenter);
        sf.SetLineAlignment(StringAlignmentCenter);
        RectF layout(0, gap / 2.0f, static_cast<REAL>(canvas),
                     static_cast<REAL>(availH));
        SolidBrush brush(ToColor(accent));
        g.DrawString(label, -1, &font, layout, &sf, &brush);
    }

    // 底部比例条：外框 + 填充
    const int y0 = canvas - barH - gap / 2;
    const int y1 = canvas - gap / 2;
    {
        Pen pen(ToColor(fg), 1.0f);
        g.DrawRectangle(&pen, 0, y0, canvas - 1, y1 - y0);
    }
    if (percent >= 0) {
        const int innerW = canvas - 2;
        const int clamped = percent < 0 ? 0 : (percent > 100 ? 100 : percent);
        const int fillW = innerW * clamped / 100;
        if (fillW > 0) {
            if (stale && !charging) {
                // 历史值：空心条表示"非实时"
                Pen pen(ToColor(accent), 1.0f);
                g.DrawRectangle(&pen, 1, y0 + 1, fillW, y1 - y0 - 2);
            } else {
                SolidBrush brush(ToColor(accent));
                g.FillRectangle(&brush, 1, y0 + 1, fillW, y1 - y0 - 2);
            }
        }
    } else if (charging) {
        SolidBrush brush(ToColor(accent));
        g.FillRectangle(&brush, 1, y0 + 1, canvas - 3, y1 - y0 - 2);
    }

    // 缩放到目标尺寸。
    // 注意：变量**不能**叫 small —— Windows 头文件里有
    //   #define small char
    // 这个宏，会把声明拆成 `Bitmap char(...)`，报出一串
    // "Bitmap 后面接 char 是非法的" 之类的怪错误。
    Bitmap scaled(size, size, PixelFormat32bppARGB);
    {
        Graphics gs(&scaled);
        gs.SetInterpolationMode(InterpolationModeHighQualityBicubic);
        gs.SetPixelOffsetMode(PixelOffsetModeHighQuality);
        gs.Clear(Color(0, 0, 0, 0));
        gs.DrawImage(&bmp, Rect(0, 0, size, size), 0, 0, canvas, canvas,
                     UnitPixel);
    }

    HBITMAP hbmp = BitmapToHBitmap(scaled);
    if (!hbmp) return nullptr;
    HICON icon = BitmapToIcon(hbmp, size, size);
    DeleteObject(hbmp);
    return icon;
}

// ---------------------------------------------------------------- Tooltip

std::string BuildTooltip(const std::string& name, int percent,
                         const std::string& chargingText,
                         const std::string& level, int voltageMv,
                         double hoursRemaining, double lastSeenEpoch,
                         bool stale) {
    if (percent < 0)
        return u8"罗技鼠标电量：离线（鼠标休眠中，动一下即可唤醒）";

    char buf[512];
    std::string head;
    std::snprintf(buf, sizeof(buf), "%s：%d%%", 
                  name.empty() ? u8"鼠标" : name.c_str(), percent);
    head = buf;
    if (!chargingText.empty()) head += u8"　" + chargingText;

    std::string out = head;
    if (hoursRemaining > 0) {
        // 复用 battery_history 的格式化，保证与 Python 版逐字一致
        out += "\n" + std::string(u8"预计可用：") +
               battery::FormatDuration(hoursRemaining);
    }
    if (!level.empty()) out += std::string("\n") + u8"档位：" + level;
    if (voltageMv > 0) {
        std::snprintf(buf, sizeof(buf), "\n%s%d mV", u8"电压：", voltageMv);
        out += buf;
    }
    if (lastSeenEpoch > 0) {
        // 转本地时间 HH:MM:SS
        const time_t t = static_cast<time_t>(lastSeenEpoch);
        struct tm lt{};
        localtime_s(&lt, &t);
        std::snprintf(buf, sizeof(buf), "\n%s%02d:%02d:%02d",
                      u8"更新：", lt.tm_hour, lt.tm_min, lt.tm_sec);
        out += buf;
    }
    if (stale) out += std::string("\n") + u8"（当前离线，以上为最后读数）";

    // Windows 托盘 tooltip 上限是 127 **个字符**
    // （NOTIFYICONDATA.szTip 是 WCHAR[128]，末尾留 0）。
    //
    // 注意不能用 out.resize(127)：std::string::size() 数的是**字节**，
    // 中文在 UTF-8 下一个字 3 字节，按字节截到 127 只能放约 42 个汉字，
    // 而且可能把某个字**从中间切断**，产生非法 UTF-8，托盘里显示成乱码。
    // 所以按码点数截断，并保证切在字符边界上。
    return util::TruncateUtf8Chars(out, 127);
}

// ---------------------------------------------------------------- 托盘图标

TrayIcon::~TrayIcon() { Destroy(); }

bool TrayIcon::Create(const std::string& tooltip, ClickFn onClick,
                      ClickFn onDoubleClick, CommandFn onCommand,
                      std::string& err) {
    Destroy();
    onClick_ = onClick;
    onDoubleClick_ = onDoubleClick;
    onCommand_ = onCommand;

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = TrayWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kTrayClassName;
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    RegisterClassExW(&wc);   // 已注册时返回 0 + ERROR_CLASS_ALREADY_EXISTS

    HWND h = CreateWindowExW(0, kTrayClassName, L"", 0, 0, 0, 0, 0,
                             nullptr, nullptr, GetModuleHandleW(nullptr),
                             nullptr);
    if (!h) {
        err = "创建托盘消息窗口失败";
        return false;
    }
    hwnd_ = h;
    g_instances[h] = this;

    NOTIFYICONDATAW nid{};
    nid.cbSize = sizeof(nid);
    nid.hWnd = h;
    nid.uID = kTrayIconId;
    // 注意：必须包含 NIF_SHOWTIP，否则高版本 Windows 上图标可能不显示
    nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    nid.uCallbackMessage = kTrayCallbackMsg;
    nid.hIcon = static_cast<HICON>(
        RenderIcon(-1, false, false, 16, false));

    // 复制 tooltip（UTF-8 -> UTF-16）
    const std::wstring wtip = util::Utf8ToWide(tooltip);
    wcsncpy_s(nid.szTip, wtip.c_str(), _TRUNCATE);

    if (!Shell_NotifyIconW(NIM_ADD, &nid)) {
        const DWORD e = GetLastError();
        err = "Shell_NotifyIcon(NIM_ADD) 失败（explorer 是否在运行？）";
        lastError_ = static_cast<int>(e);
        Destroy();
        return false;
    }

    // 让"左键单击"这类新式通知生效（Vista+）
    nid.uVersion = NOTIFYICON_VERSION_4;
    v4_ = Shell_NotifyIconW(NIM_SETVERSION, &nid) != FALSE;

    icon_ = nid.hIcon;
    created_ = true;
    return true;
}

void TrayIcon::Destroy() {
    if (hwnd_) {
        NOTIFYICONDATAW nid{};
        nid.cbSize = sizeof(nid);
        nid.hWnd = static_cast<HWND>(hwnd_);
        nid.uID = kTrayIconId;
        Shell_NotifyIconW(NIM_DELETE, &nid);
        g_instances.erase(static_cast<HWND>(hwnd_));
        DestroyWindow(static_cast<HWND>(hwnd_));
        hwnd_ = nullptr;
    }
    if (icon_) {
        DestroyIcon(static_cast<HICON>(icon_));
        icon_ = nullptr;
    }
    created_ = false;
}

void TrayIcon::Update(const TrayState& st, bool darkTaskbar) {
    if (!created_ || !hwnd_) return;

    HICON fresh = static_cast<HICON>(RenderIcon(
        st.percent, st.charging, darkTaskbar, 16, st.stale));
    if (!fresh) return;

    NOTIFYICONDATAW nid{};
    nid.cbSize = sizeof(nid);
    nid.hWnd = static_cast<HWND>(hwnd_);
    nid.uID = kTrayIconId;
    nid.uFlags = NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    nid.hIcon = fresh;
    const std::wstring wtip = util::Utf8ToWide(st.tooltip);
    wcsncpy_s(nid.szTip, wtip.c_str(), _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &nid);

    // 换掉旧图标，避免句柄泄漏
    if (icon_ && icon_ != fresh) DestroyIcon(static_cast<HICON>(icon_));
    icon_ = fresh;
}

// ---------------------------------------------------------------- 菜单

namespace {

// 递归把 MenuItem 建成 HMENU
void BuildMenu(const std::vector<MenuItem>& items, HMENU menu,
               std::map<uint32_t, bool>* idUsed, uint32_t* nextId) {
    for (const auto& it : items) {
        if (it.kind == MenuItem::Kind::Separator) {
            AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
            continue;
        }
        if (it.kind == MenuItem::Kind::Submenu) {
            HMENU sub = CreatePopupMenu();
            BuildMenu(it.children, sub, idUsed, nextId);
            AppendMenuW(menu, MF_POPUP,
                        reinterpret_cast<UINT_PTR>(sub),
                        util::Utf8ToWide(it.label).c_str());
            continue;
        }

        UINT flags = MF_STRING;
        if (!it.enabled) flags |= MF_GRAYED;
        if (it.checked) flags |= MF_CHECKED;
        // 单选圆点：MF_RADIOCHECK 并不存在，正确做法是把 MFT_RADIOCHECK
        // 放进"类型"位（低 4 位），同时带 MF_CHECKED 才会显示圆点。
        if (it.radio) flags |= MFT_RADIOCHECK;

        uint32_t id = it.id;
        if (id == 0) {
            // 自动分配：从 1000 起，跳过已用的
            while (idUsed->count(*nextId)) ++(*nextId);
            id = (*nextId)++;
        }
        (*idUsed)[id] = true;
        AppendMenuW(menu, flags, id, util::Utf8ToWide(it.label).c_str());
    }
}

}  // namespace

void TrayIcon::ShowMenu(const std::vector<MenuItem>& menu, int x, int y) {
    if (!hwnd_) return;

    HMENU root = CreatePopupMenu();
    std::map<uint32_t, bool> used;
    uint32_t nextId = 1000;
    BuildMenu(menu, root, &used, &nextId);

    // 必须先 SetForegroundWindow，否则菜单弹出后不会自动消失
    // （点别处菜单仍留在屏幕上）。这是 TrackPopupMenu 的经典要求。
    SetForegroundWindow(static_cast<HWND>(hwnd_));

    const UINT cmd = TrackPopupMenu(
        root,
        TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
        x, y, 0, static_cast<HWND>(hwnd_), nullptr);

    DestroyMenu(root);

    if (cmd != 0 && onCommand_) onCommand_(cmd);
}

void TrayIcon::ShowMenuAtCursor() {
    POINT pt{};
    GetCursorPos(&pt);
    ShowMenu(currentMenu_, pt.x, pt.y);
}


void TrayIcon::OnCallback(uintptr_t wparam, intptr_t lparam) {
    // 我们注册的是 NOTIFYICON_VERSION_4，必须按 v4 的事件码判断。
    //
    // v4 的语义与旧版不同，这是"点击托盘没反应"的直接原因：
    //   * 左键单击 -> NIN_SELECT (WM_USER+0)，**不是** WM_LBUTTONUP
    //   * 右键     -> WM_CONTEXTMENU，**不是** WM_RBUTTONUP
    //   * 事件码在 lParam 低字；光标坐标在 wParam（低字 x、高字 y）
    // 旧事件码一并保留，降级到 v0~v3 时仍可用。
    const UINT event = LOWORD(static_cast<DWORD_PTR>(lparam));

    // **v4 与旧版的事件码必须二选一，不能都处理。**
    //
    // 实测（一次真实左键单击）：
    //     lp=10202 (WM_LBUTTONUP) -> 触发
    //     lp=10400 (NIN_SELECT)   -> 又触发
    // 也就是说 v4 模式下一次点击会同时收到新旧两套事件。两个都响应的话，
    // 动作会执行两次 —— 左键表现为"浮窗开了立刻又关"（TogglePopup 跑两遍），
    // 右键表现为菜单被弹三次。
    //
    // 这里根据 NIM_SETVERSION 是否成功来选择事件集。
    const bool isLeft =
        v4_ ? (event == NIN_SELECT || event == NIN_KEYSELECT)
            : (event == WM_LBUTTONUP);
    // 双击在 v4 下仍用 WM_LBUTTONDBLCLK（v4 没有对应通知）
    const bool isDouble = (event == WM_LBUTTONDBLCLK);
    const bool isRight =
        v4_ ? (event == WM_CONTEXTMENU)
            : (event == WM_RBUTTONUP || event == WM_RBUTTONDOWN);

    if (isLeft) {
        if (onClick_) onClick_();
        return;
    }
    if (isDouble) {
        if (onDoubleClick_) onDoubleClick_();
        return;
    }
    if (isRight) {
        POINT pt{};
        if (v4_) {
            // v4 把光标位置放在 wParam（低字 x、高字 y）
            const int x = GET_X_LPARAM(static_cast<DWORD_PTR>(wparam));
            const int y = GET_Y_LPARAM(static_cast<DWORD_PTR>(wparam));
            if (x != 0 || y != 0) {
                pt.x = x;
                pt.y = y;
            } else {
                GetCursorPos(&pt);
            }
        } else {
            GetCursorPos(&pt);
        }
        ShowMenu(currentMenu_, pt.x, pt.y);
        return;
    }
}

bool TrayIcon::ProcessMessage(void* msg) {
    if (!msg) return false;
    MSG* m = static_cast<MSG*>(msg);
    if (!hwnd_ || m->hwnd != static_cast<HWND>(hwnd_)) return false;
    if (m->message != kTrayCallbackMsg) return false;

    // 队列路径（例如被 PostMessage 投递时）。真实点击走窗口过程。
    OnCallback(static_cast<uintptr_t>(m->wParam),
               static_cast<intptr_t>(m->lParam));
    return true;
}

// ---------------------------------------------------------------- 自检

int RunSelfTest() {
    util::Print(u8"\n=== 托盘图标与菜单自检 ===\n");

    int fails = 0;
    auto check = [&](bool ok, const char* what) {
        util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s\n", what);
        if (!ok) ++fails;
    };

    // ---- 1) 颜色映射
    util::Print(u8"\n[1] 状态色映射\n");
    check(LevelColor(94, false) == kColorOk, u8"94% -> 绿色");
    check(LevelColor(30, false) == kColorMid, u8"30% -> 黄色");
    check(LevelColor(10, false) == kColorLow, u8"10% -> 红色");
    check(LevelColor(10, true) == kColorCharging, u8"充电中优先用蓝色");
    check(LevelColor(-1, false) == kColorUnknown, u8"未知 -> 灰色");

    // ---- 2) 图标生成（多状态）+ 尺寸
    util::Print(u8"\n[2] 图标生成\n");
    struct Case { int pct; bool chg; bool stale; const char* name; };
    const Case cases[] = {
        {94, false, false, u8"94% 正常"},
        {100, true, false, u8"100% 充电"},
        {45, false, false, u8"45% 中等"},
        {18, false, false, u8"18% 低"},
        {-1, false, false, u8"未知 --"},
        {94, false, true, u8"94% 历史值（灰+空心条）"},
    };
    for (const auto& c : cases) {
        HICON ic = static_cast<HICON>(
            RenderIcon(c.pct, c.chg, false, 16, c.stale));
        const bool ok = ic != nullptr;
        util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s 生成失败\n", c.name);
        if (!ok) ++fails;
        if (ic) DestroyIcon(ic);
    }

    // ---- 3) 图标内容确实随状态变化
    util::Print(u8"\n[3] 图标内容随状态变化\n");
    {
        auto renderToGray = [](int pct, bool chg, bool dark, bool stale,
                               std::vector<uint8_t>& out, int& w, int& h) {
            HICON ic = static_cast<HICON>(
                RenderIcon(pct, chg, dark, 16, stale));
            if (!ic) return false;
            ICONINFO ii{};
            if (!GetIconInfo(ic, &ii)) { DestroyIcon(ic); return false; }
            BITMAP bm{};
            GetObject(ii.hbmColor, sizeof(bm), &bm);
            w = bm.bmWidth;
            h = bm.bmHeight;
            // 读回像素（32bpp 自上而下）
            BITMAPINFOHEADER bi{};
            bi.biSize = sizeof(bi);
            bi.biWidth = w;
            bi.biHeight = -h;
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            bi.biCompression = BI_RGB;
            std::vector<uint8_t> buf(static_cast<size_t>(w) * h * 4);
            HDC dc = GetDC(nullptr);
            const int got = GetDIBits(dc, ii.hbmColor, 0, h, buf.data(),
                                      reinterpret_cast<BITMAPINFO*>(&bi),
                                      DIB_RGB_COLORS);
            ReleaseDC(nullptr, dc);
            out.swap(buf);
            if (ii.hbmColor) DeleteObject(ii.hbmColor);
            if (ii.hbmMask) DeleteObject(ii.hbmMask);
            DestroyIcon(ic);
            return got != 0;
        };

        std::vector<uint8_t> a, b, c;
        int w = 0, h = 0;
        const bool okA = renderToGray(94, false, false, false, a, w, h);
        const bool okB = renderToGray(10, false, false, false, b, w, h);
        const bool okC = renderToGray(94, false, false, true, c, w, h);

        check(okA && okB && okC, u8"图标像素读回成功");
        check(w == 16 && h == 16, u8"图标尺寸为 16x16");

        if (okA && okB) {
            // 94%（绿）与 10%（红）应明显不同
            double diff = 0;
            size_t n = 0;
            for (size_t i = 0; i < a.size() && i < b.size(); i += 4) {
                for (int ch = 0; ch < 3; ++ch) {
                    diff += std::abs(static_cast<int>(a[i + ch]) -
                                     static_cast<int>(b[i + ch]));
                    ++n;
                }
            }
            diff = n ? diff / n : 0;
            util::Print(u8"    94%%(绿) 与 10%%(红) 平均差 %.2f\n", diff);
            check(diff > 3.0, u8"不同电量图标确实不同");
        }
        if (okA && okC) {
            // 正常 vs 历史值：颜色应不同（绿 vs 灰）
            double diff = 0;
            size_t n = 0;
            for (size_t i = 0; i < a.size() && i < c.size(); i += 4) {
                for (int ch = 0; ch < 3; ++ch) {
                    diff += std::abs(static_cast<int>(a[i + ch]) -
                                     static_cast<int>(c[i + ch]));
                    ++n;
                }
            }
            diff = n ? diff / n : 0;
            util::Print(u8"    实时(绿) 与 历史(灰) 平均差 %.2f\n", diff);
            check(diff > 2.0, u8"历史值图标与实时值可区分");
        }

        // 非空检查：图标必须真的画了东西（alpha 有非零）
        if (okA) {
            size_t opaque = 0;
            for (size_t i = 3; i < a.size(); i += 4)
                if (a[i] > 32) ++opaque;
            util::Print(u8"    94%% 图标不透明像素 %zu / 256\n", opaque);
            check(opaque > 30, u8"图标确实绘制了内容");
        }
    }

    // ---- 3.5) 事件分发（v4 vs 旧版）
    //
    // 这一段是修复后补的**防回归**测试。起因：真实点击托盘毫无反应，
    // 而原先的测试却"通过"了，因为它犯和实现一样的错：
    //   · 用 PostMessage 投递 —— 而 shell 用 SendMessage 直接投给窗口过程，
    //     根本不进消息队列，测试走的是 shell 从不使用的路径；
    //   · 投递 WM_LBUTTONUP —— 恰好是实现当时唯一接受的事件码。
    // v4 模式下真实左键是 NIN_SELECT，且 shell **新旧两套都会发**：
    // 只认旧的 -> 无反应；两套都认 -> 触发两次（浮窗开了立刻关）。
    util::Print(u8"\n[3.5] 事件分发（v4 / 旧版，必须各触发一次）\n");
    {
        constexpr uintptr_t NIN_SELECT_V = 0x0400;   // WM_USER + 0
        constexpr uintptr_t NIN_KEYSELECT_V = 0x0401;
        constexpr uintptr_t WM_LBUTTONUP_V = 0x0202;
        constexpr uintptr_t WM_LBUTTONDBLCLK_V = 0x0203;
        constexpr uintptr_t WM_CONTEXTMENU_V = 0x007B;

        // 一次真实左键在 v4 下会依次到达这些事件；click 只能被触发一次
        struct Case {
            const char* name;
            bool v4;
            std::vector<uintptr_t> events;
            int wantClicks;
            int wantDoubles;
        };
        const Case eventCases[] = {
            {u8"v4 收到新旧两套（应只算一次）", true,
             {0x0200, 0x0201, WM_LBUTTONUP_V, NIN_SELECT_V}, 1, 0},
            {u8"v4 仅 NIN_SELECT", true, {NIN_SELECT_V}, 1, 0},
            {u8"v4 仅 NIN_KEYSELECT", true, {NIN_KEYSELECT_V}, 1, 0},
            {u8"v4 旧码不应触发", true, {WM_LBUTTONUP_V}, 0, 0},
            {u8"v4 双击", true, {WM_LBUTTONDBLCLK_V}, 0, 1},
            {u8"旧版 WM_LBUTTONUP", false, {WM_LBUTTONUP_V}, 1, 0},
            {u8"旧版不应认 NIN_SELECT", false, {NIN_SELECT_V}, 0, 0},
        };

        for (const auto& cs : eventCases) {
            tray::TrayIcon probe;
            probe.SetV4ForTest(cs.v4);
            int clicks = 0, doubles = 0;
            probe.SetClickCounterForTest(&clicks);
            probe.SetDoubleClickCounterForTest(&doubles);
            for (uintptr_t e : cs.events)
                probe.OnCallback(0, static_cast<intptr_t>(e));

            const bool ok = (clicks == cs.wantClicks &&
                             doubles == cs.wantDoubles);
            util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s（clicks=%d 期望 %d）\n",
                        cs.name, clicks, cs.wantClicks);
            if (!ok) ++fails;
        }

        // 右键不应触发左键回调（两者必须分开）
        {
            tray::TrayIcon probe;
            probe.SetV4ForTest(true);
            int clicks = 0;
            probe.SetClickCounterForTest(&clicks);
            // 只发右键事件；ShowMenu 在没有窗口时会安全返回
            probe.OnCallback(0, static_cast<intptr_t>(WM_CONTEXTMENU_V));
            probe.OnCallback(0, static_cast<intptr_t>(0x0204));
            probe.OnCallback(0, static_cast<intptr_t>(0x0205));
            const bool ok = (clicks == 0);
            util::Print(ok ? u8"  ✓ 右键不触发左键回调\n"
                           : u8"  ✗ 右键误触发了 %d 次左键\n", clicks);
            if (!ok) ++fails;
        }
    }

    // ---- 4) tooltip 合规
    util::Print(u8"\n[4] Tooltip（上限 127 **字符**）\n");
    {
        // 校验 UTF-8 是否合法：首字节声明的序列长度必须与实际字节吻合，
        // 且不能出现 U+FFFD 替换字符（Python 的 errors="replace" 会插入它）。
        auto utf8Valid = [](const std::string& s) {
            size_t i = 0;
            while (i < s.size()) {
                const unsigned char c = static_cast<unsigned char>(s[i]);
                size_t len = 1;
                if (c < 0x80) len = 1;
                else if ((c & 0xE0) == 0xC0) len = 2;
                else if ((c & 0xF0) == 0xE0) len = 3;
                else if ((c & 0xF8) == 0xF0) len = 4;
                else return false;                 // 非法首字节
                if (i + len > s.size()) return false;   // 序列被截断
                for (size_t k = 1; k < len; ++k) {
                    const unsigned char cc =
                        static_cast<unsigned char>(s[i + k]);
                    if ((cc & 0xC0) != 0x80) return false;   // 非续接字节
                }
                i += len;
            }
            return true;
        };
        auto hasReplacementChar = [](const std::string& s) {
            // U+FFFD 的 UTF-8 编码是 EF BF BD
            return s.find("\xEF\xBF\xBD") != std::string::npos;
        };

        const std::string t1 = BuildTooltip("PRO X Wireless", 94, u8"放电中",
                                            u8"满", 0, 40.0,
                                            util::NowSec() + 1767225600.0,
                                            false);
        util::Print(u8"    正常: %s\n", t1.c_str());
        check(util::Utf8CharCount(t1) <= 127, u8"正常 tooltip 字符数合规");
        check(utf8Valid(t1), u8"正常 tooltip 是合法 UTF-8");

        const std::string t2 = BuildTooltip("", -1, "", "", 0, -1, 0, false);
        util::Print(u8"    离线: %s\n", t2.c_str());
        check(util::Utf8CharCount(t2) <= 127, u8"离线 tooltip 字符数合规");
        check(utf8Valid(t2), u8"离线 tooltip 是合法 UTF-8");

        // 超长输入：这是关键用例。
        // 早期断言用的是 t3.size() <= 127（字节），恰好把 bug 放过去了：
        // 按字节截断同样满足它，但只能留下约 42 个汉字，还可能切出半个字。
        std::string longName;
        for (int k = 0; k < 200; ++k) longName += u8"鼠标";   // 大量多字节
        const std::string t3 = BuildTooltip(longName, 94, u8"放电中", u8"满",
                                            3900, 100.0,
                                            util::NowSec() + 1767225600.0,
                                            true);
        const size_t chars3 = util::Utf8CharCount(t3);
        util::Print(u8"    超长输入: %zu 字符 / %zu 字节\n", chars3,
                    t3.size());
        util::Print(u8"      （字节数大于 127 是**正确**的："
                    u8"限的是字符，不是字节）\n");

        check(chars3 <= 127, u8"超长 tooltip 字符数被限制在 127");
        check(utf8Valid(t3), u8"超长 tooltip 截断后仍是合法 UTF-8");
        check(!hasReplacementChar(t3), u8"截断未产生替换字符");

        // 必须**充分利用**配额：若按字节截断，这里只有约 42 个字符。
        // 用这个下界把"按字节"的实现挡在门外。
        check(chars3 >= 100, u8"超长 tooltip 用足了字符配额（>100）");

        // 边界：恰好 127 字符不应被改动
        std::string exact;
        for (int k = 0; k < 127; ++k) exact += "A";
        const std::string te = util::TruncateUtf8Chars(exact, 127);
        check(te.size() == 127 && util::Utf8CharCount(te) == 127,
              u8"恰好 127 字符时不做改动");

        // 边界：不会切出半个多字节字符
        std::string cn;
        for (int k = 0; k < 127; ++k) cn += u8"中";
        const std::string tc = util::TruncateUtf8Chars(cn, 126);
        check(util::Utf8CharCount(tc) == 126 && utf8Valid(tc),
              u8"中文截断不产生半个字符");
    }

    util::Print(u8"\n");
    if (fails) {
        util::Print(u8"托盘自检失败 %d 项\n", fails);
        return 1;
    }
    util::Print(u8"托盘图标与菜单自检全部通过 ✓\n");
    return 0;
}

}  // namespace tray
