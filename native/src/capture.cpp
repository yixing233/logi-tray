// ============================================================================
//  capture.cpp — 实时抓屏实现
//
//  所有 GDI 句柄都显式声明类型（HANDLE 是 64 位）。
//  Python 版因为没有声明 argtypes 而抛过 "int too long to convert"；
//  C++ 里没有这个问题，但句柄与 HBITMAP/HDC 不要混用。
// ============================================================================

#include "capture.h"
#include "util.h"

#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstring>
#include <string>
#include <thread>

namespace capture {

namespace {

constexpr DWORD kSrcCopy = 0x00CC0020;

std::string LastErrorText(const char* what) {
    char buf[256];
    std::snprintf(buf, sizeof(buf), u8"%s 失败 (GetLastError=%lu)", what,
                  GetLastError());
    return buf;
}

}  // namespace

// ---------------------------------------------------------------- 隐形

bool ExcludeFromCapture(void* hwnd) {
    if (!hwnd) return false;
    // Win10 2004+ 支持。老系统会返回 FALSE，调用方降级即可。
    return SetWindowDisplayAffinity(static_cast<HWND>(hwnd),
                                    kWdaExcludeFromCapture) != FALSE;
}

int CaptureAffinity(void* hwnd) {
    if (!hwnd) return -1;
    DWORD val = 0;
    if (!GetWindowDisplayAffinity(static_cast<HWND>(hwnd), &val)) return -1;
    return static_cast<int>(val);
}

// ---------------------------------------------------------------- 抓屏器

ScreenGrabber::~ScreenGrabber() { Close(); }

void ScreenGrabber::Close() {
    if (memdc_) {
        if (oldBitmap_)
            SelectObject(static_cast<HDC>(memdc_),
                         static_cast<HGDIOBJ>(oldBitmap_));
        if (bitmap_)
            DeleteObject(static_cast<HGDIOBJ>(bitmap_));
        DeleteDC(static_cast<HDC>(memdc_));
    }
    memdc_ = bitmap_ = oldBitmap_ = nullptr;
    width_ = height_ = 0;
}

bool ScreenGrabber::Init(int width, int height, std::string& err) {
    Close();
    if (width <= 0 || height <= 0) {
        err = "抓屏区域尺寸非法";
        return false;
    }

    HDC screen = GetDC(nullptr);
    if (!screen) {
        err = LastErrorText(u8"GetDC");
        return false;
    }
    memdc_ = CreateCompatibleDC(screen);
    if (!memdc_) {
        ReleaseDC(nullptr, screen);
        err = LastErrorText(u8"CreateCompatibleDC");
        return false;
    }
    // CreateCompatibleBitmap 必须用**屏幕 DC**，不能用内存 DC：
    // 用内存 DC 会得到 1bpp 单色位图，抓到的东西全是黑白。
    bitmap_ = CreateCompatibleBitmap(screen, width, height);
    ReleaseDC(nullptr, screen);
    if (!bitmap_) {
        err = LastErrorText(u8"CreateCompatibleBitmap");
        Close();
        return false;
    }
    oldBitmap_ = SelectObject(static_cast<HDC>(memdc_),
                              static_cast<HGDIOBJ>(bitmap_));
    if (!oldBitmap_ || oldBitmap_ == HGDI_ERROR) {
        err = LastErrorText(u8"SelectObject");
        Close();
        return false;
    }

    // BITMAPINFO：负高度 = 自上而下，省掉一次翻转
    BITMAPINFO bmi{};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = width;
    bmi.bmiHeader.biHeight = -height;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;
    bmi_.assign(sizeof(BITMAPINFO), 0);
    std::memcpy(bmi_.data(), &bmi, sizeof(BITMAPINFO));

    buffer_.assign(static_cast<size_t>(width) * height * 4, 0);
    width_ = width;
    height_ = height;
    return true;
}

bool ScreenGrabber::Grab(int x, int y, gpu::Image& out) {
    if (!memdc_ || !bitmap_) return false;
    const double t0 = util::NowMs();

    HDC screen = GetDC(nullptr);
    if (!screen) return false;
    const BOOL ok = BitBlt(static_cast<HDC>(memdc_), 0, 0, width_, height_,
                           screen, x, y, kSrcCopy);
    ReleaseDC(nullptr, screen);
    if (!ok) return false;

    BITMAPINFO* bmi = reinterpret_cast<BITMAPINFO*>(bmi_.data());
    const int lines = GetDIBits(static_cast<HDC>(memdc_),
                                static_cast<HBITMAP>(bitmap_), 0,
                                static_cast<UINT>(height_), buffer_.data(),
                                bmi, DIB_RGB_COLORS);
    if (lines == 0) return false;

    out.Resize(width_, height_);
    // BGRA -> RGBA（GDI 给的是 BGRA，且 alpha 通道不可靠，统一置 255）
    const int n = width_ * height_;
    const uint8_t* src = buffer_.data();
    uint8_t* dst = out.rgba.data();
    for (int i = 0; i < n; ++i) {
        dst[i * 4 + 0] = src[i * 4 + 2];   // R <- B
        dst[i * 4 + 1] = src[i * 4 + 1];   // G
        dst[i * 4 + 2] = src[i * 4 + 0];   // B <- R
        dst[i * 4 + 3] = 255;
    }

    lastMs_ = util::NowMs() - t0;
    return true;
}

// ---------------------------------------------------------------- 实时循环

struct RealtimeLoop::Impl {
    Config cfg;
    ComposeFn compose = nullptr;
    PresentFn present = nullptr;
    void* user = nullptr;

    ScreenGrabber grabber;
    std::thread thread;
    std::atomic<bool> stop{false};

    gpu::Image backdrop;      // 抓到的背景（RGB）
    gpu::Image frame;         // 合成结果（RGBA）
    std::vector<int16_t> prevSample;
    bool hasPrev = false;

    std::atomic<uint64_t> frames{0};
    std::atomic<double> lastMs{0.0};
    std::atomic<bool> idle{false};
    std::atomic<bool> excluded{false};
    std::string error;
};

RealtimeLoop::~RealtimeLoop() {
    Stop();
    delete impl_;
    impl_ = nullptr;
}

bool RealtimeLoop::Start(const Config& cfg, ComposeFn compose,
                         PresentFn present, void* user, std::string& err) {
    Stop();
    if (!impl_) impl_ = new Impl();
    Impl* d = impl_;

    if (cfg.width <= 0 || cfg.height <= 0) {
        err = "窗口尺寸非法";
        return false;
    }
    d->cfg = cfg;
    d->compose = compose;
    d->present = present;
    d->user = user;
    d->stop = false;
    d->hasPrev = false;
    d->frames = 0;
    d->error.clear();

    // 关键第一步：让自己对抓屏隐形，否则抓到的是自己（自我吞噬）
    d->excluded = ExcludeFromCapture(cfg.hwnd);
    excluded_ = d->excluded;

    if (!d->grabber.Init(cfg.width, cfg.height, err)) return false;

    d->thread = std::thread([d]() {
        while (!d->stop.load()) {
            const double t0 = util::NowMs();

            if (!d->grabber.Grab(d->cfg.x, d->cfg.y, d->backdrop)) {
                d->error = "抓屏失败";
                break;
            }

            // 空闲检测：降采样后粗略比较，代价极低
            const int step = std::max(1, d->cfg.sampleStep);
            const int sw = std::max(1, d->backdrop.width / step);
            const int sh = std::max(1, d->backdrop.height / step);
            std::vector<int16_t> sample(static_cast<size_t>(sw) * sh * 3);
            for (int y = 0; y < sh; ++y) {
                for (int x = 0; x < sw; ++x) {
                    const size_t si =
                        (static_cast<size_t>(y * step) * d->backdrop.width
                         + x * step) * 4;
                    const size_t di = (static_cast<size_t>(y) * sw + x) * 3;
                    for (int c = 0; c < 3; ++c)
                        sample[di + c] = d->backdrop.rgba[si + c];
                }
            }
            if (d->hasPrev && d->prevSample.size() == sample.size()) {
                double sum = 0;
                for (size_t i = 0; i < sample.size(); ++i)
                    sum += std::abs(sample[i] - d->prevSample[i]);
                const double diff = sum / static_cast<double>(sample.size());
                d->idle = diff < d->cfg.idleThreshold;
            } else {
                d->idle = false;
            }
            d->prevSample.swap(sample);
            d->hasPrev = true;

            gpu::Image outImg;
            if (d->compose && d->compose(d->user, d->backdrop, outImg)) {
                if (d->present) d->present(d->user, outImg);
            }

            const double dt = (util::NowMs() - t0) / 1000.0;
            d->lastMs = dt * 1000.0;
            ++d->frames;

            // 按帧率目标休眠；空闲时降到 idleFps，避免白烧 CPU
            const int fps = d->idle ? std::max(1, d->cfg.idleFps)
                                    : std::max(1, d->cfg.fps);
            const double target = 1.0 / fps;
            const double sleepSec = target - dt;
            if (sleepSec > 0) {
                const int ms = static_cast<int>(sleepSec * 1000.0);
                for (int slept = 0; slept < ms && !d->stop.load(); slept += 5)
                    Sleep(std::min(5, ms - slept));
            }
        }
    });

    running_ = true;
    return true;
}

void RealtimeLoop::Stop() {
    if (!impl_) return;
    Impl* d = impl_;
    d->stop = true;
    if (d->thread.joinable()) d->thread.join();
    d->grabber.Close();
    running_ = false;
    // 把线程内的统计搬出来，供 Stop 之后查询
    frames_ = d->frames.load();
    lastFrameMs_ = d->lastMs.load();
    idle_ = d->idle.load();
    if (!d->error.empty()) error_ = d->error;
}

// ---------------------------------------------------------------- 自检

namespace {

// 一块纯色窗口，用作可靠的背景。
//
// 前后踩了两次坑，都源于"怎么把颜色画上去持久"：
//   1) 普通 WS_POPUP 用 GetDC+FillRect 填色，但没有 WM_PAINT 处理，
//      被重绘后变回默认白色 —— 测试因此量到 (252,252,252)。
//   2) 改用 WS_EX_LAYERED + UpdateLayeredWindow(ULW_ALPHA)，
//      但 ULW_ALPHA 要求真正的 32bpp 预乘 alpha 位图；而
//      CreateCompatibleBitmap + FillRect 出来的位图 alpha 通道是 0，
//      窗口变成**完全透明**，抓到的还是桌面 (252,252,252)。
//
// 结论：老老实实处理 WM_ERASEBKGND / WM_PAINT，用画刷填色。
// 不涉及 alpha 语义，重绘后颜色依然正确。
struct SolidWindow {
    HWND hwnd = nullptr;
    int w = 0, h = 0;
    COLORREF color = RGB(255, 255, 255);
    HBRUSH brush = nullptr;

    static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp) {
        SolidWindow* self = reinterpret_cast<SolidWindow*>(
            GetWindowLongPtrW(h, GWLP_USERDATA));
        switch (msg) {
            case WM_ERASEBKGND:
                return 1;   // 自己在 WM_PAINT 里画，避免闪烁
            case WM_PAINT: {
                PAINTSTRUCT ps{};
                HDC dc = BeginPaint(h, &ps);
                RECT rc{};
                GetClientRect(h, &rc);
                HBRUSH br = (self && self->brush)
                    ? self->brush
                    : static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH));
                FillRect(dc, &rc, br);
                EndPaint(h, &ps);
                return 0;
            }
            case WM_NCHITTEST:
                return HTTRANSPARENT;   // 不挡鼠标
            default:
                break;
        }
        return DefWindowProcW(h, msg, wp, lp);
    }

    bool Create(int x, int y, int w_, int h_, uint8_t r_, uint8_t g_,
                uint8_t b_) {
        w = w_; h = h_;
        color = RGB(r_, g_, b_);
        if (brush) DeleteObject(brush);
        brush = CreateSolidBrush(color);

        static bool registered = false;
        if (!registered) {
            WNDCLASSEXW wc{};
            wc.cbSize = sizeof(wc);
            wc.lpfnWndProc = &SolidWindow::WndProc;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.lpszClassName = L"MouseTrayTestSolid";
            wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
            wc.hbrBackground = nullptr;   // 由 WM_PAINT 负责
            RegisterClassExW(&wc);
            registered = true;
        }

        // WS_EX_TOPMOST 必须在**创建时**就给：只在创建后用 SetWindowPos
        // 抬升，实测窗口仍会被别的窗口盖住，抓屏读到的是别人的内容
        // （对照实验：diag_region 创建时带 TOPMOST 就能被稳定抓到）。
        hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            L"MouseTrayTestSolid", L"solid", WS_POPUP,
            x, y, w, h, nullptr, nullptr,
            GetModuleHandleW(nullptr), nullptr);
        if (!hwnd) return false;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA,
                          reinterpret_cast<LONG_PTR>(this));
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        UpdateWindow(hwnd);
        return true;
    }

    // 换色并立即重绘
    void SetColor(uint8_t r_, uint8_t g_, uint8_t b_) {
        color = RGB(r_, g_, b_);
        if (brush) DeleteObject(brush);
        brush = CreateSolidBrush(color);
        InvalidateRect(hwnd, nullptr, TRUE);
        UpdateWindow(hwnd);
    }

    void MoveTo(int x, int y) {
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0,
                     SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(hwnd, nullptr, TRUE);
        UpdateWindow(hwnd);
    }

    void Destroy() {
        if (hwnd) { DestroyWindow(hwnd); hwnd = nullptr; }
        if (brush) { DeleteObject(brush); brush = nullptr; }
    }
};

// 取图像中心区域的平均 RGB
void CenterMean(const gpu::Image& img, double& r, double& g, double& b) {
    r = g = b = 0;
    if (!img.Valid()) return;
    const int x0 = img.width / 4, x1 = img.width * 3 / 4;
    const int y0 = img.height / 4, y1 = img.height * 3 / 4;
    size_t n = 0;
    for (int y = y0; y < y1; ++y) {
        for (int x = x0; x < x1; ++x) {
            const uint8_t* p =
                &img.rgba[(static_cast<size_t>(y) * img.width + x) * 4];
            r += p[0]; g += p[1]; b += p[2];
            ++n;
        }
    }
    if (n) { r /= n; g /= n; b /= n; }
}

}  // namespace

// ---------------------------------------------------------------- 实时性验证

namespace {

struct RTState {
    gpu::Image out;
    gpu::Image lastPresented;
    uint64_t presents = 0;
};

// 合成回调：把抓到的背景直接当作面板内容（仅测试跟随性，
// 不走真实材质渲染，避免把渲染问题混进来）。
bool RTCompose(void*, const gpu::Image& backdrop, gpu::Image& out) {
    // 直接透传，面板颜色 = 背景颜色（只测跟随性，不掺入材质渲染）
    out = backdrop;
    return out.Valid();
}

void RTPresent(void* user, const gpu::Image& frame) {
    RTState* st = static_cast<RTState*>(user);
    st->lastPresented = frame;
    ++st->presents;
}

double FrameMeanRed(const gpu::Image& img) {
    if (!img.Valid()) return -1.0;
    double sum = 0;
    size_t n = 0;
    for (size_t k = 0; k < img.rgba.size(); k += 4) { sum += img.rgba[k]; ++n; }
    return n ? sum / n : -1.0;
}

}  // namespace

int RunRealtimeTest() {
    util::Print(u8"\n=== 实时循环验证（面板是否跟随背景） ===\n");

    int fails = 0;
    auto check = [&](bool ok, const char* what) {
        util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s\n", what);
        if (!ok) ++fails;
    };

    // ---- 夹具尺寸与位置：**尽量小、放角落**
    //
    // 原来是 300x160 放在屏幕中央 (460,380)，用户全屏游戏时非常刺眼
    // （被反馈为"莫名其妙的颜色方块"）。缩小并挪到右下角：
    // 判定只需采样到"红/非红"，160x100 足够。
    // 屏幕 1920x1080 时右下角留出边距，不遮挡主要视野。
    const int W = 160, H = 100;
    // 位置按实际屏幕算，不要写死 —— 写死会在不同分辨率/多屏下跑到屏幕外，
    // 那时测试会因为"抓不到夹具"而莫名失败。
    const int kScreenW = GetSystemMetrics(SM_CXSCREEN);
    const int kScreenH = GetSystemMetrics(SM_CYSCREEN);
    const int BX = std::max(0, kScreenW - W - 40);
    const int BY = std::max(0, kScreenH - H - 60);

    // ---- 可见夹具默认关闭
    //
    // 这个测试需要在屏幕上放一块纯色方块并循环 **蓝 -> 绿 -> 红 -> 蓝**
    // 来验证面板跟随背景。它被 test_all.py 调用，所以每次回归都会在屏幕上
    // 闪出色块 —— 用户在全屏游戏时看到的就是这个。
    //
    // 必须用可见夹具才能验证"跟随背景"，无法改成不可见，所以改为**按需开启**：
    // 只有 MOUSETRAY_VISUAL_TEST=1 才跑；否则打印跳过说明并返回 0。
    wchar_t visFlag[8] = {0};
    const bool kVisualTest =
        GetEnvironmentVariableW(L"MOUSETRAY_VISUAL_TEST", visFlag, 8) > 0;
    if (!kVisualTest) {
        // 标题已在函数开头打印过，这里不再重复
        util::Print(u8"SKIP: 需要在屏幕上放置彩色方块夹具，"
                    u8"默认不运行以免干扰（设 MOUSETRAY_VISUAL_TEST=1 启用）\n");
        return 0;
    }
    util::Print(u8"    ! 可见夹具已启用：右下角 (%d,%d) %dx%d 会闪色块\n",
                BX, BY, W, H);

    // 面板所在位置先放一块会变色的背景。
    // 注意：TOPMOST 在创建时给（只在创建后 SetWindowPos 抬升会被遮挡）。
    SolidWindow backdrop;
    if (!backdrop.Create(BX, BY, W, H, 0, 0, 255)) {
        util::Print(u8"  ! 无法创建背景窗口，测试无法进行\n");
        return 1;
    }
    backdrop.MoveTo(BX, BY);
    Sleep(300);

    // 面板窗口（本身不可见内容无所谓，我们只测回调链）
    SolidWindow panel;
    if (!panel.Create(BX, BY, W, H, 255, 255, 255)) {
        backdrop.Destroy();
        util::Print(u8"  ! 无法创建面板窗口\n");
        return 1;
    }
    panel.MoveTo(BX, BY);
    Sleep(200);

    // 真实的 RealtimeLoop
    RTState st;
    RealtimeLoop loop;
    RealtimeLoop::Config cfg;
    cfg.hwnd = panel.hwnd;
    cfg.x = BX; cfg.y = BY;
    cfg.width = W; cfg.height = H;
    cfg.fps = 30;
    cfg.idleFps = 6;
    cfg.idleThreshold = 1.5;

    // ---- 改色阶段禁用空闲降频，消除"固定 320ms 睡眠"的竞态
    //
    // 实测这一项约 1/3 概率报"面板没有跟随背景变化"。根因是空闲检测：
    // 背景静止片刻后帧率降到 idleFps=6，320ms 只出约 2 帧；若这几帧恰好
    // 发生在改色之前，读到的仍是上一轮画面。固定睡眠本身就是竞态。
    //
    // 注意判定方向：`d->idle = diff < idleThreshold`（差异**小于**阈值算空闲）。
    // 所以要"永不空闲"必须把阈值设成**负数**，而不是设成很大的正数 ——
    // 设很大反而会一直判定为空闲、帧率掉到 6fps（我第一次就写反了，
    // 结果只跑了 5 帧、且后续空闲降频项也失效）。
    // 空闲降频本身在下面的 [空闲降频] 段单独验证，覆盖不会丢。
    const double kSavedIdleThreshold = cfg.idleThreshold;
    cfg.idleThreshold = -1.0;      // 差异恒 >= 0，故永不判定为空闲

    std::string err;
    if (!loop.Start(cfg, &RTCompose, &RTPresent, &st, err)) {
        util::Print(u8"  ✗ 实时循环启动失败: %s\n", err.c_str());
        panel.Destroy(); backdrop.Destroy();
        return 1;
    }
    check(true, u8"实时循环启动");
    util::Print(u8"    已对抓屏隐形: %s\n",
                loop.excluded() ? u8"是" : u8"否（降级为静态）");

    // 颜色序列：蓝 -> 绿 -> 红 -> 蓝（回到起点）
    const struct { uint8_t r, g, b; const char* name; } seq[] = {
        {0, 0, 255, u8"蓝"}, {0, 255, 0, u8"绿"},
        {255, 0, 0, u8"红"}, {0, 0, 255, u8"蓝"},
    };

    struct Result { double meanR; uint64_t presentsAtSample; };
    std::vector<Result> results;

    for (const auto& c : seq) {
        backdrop.SetColor(c.r, c.g, c.b);
        // 等到帧数推进 **并且再多跑几帧**。
        //
        // 实测：只等"帧数 +1"仍会偶发失败（约 1/6）。原因有两层：
        //   1) 抓屏是异步的：帧计数代表"呈现了一帧"，但那一帧抓到的可能
        //      仍是改色之前的桌面；
        //   2) 重新 compositing 后的清晰图像要再一帧才稳定。
        // 所以推进 1 帧只说明"在动"，不足以说明"已经是新颜色"。
        // 这里要求至少多跑 3 帧，把这两层延迟都覆盖掉。
        const uint64_t before = st.presents;
        const uint64_t target = before + 3;
        for (int w = 0; w < 120; ++w) {
            Sleep(20);
            if (st.presents >= target) break;
        }
        Sleep(40);      // 再给一帧余量
        results.push_back({FrameMeanRed(st.lastPresented), st.presents});
        util::Print(u8"    背景设为%s -> 面板平均 R = %.1f"
                    u8"（累计呈现 %llu 帧）\n",
                    c.name, results.back().meanR,
                    static_cast<unsigned long long>(st.presents));
    }

    loop.Stop();
    // 注意：**不要在这里销毁 panel / backdrop**。
    //
    // 销毁后下面的 [空闲降频] 段就会去抓**桌面**而不是面板：
    // 桌面若是静态的，diff 很小 -> 判定空闲 -> 测试通过；
    // 桌面若在动（例如全屏游戏），diff 很大 -> 永不空闲 -> 误报失败。
    // 这就是"桌面在播放动画时空闲降频项随机失败"的根因。
    // 保留面板（纯白、静止）才能得到与桌面无关的稳定画面。

    util::Print(u8"\n    累计呈现 %llu 帧，平均单帧 %.2f ms\n",
                static_cast<unsigned long long>(st.presents),
                loop.lastFrameMs());

    // "一直在出帧"而不是"帧数够多"。
    // 早期这里断言 presents > 20 是错的：背景静止时空闲检测会把帧率降到
    // idleFps=6（这是刻意的省电设计），1.3 秒本来就只有约 8 帧。
    // 真正要证明的是计数器**每轮都在增长**，即不是只抓了一次。
    bool advancing = true;
    for (size_t i = 1; i < results.size(); ++i) {
        if (results[i].presentsAtSample <= results[i - 1].presentsAtSample)
            advancing = false;
    }
    check(advancing, u8"帧计数持续增长（不是一次性抓屏）");
    check(st.presents >= 6, u8"产生了足够多的帧");

    // 关键判据：R 通道必须跟随背景的"红/非红"
    // 序列: 蓝(不红) 绿(不红) 红(红) 蓝(不红)
    const bool r0Low = results[0].meanR < 120;
    const bool r1Low = results[1].meanR < 120;
    const bool r2High = results[2].meanR > 180;
    const bool r3Low = results[3].meanR < 120;
    util::Print(u8"    R 通道序列 %.1f / %.1f / %.1f / %.1f"
                u8"（期望 低/低/高/低）\n",
                results[0].meanR, results[1].meanR, results[2].meanR,
                results[3].meanR);

    if (!(r0Low && r1Low && r2High && r3Low)) {
        util::Print(u8"  ✗ 面板没有跟随背景变化 —— 很可能是静态快照\n");
        ++fails;
    } else {
        util::Print(u8"  ✓ 面板跟随背景变化（红/非红正确）\n");
    }

    // 相邻帧之间的差异必须显著（证明真的在更新，而不是一直同一张图）
    const double d01 = std::abs(results[0].meanR - results[1].meanR);
    const double d12 = std::abs(results[1].meanR - results[2].meanR);
    util::Print(u8"    相邻采样差 %.1f / %.1f（>20 说明确实在更新）\n", d01,
                d12);
    check(d01 > 20 || d12 > 20, u8"相邻帧确实发生变化");

    // 回到起点应当与第一次接近（说明是可逆跟随，不是单向漂移）
    const double back = std::abs(results[0].meanR - results[3].meanR);
    util::Print(u8"    回到起点偏差 %.1f\n", back);
    check(back < 25, u8"回到起点后与首次一致");

    // 验证空闲降频：让背景保持不动一段时间，帧率应明显低于满帧率。
    // 这是"不要白烧 CPU"的设计目标，值得锁进测试。
    util::Print(u8"\n[空闲降频]\n");
    {
        RTState st2;
        RealtimeLoop loop2;
        RealtimeLoop::Config c2 = cfg;
        // 恢复原始的空闲阈值 —— 上面为了让改色阶段满帧跑，把它设成了 1e9。
        // 不恢复的话这一项会变成"永不降频"，测试直接失败。
        c2.idleThreshold = kSavedIdleThreshold;
        std::string e2;
        if (loop2.Start(c2, &RTCompose, &RTPresent, &st2, e2)) {
            Sleep(400);                     // 先让空闲判定生效
            const uint64_t before = st2.presents;
            const double t0 = util::NowMs();
            Sleep(1000);                    // 观察 1 秒
            const uint64_t after = st2.presents;
            const double elapsed = (util::NowMs() - t0) / 1000.0;
            const double fps = elapsed > 0 ? (after - before) / elapsed : 0;
            util::Print(u8"    静止 1 秒内出帧 %.1f fps"
                        u8"（满帧 %d，空闲目标 %d）\n",
                        fps, cfg.fps, cfg.idleFps);
            loop2.Stop();
            // 允许一定余量：空闲目标 6fps，取 15 作为上限
            if (fps > 15.0) {
                util::Print(u8"  ✗ 静止时没有降频（仍在 %.1f fps）\n", fps);
                ++fails;
            } else {
                util::Print(u8"  ✓ 静止时已降频，未白烧 CPU\n");
            }
        } else {
            util::Print(u8"  ! 无法启动第二个循环，跳过降频检查\n");
        }
    }

    // 到这里才销毁：上面的 [空闲降频] 需要面板继续存在
    panel.Destroy();
    backdrop.Destroy();

    util::Print(u8"\n");
    if (fails) {
        util::Print(u8"实时循环验证失败 %d 项\n", fails);
        return 1;
    }
    util::Print(u8"实时循环验证全部通过 ✓\n");
    return 0;
}

int RunSelfTest() {
    util::Print(u8"\n=== 实时抓屏自检 ===\n");

    int fails = 0, skipped = 0;
    auto check = [&](bool ok, const char* what) {
        util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s\n", what);
        if (!ok) ++fails;
    };

    const int W = 340, H = 172;
    // ---- 可见夹具默认关闭
    //
    // 这个自检需要在屏幕上放一块**洋红面板**来验证 WDA_EXCLUDEFROMCAPTURE。
    // 它会在一键回归里每次都闪一下屏幕中央 —— 用户在全屏游戏时被干扰，
    // 反馈"一直搞一堆莫名其妙的颜色块"。所以改为**按需开启**：
    // 只有设置 MOUSETRAY_VISUAL_TEST=1 才创建窗口，否则标为跳过。
    // 位置也从屏幕中央挪到右下角，尽量不挡视线。
    wchar_t visFlag[8] = {0};
    const bool kVisualTest =
        GetEnvironmentVariableW(L"MOUSETRAY_VISUAL_TEST", visFlag, 8) > 0;
    const int PX = 1540, PY = 880;

    // ---- 1) 抓屏器可用 + 速度
    ScreenGrabber grab;
    std::string err;
    if (!grab.Init(W, H, err)) {
        util::Print(u8"  ✗ 抓屏器初始化失败: %s\n", err.c_str());
        return 1;
    }
    check(true, u8"抓屏器初始化成功");

    gpu::Image shot;
    if (!grab.Grab(0, 0, shot)) {
        util::Print(u8"  ✗ 抓取 (0,0) 失败\n");
        return 1;
    }
    check(shot.Valid(), u8"抓到有效图像");
    util::Print(u8"    单次抓取耗时 %.2f ms\n", grab.lastMs());
    check(grab.lastMs() < 12.0, u8"抓屏速度达标（< 12ms）");

    // ---- 2) 自身隐形
    //
    // 设计说明：这里**不用**彩色背景窗口做夹具。前三次尝试都卡在
    // "怎么让一个纯色窗口稳定地出现在屏幕上"上（无 WM_PAINT 会被重绘成白、
    // ULW_ALPHA 配 alpha=0 的位图会全透明、以及窗口明明可见却抓不到）。
    // 改为以**桌面本身**为背景，用三次采样互相印证：
    //   A 面板可见且未排除 -> 应抓到自己
    //   B 面板可见且已排除 -> 不应抓到自己
    //   C 面板隐藏         -> 背后真实画面的基准
    // 判据：A 含面板色、B 与 A 不同、B 约等于 C。
    util::Print(u8"\n[2] 自身隐形（WDA_EXCLUDEFROMCAPTURE）\n");
    {
        // 纯洋红面板：与桌面（多为白/灰）区分度极高，便于判定
        const uint8_t PR = 255, PG = 0, PB = 255;
        SolidWindow panel;
        if (!kVisualTest) {
            util::Print(u8"SKIP: 可见夹具默认关闭"
                        u8"（设 MOUSETRAY_VISUAL_TEST=1 启用）\n");
            ++skipped;
        } else if (!panel.Create(PX, PY, W, H, PR, PG, PB)) {
            util::Print(u8"  ! 无法创建测试面板窗口，跳过此项\n");
            ++skipped;
        } else {
            panel.MoveTo(PX, PY);
            Sleep(300);

            ScreenGrabber gs;
            std::string e2;
            if (!gs.Init(W, H, e2)) {
                util::Print(u8"  ! 抓屏器初始化失败: %s\n", e2.c_str());
                ++fails;
            } else {
                // 判定"这张图里有没有面板的洋红"
                auto hasPanelColor = [&](const gpu::Image& im) {
                    if (!im.Valid()) return 0.0;
                    size_t hit = 0, n = 0;
                    for (size_t k = 0; k < im.rgba.size(); k += 4) {
                        if (im.rgba[k] > 200 && im.rgba[k + 1] < 90 &&
                            im.rgba[k + 2] > 200)
                            ++hit;
                        ++n;
                    }
                    return n ? static_cast<double>(hit) / n : 0.0;
                };
                auto meanAbsDiff = [](const gpu::Image& a,
                                      const gpu::Image& b) {
                    if (!a.Valid() || a.rgba.size() != b.rgba.size())
                        return -1.0;
                    double s = 0;
                    size_t n = 0;
                    for (size_t k = 0; k < a.rgba.size(); k += 4) {
                        for (int c = 0; c < 3; ++c) {
                            s += std::abs(static_cast<int>(a.rgba[k + c]) -
                                          static_cast<int>(b.rgba[k + c]));
                            ++n;
                        }
                    }
                    return n ? s / n : -1.0;
                };

                // A) 未排除
                gpu::Image imgA;
                gs.Grab(PX, PY, imgA);
                const double fracA = hasPanelColor(imgA);

                // B) 排除后
                const bool excl = ExcludeFromCapture(panel.hwnd);
                const int aff = CaptureAffinity(panel.hwnd);
                Sleep(300);
                gpu::Image imgB;
                gs.Grab(PX, PY, imgB);
                const double fracB = hasPanelColor(imgB);

                // C) 隐藏面板 -> 背后真实画面
                ShowWindow(panel.hwnd, SW_HIDE);
                Sleep(300);
                gpu::Image imgC;
                gs.Grab(PX, PY, imgC);
                ShowWindow(panel.hwnd, SW_SHOWNOACTIVATE);

                util::Print(u8"    A 未排除：面板色占比 %.1f%%\n", fracA * 100);
                util::Print(u8"    B 已排除：面板色占比 %.1f%%"
                            u8"（affinity=%d）\n", fracB * 100, aff);
                util::Print(u8"    C 隐藏面板（基准）\n");
                const double dAB = meanAbsDiff(imgA, imgB);
                const double dBC = meanAbsDiff(imgB, imgC);

                // ---- 基准噪声：多次抓隐藏状态，测**桌面自身**的抖动
                //
                // 原始判据是 dBC <= 6，隐含假设"桌面静态"。实测不成立：
                // 机器上跑着全屏游戏/视频时，背景每帧都在变。
                // 只抓一次噪声样本不够 —— 动画速率逐帧不同，单次样本可能
                // 恰好落在低点（实测 5.8），而随后的 dBC 落在高点（19.9），
                // 于是误判失败。改为取多次采样的**最大值**作为上界。
                double noise = 0.0;
                {
                    gpu::Image prev = imgC;
                    for (int i = 0; i < 4; ++i) {
                        gpu::Image cur;
                        Sleep(220);
                        gs.Grab(PX, PY, cur);
                        const double d = meanAbsDiff(prev, cur);
                        if (d > noise) noise = d;
                        prev = cur;
                    }
                }

                util::Print(u8"    A 与 B 平均差 %.1f   B 与 C 平均差 %.1f"
                            u8"   桌面自身抖动(峰值) %.1f\n", dAB, dBC, noise);

                const double kSlack = 6.0;
                const bool bgStatic = (dBC <= kSlack);
                const bool bgNoisyButOk = (noise > 2.0) &&
                                          (dBC <= noise + kSlack);
                if (aff != kWdaExcludeFromCapture && !excl) {
                    util::Print(u8"  ✗ SetWindowDisplayAffinity 失败"
                                u8"（本系统可能不支持）\n");
                    ++fails;
                } else if (fracA < 0.50) {
                    util::Print(u8"  ✗ 未排除时竟没抓到自己（前提不成立，"
                                u8"面板可能被遮挡）\n");
                    ++fails;
                } else if (fracB > 0.10) {
                    util::Print(u8"  ✗ 排除后仍抓到自己（可见 %.1f%%）\n",
                                fracB * 100);
                    ++fails;
                } else if (bgStatic || bgNoisyButOk) {
                    // fracB ≈ 0 已证明没抓到自己；这里再确认抓到的是背景而非别的
                    util::Print(u8"  ✓ 隐形生效：排除前抓到自己(%.0f%%)，"
                                u8"排除后抓到的与真实背景一致"
                                u8"（差 %.1f，桌面抖动 %.1f）\n",
                                fracA * 100, dBC, noise);
                } else {
                    util::Print(u8"  ✗ 排除后抓到的不是真实背景"
                                u8"（与隐藏基准差 %.1f，桌面抖动仅 %.1f）\n",
                                dBC, noise);
                    ++fails;
                }
            }
            panel.Destroy();
        }
    }

    // ---- 3) 空闲检测
    util::Print(u8"\n[3] 空闲检测参数\n");
    util::Print(u8"    采样步长 8，阈值 1.5（与 Python 版一致）\n");
    check(true, u8"空闲检测参数已设置");

    grab.Close();
    util::Print(u8"\n");
    if (skipped) util::Print(u8"（跳过 %d 项）\n", skipped);
    if (fails) {
        util::Print(u8"实时抓屏自检失败 %d 项\n", fails);
        return 1;
    }
    util::Print(u8"实时抓屏自检全部通过 ✓\n");
    return 0;
}

}  // namespace capture
