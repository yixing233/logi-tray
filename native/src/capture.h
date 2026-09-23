// ============================================================================
//  capture.h — 实时抓屏与自身隐形
//
//  从已验证的 Python 版 (realtime_glass.py) 移植。
//
//  为什么需要"自身隐形"：
//    面板要显示"背后是什么"，但面板自己就在屏幕那个位置。
//    直接抓屏会抓到自己（自我吞噬），表现为面板里套着面板。
//    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) 让本窗口对
//    **抓屏 API** 隐形（对肉眼仍可见），于是每帧抓到的就是真实背景，
//    窗口无需隐藏、不会闪烁。
//
//  为什么不用 DXGI 桌面复制（看似更"原生 GPU"）：
//    本机实测 DuplicateOutput 返回 0x887A0005（DXGI_ERROR_UNSUPPORTED），
//    与 GameViewer 虚拟显示适配器有关。BitBlt 虽走 CPU，但复用 DC/位图
//    后实测约 3~5 ms，对 30fps 的面板足够。
// ============================================================================
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "glass_renderer.h"   // gpu::Image

namespace capture {

// WDA_* 取值
constexpr uint32_t kWdaNone = 0x00000000;
constexpr uint32_t kWdaExcludeFromCapture = 0x00000011;

// 让窗口对抓屏 API 隐形。失败返回 false，调用方应降级为静态渲染。
bool ExcludeFromCapture(void* hwnd);

// 读回当前 affinity（自检用）。失败返回 -1。
int CaptureAffinity(void* hwnd);

// 复用 DC / 位图 / 缓冲区的区域抓屏器。
//
// 比每次新建 DC 的写法快很多：后者每帧都要 GetDC + CreateCompatibleBitmap
// + GetDIBits，实测约 20ms；复用后约 3~5ms。
class ScreenGrabber {
public:
    ScreenGrabber() = default;
    ~ScreenGrabber();

    ScreenGrabber(const ScreenGrabber&) = delete;
    ScreenGrabber& operator=(const ScreenGrabber&) = delete;

    // 初始化。width/height 是**抓取区域**尺寸（即面板窗口尺寸）。
    bool Init(int width, int height, std::string& err);
    void Close();
    bool valid() const { return memdc_ != nullptr; }

    // 抓取屏幕 (x, y) 开始的 width×height 区域，写入 out（BGRA 转 RGBA）。
    // 复用内部缓冲，不分配新内存。
    bool Grab(int x, int y, gpu::Image& out);

    // 最近一次抓取耗时（毫秒）
    double lastMs() const { return lastMs_; }

private:
    void* memdc_ = nullptr;
    void* bitmap_ = nullptr;
    void* oldBitmap_ = nullptr;
    std::vector<uint8_t> buffer_;
    std::vector<uint8_t> bmi_;      // BITMAPINFO
    int width_ = 0;
    int height_ = 0;
    double lastMs_ = 0.0;
};

// 后台实时渲染器：按固定帧率抓屏 -> 渲染 -> 回调输出。
//
// 独立线程与窗口消息循环解耦：渲染再慢也不会让窗口"无响应"
// （Python 版踩过这个坑）。
class RealtimeLoop {
public:
    // 回调：收到背后画面（RGB），返回要显示的整窗图像（RGBA 直通 alpha）。
    // 返回 false 表示本帧跳过（不更新画面）。
    using ComposeFn = bool (*)(void* user, const gpu::Image& backdrop,
                               gpu::Image& out);

    RealtimeLoop() = default;
    ~RealtimeLoop();

    RealtimeLoop(const RealtimeLoop&) = delete;
    RealtimeLoop& operator=(const RealtimeLoop&) = delete;

    struct Config {
        void* hwnd = nullptr;
        int x = 0, y = 0;              // 窗口屏幕坐标
        int width = 0, height = 0;     // 窗口尺寸
        int fps = 30;
        int idleFps = 6;
        double idleThreshold = 1.5;    // 降采样平均差低于此值视为静止
        int sampleStep = 8;            // 空闲检测的采样步长
    };

    // 设置"显示一帧"的最终动作（通常是 UpdateLayeredWindow）。
    using PresentFn = void (*)(void* user, const gpu::Image& frame);

    bool Start(const Config& cfg, ComposeFn compose, PresentFn present,
               void* user, std::string& err);
    void Stop();

    bool running() const { return running_; }
    uint64_t frames() const { return frames_; }
    double lastFrameMs() const { return lastFrameMs_; }
    bool idle() const { return idle_; }
    bool excluded() const { return excluded_; }
    std::string error() const { return error_; }

private:
    struct Impl;
    Impl* impl_ = nullptr;
    bool running_ = false;
    uint64_t frames_ = 0;
    double lastFrameMs_ = 0.0;
    bool idle_ = false;
    bool excluded_ = false;
    std::string error_;
};

// ---------------------------------------------------------------- 自检

// 验证：隐形生效、抓屏速度、空闲检测、抓到的确实是背景而非自己
int RunSelfTest();

// 验证实时循环：让背景颜色循环变化，检查面板是否跟随。
// 这是 Python 版最初做错的地方（抓一次屏 -> 静态快照，
// 背景变化时面板变化为 0.000）。
int RunRealtimeTest();

}  // namespace capture
