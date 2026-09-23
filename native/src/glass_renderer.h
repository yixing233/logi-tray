// ============================================================================
//  glass_renderer.h — GPU（D3D11）材质渲染
//
//  为什么换到 GPU：Python/numpy 版把液态玻璃的逐像素运算放在 CPU 上，
//  实测 11~35 ms/帧，而 RTX 5060 基本闲置（占用 7%）。
//  同一套算法搬到 HLSL 后实测 0.029 ms/帧。
//
//  三种材质都在这里实现，与 Python 版 (glass_effects.py) 的算法一一对应：
//    Liquid  圆角 SDF 求法线 -> 边缘倒角带内折射 -> RGB 按波长错开采样产生
//            色散 -> 内侧暗肩 + 亮边（向白提升而非叠加）
//    Acrylic 模糊窗口正后方 + 噪声颗粒 + 色调叠加
//    Mica    取**桌面壁纸**色调（不是窗口后方）+ 强混合 + 几乎不模糊
//
//  设计要点：
//    * 全屏三角形（不需要顶点缓冲），blur 做可分离两趟
//    * 材质在指定分辨率上算，调用方可传更小的画布做超采样或降采样
//    * 读回用 staging texture；分层窗口需要预乘 alpha，这里输出**直通 alpha**，
//      由调用方在送 UpdateLayeredWindow 前预乘（372x204 的 CPU 开销可忽略）
// ============================================================================
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace gpu {

enum class Material { Liquid, Acrylic, Mica };

const char* MaterialName(Material m);

struct Params {
    // 液态玻璃
    float refraction = 0.55f;      // 折射强度
    float dispersion = 0.45f;      // 色散强度
    float splay = 0.50f;           // 倒角带宽度
    float depth = 0.55f;           // 玻璃"厚度"（决定折射位移量）
    float frost = 0.35f;           // 磨砂程度
    float lightAngle = 2.0071f;    // 光源方向（弧度，约 115°）
    float lightIntensity = 0.80f;  // 光照强度
    // 亚克力 / 云母
    float blur = 26.0f;            // 模糊半径
    float grain = 0.030f;          // 噪声颗粒强度
    float opacity = 0.55f;         // 表面色占比
    float saturate = 0.55f;        // 饱和度（云母用）
    // 亚克力亮度层的对比强度。
    // 该层是"对比压缩"，公式 lumOut = (lum - pivot) * contrast + pivot；
    // pivot 取背景平均亮度（Compose 里按实际背景算，不是固定的 0.5）。
    // 早期 pivot 固定 0.5，导致浅色桌面被整体压暗 20%（实测 247 -> 205）。
    float contrast = 0.50f;

    // ---- 投影
    // 早期 sigma=7、强度 0.45 写死在着色器里，实测峰值把面板周围压暗 55%
    // （234 -> 105），看起来像一圈厚黑边框而不是投影。Windows 弹窗大约
    // 12~18%，且更宽更散。这里改成可调，默认宽而淡。
    float shadowSigma = 10.0f;     // 高斯半径，越大越散
    float shadowStrength = 0.16f;  // 峰值不透明度
    float shadowOffsetY = 3.0f;    // 向下的偏移，模拟光来自上方

    // 表面色：材质与背景混合的基准色。
    // **必须显式给出** —— 留 0 会把画面混向黑色
    // （实测漏设时云母亮度从 179 掉到 38，且三材质亮度排序颠倒）。
    float surfaceR = 249, surfaceG = 249, surfaceB = 250;  // 浅色主题
    float dark = 0.0f;             // 1 = 深色主题（影响颗粒强度）

    // 按材质套用各自的默认值（与 glass_effects.DEFAULTS 对应）
    void ApplyMaterialDefaults(Material m);
};

// 一张 RGBA 图，**直通 alpha**（非预乘）
struct Image {
    int width = 0;
    int height = 0;
    std::vector<uint8_t> rgba;   // width*height*4

    bool Valid() const {
        return width > 0 && height > 0 &&
               rgba.size() == static_cast<size_t>(width) * height * 4;
    }
    void Resize(int w, int h) {
        width = w;
        height = h;
        rgba.assign(static_cast<size_t>(w) * h * 4, 0);
    }
};

// RGBA <-> BGRA 通道交换（原地）。
//
// 三套通道约定在这里交汇，必须显式转换：
//   * GPU 读回 DXGI_FORMAT_R8G8B8A8_UNORM  内存序 = R,G,B,A
//   * GDI+ PixelFormat32bppARGB            内存序 = B,G,R,A
//   * CreateDIBSection 32bpp BI_RGB        内存序 = B,G,R,A
// 不转换会 R/B 互换：红色画成蓝色、蓝色画成橙色。
// **这个错误数值对比发现不了**（换通道几乎不改变亮度与均值）。
void SwapRedBlue(Image& img);

// 纯色图（测试用）
Image SolidImage(int w, int h, uint8_t r, uint8_t g, uint8_t b, uint8_t a = 255);

class Renderer {
public:
    Renderer();
    ~Renderer();

    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;

    // 初始化设备并编译着色器。失败时 err 带原因。
    bool Init(std::string& err);
    void Shutdown();
    bool ready() const { return ready_; }

    // 渲染一块材质面板。
    //   backdrop  窗口背后的画面（亚克力/液态玻璃用），尺寸任意，内部会缩放
    //   wallpaper 桌面壁纸（云母用）；为空则退回 backdrop
    //   inset     面板相对画布的内边距；这块边距是液态玻璃折射的采样源
    //   radius    面板圆角半径（画布像素）
    // 输出 out 为 canvasW×canvasH 的直通 alpha RGBA。
    bool Render(const Image& backdrop, const Image& wallpaper,
                Material material, const Params& p,
                int canvasW, int canvasH, float inset, float radius,
                Image& out, std::string& err);

    // 上一次渲染的 GPU 耗时（毫秒），用于性能核对
    double lastMs() const { return lastMs_; }
    // 渲染若干次取平均（预热后），用于基准测试
    double BenchmarkMs(const Image& backdrop, const Image& wallpaper,
                       Material material, const Params& p,
                       int canvasW, int canvasH, float inset, float radius,
                       int iterations);

    const std::string& deviceName() const { return deviceName_; }

private:
    struct Impl;
    Impl* impl_ = nullptr;
    bool ready_ = false;
    double lastMs_ = 0.0;
    std::string deviceName_;
};

// 自检：设备、着色器、三种材质渲染、参数响应、性能
int RunSelfTest();

// 把某种材质的渲染结果写到文件（供与 Python 版做对比）。
// 写 32 位 BMP：无需第三方库，PIL 可直接读。
bool DumpMaterial(const std::string& material, const std::string& outPath,
                  const std::string& err);

}  // namespace gpu
