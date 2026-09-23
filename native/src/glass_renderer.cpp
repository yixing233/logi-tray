// ============================================================================
//  glass_renderer.cpp — D3D11 材质渲染实现
//
//  着色器全部内嵌（运行时用 D3DCompile 编译），避免额外的构建步骤。
//
//  渲染流程（所有材质共用）：
//    1. 把背景 blit 到画布尺寸的纹理
//    2. 可分离高斯模糊：横向 -> 纵向
//    3. 主 pass：按材质合成（液态玻璃还会叠加折射/色散/亮边）
//    4. 读回到 CPU（分层窗口需要 CPU 侧像素）
//
//  常量缓冲刻意全部用 float4 排列。HLSL 会把 float3 向 16 字节边界对齐，
//  与 C++ 结构体镜像时极易错位（liquidock 的注释里也记了这个坑）。
//  全 float4 就没有对齐歧义。
// ============================================================================

#include "glass_renderer.h"
#include "util.h"

#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>

#include <cstring>
#include <cmath>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3dcompiler.lib")

namespace gpu {

const char* MaterialName(Material m) {
    switch (m) {
        case Material::Liquid:  return u8"液态玻璃";
        case Material::Acrylic: return u8"亚克力";
        case Material::Mica:    return u8"云母";
    }
    return "?";
}

// ---------------------------------------------------------------- 材质默认值

// 每种材质有各自的默认参数（对应 Python 版 glass_effects.DEFAULTS）。
// 早先把三套默认值混成一个 Params，导致云母用了亚克力的 opacity(0.55)
// 而不是自己的 0.86，观感就不对了。
void Params::ApplyMaterialDefaults(Material m) {
    const bool isDark = dark > 0.5f;
    if (isDark) {
        surfaceR = 32; surfaceG = 32; surfaceB = 34;
    } else {
        surfaceR = 249; surfaceG = 249; surfaceB = 250;
    }

    switch (m) {
        case Material::Liquid:
            refraction = 0.55f;
            dispersion = 0.45f;
            splay = 0.50f;
            depth = 0.55f;
            frost = 0.35f;
            lightAngle = 2.0071f;      // 约 115 度
            lightIntensity = 0.80f;
            break;
        case Material::Acrylic:
            // 模糊要够大才是"毛玻璃"而不是"半透明塑料"。
            // 30 在 372x204 画布上约等于半径 30px，接近 Win11 亚克力的
            // 观感；再大就糊成一片没有细节了。
            blur = 30.0f;
            grain = 0.022f;      // 噪声收细，避免"脏"
            opacity = 0.50f;     // 适中：既成色又不盖住背景
            break;
        case Material::Mica:
            blur = 2.0f;
            opacity = 0.86f;           // 云母几乎不透明
            saturate = 0.55f;
            grain = 0.0f;              // 云母没有颗粒
            break;
    }
}

// ---------------------------------------------------------------- 着色器

namespace shaders {

// 全屏三角形：不需要顶点缓冲
static const char* kCommon = R"HLSL(
struct VSOut { float4 pos : SV_Position; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    // 覆盖整个屏幕的大三角形
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;      // 屏幕 y 向下
    return o;
}
)HLSL";

// 背景 blit（顺便可选做饱和度调整，云母用）
static const char* kBlit = R"HLSL(
Texture2D    gSrc : register(t0);
SamplerState gLin : register(s0);

cbuffer Params : register(b0)
{
    float4 gSize;      // xy = canvas
    float4 gShape;     // xy = panel half, z = radius, w = inset
    float4 gLiquid;    // x refraction, y dispersion, z splay, w depth
    float4 gLight;     // x frost, y angle, z intensity, w grain
    float4 gSurface;   // rgb surface, a opacity
    float4 gMisc;      // x material, y dark, z saturate, w unused
    float4 gExtra;      // x 亮度层基准(pivot), y 对比强度, zw 保留
    float4 gShadow;     // x sigma, y strength, z offsetY, w 圆角抗锯齿宽度
};

float4 PSBlit(VSOut i) : SV_Target
{
    float2 uv = i.pos.xy / gSize.xy;
    float3 c = gSrc.SampleLevel(gLin, uv, 0).rgb;

    // 云母：降饱和（它的观感是"壁纸被调过色的版本"，不是原图）。
    // 必须**只在云母**上做 —— 早先无条件应用，亚克力也被降饱和，
    // 导致亚克力与云母渲染结果几乎相同（实测平均绝对差仅 0.52）。
    // 注意：这里不能用 MATERIAL 宏 —— 它只在 kMain 里定义，
    // blit 着色器里必须直接读原始分量。
    if (gMisc.x >= 1.5)
    {
        float lum = dot(c, float3(0.299, 0.587, 0.114));
        c = lerp(float3(lum, lum, lum), c, gMisc.z);
    }

    return float4(c, 1.0);
}
)HLSL";

// 可分离高斯模糊（横/纵由 gDir 决定）
static const char* kBlur = R"HLSL(
Texture2D    gSrc : register(t0);
SamplerState gLin : register(s0);

cbuffer Params : register(b0)
{
    float4 gSize;
    float4 gShape;
    float4 gLiquid;
    float4 gLight;
    float4 gSurface;
    float4 gMisc;      // x material, y dark, z saturate, w = blur radius
    float4 gExtra;      // x 亮度层基准(pivot), y 对比强度, zw 保留
    float4 gShadow;     // x sigma, y strength, z offsetY, w 圆角抗锯齿宽度
};

float4 PSBlur(VSOut i) : SV_Target
{
    float2 texel = 1.0 / gSize.xy;
    float  radius = max(gMisc.w, 0.0);

    // 半径 <= 0.3 时直接拷贝，避免无谓采样
    if (radius <= 0.3) {
        return float4(gSrc.SampleLevel(gLin, i.pos.xy * texel, 0).rgb, 1.0);
    }

    // 横纵方向由 gSize.w 之外的约定传：用 gShape.w 当方向开关
    //   gShape.w > 0.5 -> 纵向
    float2 dir = (gShape.w > 0.5) ? float2(0.0, 1.0) : float2(1.0, 0.0);

    // PIL 的 GaussianBlur(radius) 里 radius **就是标准差**。
    //
    // 采样间隔必须是 **1 个纹素**，且抽头数随 sigma 增长。
    // 早期用固定的 9 抽头 + step = 0.625*sigma：大 sigma 时间隔会超过条纹
    // 周期（亚克力 sigma=26 -> 间隔 16.25px，正好等于 16px 的条纹周期），
    // 9 个抽头全部落在同一相位，模糊等于没做（亚克力输出仍能看到清晰条纹）。
    // 输入已经做过盒式降采样，因此按纹素中心采样不会混叠。
    const float sigma = max(radius, 0.6);
    const float inv2s2 = 1.0 / (2.0 * sigma * sigma);

    // 覆盖 ±2.5σ，上限 64 抽头（单侧），避免极端 sigma 拖垮性能
    int half = (int)ceil(2.5 * sigma);
    half = clamp(half, 1, 64);

    float3 sum = 0.0;
    float  wsum = 0.0;
    for (int k = -half; k <= half; ++k)
    {
        float off = float(k);                  // 1 纹素步长
        float w = exp(-(off * off) * inv2s2);
        sum += gSrc.SampleLevel(gLin, (i.pos.xy + dir * off) * texel, 0).rgb * w;
        wsum += w;
    }
    return float4(sum / max(wsum, 1e-5), 1.0);
}
)HLSL";

// 盒式降采样（4x4）。
//
// **必须先把全分辨率图限带再降采样**。早期直接在低分辨率上用大间隔抽头去
// 采全分辨率纹理，抽头间隔恰好等于条纹周期时，9 个抽头会全部落在同一相位，
// 模糊等于没做（实测亚克力仍显示清晰条纹）。这里用 16 抽头做一次真正的
// 面积平均，之后在低分辨率上模糊就不会混叠。
static const char* kDown = R"HLSL(
Texture2D    gSrc : register(t0);
SamplerState gLin : register(s0);

cbuffer Params : register(b0)
{
    float4 gSize;
    float4 gShape;
    float4 gLiquid;
    float4 gLight;
    float4 gSurface;
    float4 gMisc;      // w = 降采样倍率
    float4 gExtra;      // x 亮度层基准(pivot), y 对比强度, zw 保留
    float4 gShadow;     // x sigma, y strength, z offsetY, w 圆角抗锯齿宽度
};

float4 PSDown(VSOut i) : SV_Target
{
    float2 srcSize = gSize.zw;          // zw = 源尺寸
    float  fac = max(gMisc.w, 1.0);
    float2 dstSize = srcSize / fac;

    // 目标像素中心在源图上的位置
    float2 base = (i.pos.xy + 0.5) * fac;

    float3 sum = 0.0;
    [unroll]
    for (int j = 0; j < 4; ++j)
    {
        [unroll]
        for (int k = 0; k < 4; ++k)
        {
            float2 sp = base + float2(float(k), float(j)) - 0.5;
            sum += gSrc.SampleLevel(gLin, (sp + 0.5) / srcSize, 0).rgb;
        }
    }
    return float4(sum / 16.0, 1.0);
}
)HLSL";

// 主合成：三种材质
static const char* kMain = R"HLSL(
Texture2D    gBlur : register(t0);   // 模糊后的背景
Texture2D    gSharp : register(t1);  // 原始背景（未用，留作扩展）
SamplerState gLin : register(s0);

cbuffer Params : register(b0)
{
    float4 gSize;      // xy = canvas
    float4 gShape;     // xy = panel half, z = radius, w = inset
    float4 gLiquid;    // x refraction, y dispersion, z splay, w depth
    float4 gLight;     // x frost, y angle, z intensity, w grain
    float4 gSurface;   // rgb surface, a opacity
    float4 gMisc;      // x material, y dark, z saturate, w unused
    float4 gExtra;      // x 亮度层基准(pivot), y 对比强度, zw 保留
    float4 gShadow;     // x sigma, y strength, z offsetY, w 圆角抗锯齿宽度
};

#define CANVAS      gSize.xy
#define PANEL_C     gSize.zw
#define PANEL_HALF  gShape.xy
#define RADIUS      gShape.z
#define REFRACTION  gLiquid.x
#define DISPERSION  gLiquid.y
#define SPLAY       gLiquid.z
#define DEPTH       gLiquid.w
#define FROST       gLight.x
#define LIGHT_ANGLE gLight.y
#define LIGHT_INT   gLight.z
#define GRAIN       gLight.w
#define SURFACE     gSurface.rgb
#define OPACITY     gSurface.a
#define MATERIAL    gMisc.x      // 0=liquid 1=acrylic 2=mica
#define LUM_PIVOT    gExtra.x   // 亮度层基准 = 背景平均亮度
#define LUM_CONTRAST gExtra.y   // 亮度层对比强度
#define SHADOW_SIGMA  gShadow.x
#define SHADOW_STR    gShadow.y
#define SHADOW_OFFY   gShadow.z
#define AA_WIDTH      max(gShadow.w, 0.5)

// 圆角矩形有向距离场（负值在内部）
float SdRoundedBox(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

// 便宜的噪声（亚克力的颗粒）
float Hash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float4 PSMain(VSOut i) : SV_Target
{
    float2 uv = i.pos.xy / CANVAS;
    float2 p = i.pos.xy - PANEL_C;

    float d = SdRoundedBox(p, PANEL_HALF, RADIUS);

    // 导数必须在任何 discard 之前取，否则被杀的 lane 会让邻域导数未定义，
    // 表现为边缘撕裂/闪烁（Python 版也遇到过同类问题）。
    float2 grad = float2(ddx(d), ddy(d));
    float2 outward = normalize(grad + 1e-6);
    // 抗锯齿：过渡宽度可调。
    // 半径小的时候 1px 过渡会在拐角留下明显阶梯（放大 6x 可见），
    // 稍微加宽过渡即可平滑，同时不糊掉直边。
    float  coverage = saturate(0.5 - d / AA_WIDTH);

    // ---- 面板外侧：只画投影
    //
    // 早期是 sigma=7 / 强度 0.45 写死，实测把面板周围压暗 55%，
    // 看着像一圈厚黑边框。改成宽而淡（默认 sigma=14 / 0.16），
    // 并按 Windows 弹窗的习惯给一点向下的偏移 —— 光来自上方。
    if (coverage <= 0.001)
    {
        // 向下偏移：用**外法线的 y 分量**做方向性偏移。
        // 注意不能写成 saturate(-d/6) —— 外侧 d>0，那项恒为 0，偏移会失效。
        //   outward.y = +1（面板下方）-> dOff = d - k，阴影更浓更长
        //   outward.y = -1（面板上方）-> dOff = d + k，阴影更淡
        float dOff = d - SHADOW_OFFY * outward.y;
        float sigma = max(SHADOW_SIGMA, 0.5);
        float s = exp(-(dOff * dOff) / (2.0 * sigma * sigma)) * SHADOW_STR;
        if (s <= 0.002) discard;
        return float4(0.0, 0.0, 0.0, s);
    }

    // ---- 背景（已模糊）
    float3 col = gBlur.SampleLevel(gLin, uv, 0).rgb;

    // ============================ 液态玻璃 ============================
    if (MATERIAL < 0.5)
    {
        // 倒角带：edge 在边界为 1，向内衰减到 0
        float halfMin = max(max(PANEL_HALF.x, PANEL_HALF.y), 1.0);
        float splay = saturate(SPLAY);
        float band  = halfMin * lerp(0.08, 1.00, splay);
        float inset = saturate(-d / band);
        float edge  = pow(max(1.0 - inset, 1e-5), lerp(3.0, 1.15, splay));

        // 折射位移：采样点向外移，把面板外侧的画面挤进来。
        // 系数与 Python 版一致（1.2 倍 depth 放大），
        // 早期 Python 版在正好等于面板尺寸的画布上算，向外采样全部越界，
        // 折射静默失效（实测 refraction 0->1 边缘变化 0.00）——
        // 所以这里同样要求画布比面板大一圈（inset 提供边距）。
        float depth = saturate(DEPTH);
        float bend  = REFRACTION * (1.0 + 19.0 * depth) * 1.2;
        float off   = edge * edge * bend;
        float2 disp = outward * off;

        float2 texel = 1.0 / CANVAS;
        float  spread = DISPERSION * 0.38;

        float3 refr;
        refr.r = gBlur.SampleLevel(gLin, uv + disp * (1.0 + spread) * texel, 0).r;
        refr.g = gBlur.SampleLevel(gLin, uv + disp * texel, 0).g;
        refr.b = gBlur.SampleLevel(gLin, uv + disp * (1.0 - spread) * texel, 0).b;
        col = refr;

        // 玻璃自身色调
        col = lerp(col, SURFACE, 0.28);

        // 上下轻微明暗
        float vert = saturate((i.pos.y - (PANEL_C.y - PANEL_HALF.y))
                              / max(2.0 * PANEL_HALF.y, 1.0));
        col += (1.0 - col) * 0.020 * (1.0 - vert);

        // 内侧暗肩：亮边要"站"在暗处才像被照亮的棱
        float inward = -d;
        float u = saturate((inward - 1.5) / 4.5);
        float shoulder = u * pow(1.0 - u, 3.0) / 0.1055;
        col *= 1.0 - shoulder * 0.26;

        // 亮边：向白"提升"而非叠加（叠加在深色背景上会变成灰线）
        float2 lightPlane = float2(-cos(LIGHT_ANGLE), sin(LIGHT_ANGLE));
        float  facing = dot(outward, lightPlane);
        float  edgeUp = abs(facing);
        float  rim = saturate(1.0 - abs(inward - 1.5) / 1.1);
        float  lift = saturate(saturate(LIGHT_INT)
                               * (0.020 + edgeUp * (0.430 + 0.070 * facing))
                               / 0.80);
        col = lerp(col, 1.0.xxx, rim * lift);

        // 内侧柔和反光
        float inner = pow(saturate(1.0 + d / 16.0), 2.0);
        col = lerp(col, 1.0.xxx, saturate(-outward.y) * inner * 0.05
                                  * saturate(LIGHT_INT));
    }
    // ============================ 亚克力 ==============================
    //
    // 按 Win11 Acrylic 的分层配方实现，而不是"往模糊图上刷一层白"：
    //    1. 模糊        —— 上游 pass 已完成（col 即模糊结果）
    //    2. 亮度层      —— 压低对比度：L' = (L-0.5)*0.5+0.5
    //    3. 去饱和      —— 只保留少量背景色相，避免整块染上壁纸的色调
    //    4. 中性色调    —— 用小 alpha 混入 SURFACE，保持"玻璃"的中性感
    //    5. 细噪点      —— 大色块防 banding
    //
    // 之前缺少第 3 步，于是在暖色壁纸上一整块变成粉紫色厚板 ——
    // 观感完全不对。关键认识：亚克力不该把背景的**颜色**照单全收，
    // 它只让背景的**明暗结构**透过来，颜色由中性色调决定。
    else
    {
        // ---- 2) 亮度层：围绕**背景自身的平均亮度**压缩对比
        //
        // 早期写成固定绕 0.5 压缩：lumOut = (lum - 0.5) * 0.5 + 0.5。
        // 于是纯白背景 lum=0.96 被压成 0.73（255 -> 186），
        // 再叠上中性色调后约 205 —— 实测浅色桌面上"面板比背景暗 20%"，
        // 这就是用户看到的灰暗。
        //
        // 正确做法是围绕背景平均亮度 pivot 压缩：
        //     lumOut = (lum - pivot) * contrast + pivot
        // 均匀白底时 pivot == lum，结果不变 —— 面板保持桌面亮度，
        // 只多了柔化、色调与颗粒，这才是亚克力该有的样子。
        float lum = dot(col, float3(0.2126, 0.7152, 0.0722));
        float lumOut = clamp((lum - LUM_PIVOT) * LUM_CONTRAST + LUM_PIVOT,
                             0.0, 1.0);
        col *= (lumOut / max(lum, 0.02));

        // ---- 3) 去饱和：这一步是"看起来像亚克力"的关键。
        //      saturation 约 0.25 时仍能看出背景的明暗与淡淡色相，
        //      但不会整块被壁纸染成紫/粉/黄。
        float grey = dot(col, float3(0.2126, 0.7152, 0.0722));
        col = lerp(float3(grey, grey, grey), col, 0.25);

        // ---- 4) 中性色调：alpha 适中，保证"玻璃感"而不发白
        col = lerp(col, SURFACE, saturate(OPACITY) * 0.55);

        // ---- 5) 细噪点：两个频率叠加，避免规则网格
        if (GRAIN > 0.0)
        {
            float n1 = Hash(i.pos.xy) - 0.5;
            float n2 = Hash(i.pos.xy * 2.13 + 37.0) - 0.5;
            col += (n1 * 0.7 + n2 * 0.3) * GRAIN *
                   lerp(0.30, 0.50, saturate(gMisc.y));
        }

        // ---- 上缘微亮：光源在上方
        float vert = saturate((i.pos.y - (PANEL_C.y - PANEL_HALF.y))
                              / max(2.0 * PANEL_HALF.y, 1.0));
        col += (1.0 - vert) * 0.018;

        // ---- 内缘高光：1.5px 以内的细亮边，给面板一个"玻璃边缘"
        //      只有在这里画，才不会变成厚重的描边
        float inward = max(-d, 0.0);
        float rim = saturate(1.0 - inward / 1.5);
        col = lerp(col, 1.0.xxx, rim * 0.16);

        // ---- 外缘细暗线：让面板与桌面之间有分界
        //      d 略大于 0 的位置（面板外 1px）
        float outer = saturate(1.0 - abs(d - 0.6) / 1.0);
        col *= 1.0 - outer * 0.06;
    }

    return float4(saturate(col), coverage);
}
)HLSL";

}  // namespace shaders

// ---------------------------------------------------------------- 常量缓冲

// 全 float4：避免 HLSL 的 16 字节对齐与 C++ 镜像错位
struct CBuffer {
    float size[4];      // canvas.xy, panelCenter.xy
    float shape[4];     // panelHalf.xy, radius, blurDir
    float liquid[4];    // refraction, dispersion, splay, depth
    float light[4];     // frost, lightAngle, lightIntensity, grain
    float surface[4];   // rgb, opacity
    float misc[4];      // material, dark, saturate, blurRadius
    float extra[4];     // x = 亮度层基准(pivot), y = 对比强度
    float shadow[4];    // x sigma, y strength, z offsetY, w AA 宽度
};
static_assert(sizeof(CBuffer) == 128, "constant buffer must stay 8 float4");

// ---------------------------------------------------------------- 实现

struct Renderer::Impl {
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* ctx = nullptr;

    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* psBlit = nullptr;
    ID3D11PixelShader* psBlur = nullptr;
    ID3D11PixelShader* psMain = nullptr;
    ID3D11PixelShader* psDown = nullptr;
    ID3D11Buffer* cb = nullptr;
    ID3D11SamplerState* sampler = nullptr;
    ID3D11RasterizerState* raster = nullptr;
    ID3D11BlendState* blendOpaque = nullptr;

    // 1/4 分辨率的模糊缓冲。大半径模糊必须在低分辨率上做：
    // sigma=11 时抽头间隔约 6.9px，在 8px 周期的条纹上会直接采出摩尔纹，
    // 看起来像"没模糊"。降到 1/4 分辨率后等效 sigma 与间隔同比缩小，
    // 再靠双线性上采样回全尺寸，得到平滑的大范围模糊。
    ID3D11Texture2D* texH1 = nullptr;
    ID3D11ShaderResourceView* srvH1 = nullptr;
    ID3D11RenderTargetView* rtvH1 = nullptr;
    ID3D11Texture2D* texH2 = nullptr;
    ID3D11ShaderResourceView* srvH2 = nullptr;
    ID3D11RenderTargetView* rtvH2 = nullptr;
    int hw = 0, hh = 0;
    static const int kDownscale = 4;

    // 画布资源（尺寸变化时重建）
    ID3D11Texture2D* texA = nullptr;          // 源（画布尺寸）
    ID3D11ShaderResourceView* srvA = nullptr;
    ID3D11Texture2D* texB = nullptr;          // 模糊中间
    ID3D11ShaderResourceView* srvB = nullptr;
    ID3D11RenderTargetView* rtvB = nullptr;
    ID3D11Texture2D* texC = nullptr;          // 模糊结果 / 最终输出
    ID3D11ShaderResourceView* srvC = nullptr;
    ID3D11RenderTargetView* rtvC = nullptr;
    ID3D11Texture2D* staging = nullptr;
    int texW = 0, texH = 0;
    int stagingW = 0, stagingH = 0;

    void ReleaseCanvas() {
        auto rel = [](auto*& p) { if (p) { p->Release(); p = nullptr; } };
        rel(texA); rel(srvA); rel(texB); rel(srvB); rel(rtvB);
        rel(texC); rel(srvC); rel(rtvC);
        rel(texH1); rel(srvH1); rel(rtvH1);
        rel(texH2); rel(srvH2); rel(rtvH2);
        texW = texH = hw = hh = 0;
    }
    void ReleaseStaging() {
        if (staging) { staging->Release(); staging = nullptr; }
        stagingW = stagingH = 0;
    }
    void ReleaseAll() {
        ReleaseCanvas();
        ReleaseStaging();
        auto rel = [](auto*& p) { if (p) { p->Release(); p = nullptr; } };
        rel(vs); rel(psBlit); rel(psBlur); rel(psMain); rel(psDown);
        rel(cb); rel(sampler); rel(raster); rel(blendOpaque);
        rel(ctx); rel(device);
    }

    bool Compile(const char* src, const char* entry, const char* target,
                 ID3DBlob** out, std::string& err) {
        ID3DBlob* code = nullptr;
        ID3DBlob* errors = nullptr;
        const HRESULT hr = D3DCompile(src, std::strlen(src), "inline",
                                      nullptr, nullptr, entry, target,
                                      D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                                      &code, &errors);
        if (FAILED(hr)) {
            err = "着色器编译失败 (";
            err += entry;
            err += "): ";
            if (errors) err += static_cast<const char*>(
                errors->GetBufferPointer());
            if (errors) errors->Release();
            if (code) code->Release();
            return false;
        }
        if (errors) errors->Release();
        *out = code;
        return true;
    }

    bool BuildPipeline(std::string& err) {
        // 把公共 VS 与各 PS 拼起来
        const std::string vsSrc = std::string(shaders::kCommon)
            + shaders::kBlit;   // VSOut 定义在 kCommon 里

        ID3DBlob* blobs[4] = {nullptr, nullptr, nullptr, nullptr};
        if (!Compile(vsSrc.c_str(), "VSMain", "vs_5_0", &blobs[0], err))
            return false;

        auto mk = [&](const char* ps, const char* entry,
                      ID3DBlob** out) -> bool {
            const std::string src = std::string(shaders::kCommon) + ps;
            return Compile(src.c_str(), entry, "ps_5_0", out, err);
        };
        if (!mk(shaders::kBlit, "PSBlit", &blobs[1])) return false;
        if (!mk(shaders::kBlur, "PSBlur", &blobs[2])) return false;
        if (!mk(shaders::kMain, "PSMain", &blobs[3])) return false;

        ID3DBlob* downBlob = nullptr;
        if (!mk(shaders::kDown, "PSDown", &downBlob)) return false;

        HRESULT hr = device->CreateVertexShader(
            blobs[0]->GetBufferPointer(), blobs[0]->GetBufferSize(),
            nullptr, &vs);
        if (FAILED(hr)) { err = "CreateVertexShader 失败"; return false; }
        hr = device->CreatePixelShader(
            blobs[1]->GetBufferPointer(), blobs[1]->GetBufferSize(),
            nullptr, &psBlit);
        if (FAILED(hr)) { err = "CreatePixelShader(blit) 失败"; return false; }
        hr = device->CreatePixelShader(
            blobs[2]->GetBufferPointer(), blobs[2]->GetBufferSize(),
            nullptr, &psBlur);
        if (FAILED(hr)) { err = "CreatePixelShader(blur) 失败"; return false; }
        hr = device->CreatePixelShader(
            blobs[3]->GetBufferPointer(), blobs[3]->GetBufferSize(),
            nullptr, &psMain);
        if (FAILED(hr)) { err = "CreatePixelShader(main) 失败"; return false; }

        hr = device->CreatePixelShader(
            downBlob->GetBufferPointer(), downBlob->GetBufferSize(),
            nullptr, &psDown);
        downBlob->Release();
        if (FAILED(hr)) { err = "CreatePixelShader(down) 失败"; return false; }

        for (auto* b : blobs) if (b) b->Release();
        return true;
    }

    bool EnsureCanvas(int w, int h, std::string& err) {
        if (texW == w && texH == h && texA) return true;
        ReleaseCanvas();
        ReleaseStaging();

        D3D11_TEXTURE2D_DESC td{};
        td.Width = w;
        td.Height = h;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE
                     | D3D11_BIND_RENDER_TARGET;

        HRESULT hr;
        hr = device->CreateTexture2D(&td, nullptr, &texA);
        if (FAILED(hr)) { err = "创建纹理 A 失败"; return false; }
        hr = device->CreateShaderResourceView(texA, nullptr, &srvA);
        if (FAILED(hr)) { err = "创建 SRV A 失败"; return false; }
        hr = device->CreateTexture2D(&td, nullptr, &texB);
        if (FAILED(hr)) { err = "创建纹理 B 失败"; return false; }
        hr = device->CreateShaderResourceView(texB, nullptr, &srvB);
        if (FAILED(hr)) { err = "创建 SRV B 失败"; return false; }
        hr = device->CreateRenderTargetView(texB, nullptr, &rtvB);
        if (FAILED(hr)) { err = "创建 RTV B 失败"; return false; }
        hr = device->CreateTexture2D(&td, nullptr, &texC);
        if (FAILED(hr)) { err = "创建纹理 C 失败"; return false; }
        hr = device->CreateShaderResourceView(texC, nullptr, &srvC);
        if (FAILED(hr)) { err = "创建 SRV C 失败"; return false; }
        hr = device->CreateRenderTargetView(texC, nullptr, &rtvC);
        if (FAILED(hr)) { err = "创建 RTV C 失败"; return false; }

        // 1/4 分辨率模糊缓冲
        {
            const int lw = std::max(1, w / kDownscale);
            const int lh = std::max(1, h / kDownscale);
            D3D11_TEXTURE2D_DESC hd = td;
            hd.Width = lw;
            hd.Height = lh;
            hd.BindFlags = D3D11_BIND_SHADER_RESOURCE
                         | D3D11_BIND_RENDER_TARGET;
            hd.Usage = D3D11_USAGE_DEFAULT;
            hd.CPUAccessFlags = 0;
            if (FAILED(device->CreateTexture2D(&hd, nullptr, &texH1)) ||
                FAILED(device->CreateShaderResourceView(texH1, nullptr, &srvH1)) ||
                FAILED(device->CreateRenderTargetView(texH1, nullptr, &rtvH1)) ||
                FAILED(device->CreateTexture2D(&hd, nullptr, &texH2)) ||
                FAILED(device->CreateShaderResourceView(texH2, nullptr, &srvH2)) ||
                FAILED(device->CreateRenderTargetView(texH2, nullptr, &rtvH2))) {
                err = "创建低分辨率模糊缓冲失败";
                return false;
            }
            hw = lw;
            hh = lh;
        }

        // 读回用的 staging
        td.BindFlags = 0;
        td.Usage = D3D11_USAGE_STAGING;
        td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        hr = device->CreateTexture2D(&td, nullptr, &staging);
        if (FAILED(hr)) { err = "创建 staging 纹理失败"; return false; }

        texW = w; texH = h;
        stagingW = w; stagingH = h;
        return true;
    }

    // 解绑所有 SRV。**必须**在切换渲染目标前调用：
    // D3D11 不允许同一张纹理同时作为着色器输入和渲染目标，
    // 违反时写入会被静默丢弃（表现为"模糊没生效"，而平均亮度不变，
    // 所以纯数值对比发现不了 —— 只能看图）。
    void UnbindSRV() {
        ID3D11ShaderResourceView* none[2] = {nullptr, nullptr};
        ctx->PSSetShaderResources(0, 2, none);
    }

    void DrawFullscreen(ID3D11PixelShader* ps,
                        ID3D11ShaderResourceView* srv) {
        ctx->IASetInputLayout(nullptr);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vs, nullptr, 0);
        ctx->PSSetShader(ps, nullptr, 0);
        // 只绑 slot 0；slot 1 留给主合成（它要同时读模糊图与原图），
        // 其余 pass 绑上去反而可能与要写的 RT 冲突。
        ctx->PSSetShaderResources(0, 1, &srv);
        ctx->PSSetConstantBuffers(0, 1, &cb);
        ctx->Draw(3, 0);
    }

    void UpdateCB(const CBuffer& c) {
        D3D11_MAPPED_SUBRESOURCE ms{};
        if (SUCCEEDED(ctx->Map(cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &ms))) {
            std::memcpy(ms.pData, &c, sizeof(c));
            ctx->Unmap(cb, 0);
        }
    }
};

// ---------------------------------------------------------------- 构造

Renderer::Renderer() : impl_(new Impl()) {}

Renderer::~Renderer() {
    Shutdown();
    delete impl_;
    impl_ = nullptr;
}

void Renderer::Shutdown() {
    if (impl_) impl_->ReleaseAll();
    ready_ = false;
}

bool Renderer::Init(std::string& err) {
    Impl* d = impl_;
    d->ReleaseAll();

    // ---- 设备
    const D3D_FEATURE_LEVEL want[] = {
        D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
    D3D_FEATURE_LEVEL got = D3D_FEATURE_LEVEL_11_0;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   0, want, 4, D3D11_SDK_VERSION,
                                   &d->device, &got, &d->ctx);
    if (FAILED(hr)) {
        // 退回 WARP（软件渲染）：没有 GPU 时至少能跑，虽然慢
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0,
                               want, 4, D3D11_SDK_VERSION,
                               &d->device, &got, &d->ctx);
        if (FAILED(hr)) {
            err = "D3D11 设备创建失败（硬件与 WARP 都不行）";
            return false;
        }
    }

    // 记录显卡名
    IDXGIDevice* dxgiDev = nullptr;
    if (SUCCEEDED(d->device->QueryInterface(__uuidof(IDXGIDevice),
                                            (void**)&dxgiDev))) {
        IDXGIAdapter* ad = nullptr;
        if (SUCCEEDED(dxgiDev->GetAdapter(&ad))) {
            DXGI_ADAPTER_DESC desc{};
            if (SUCCEEDED(ad->GetDesc(&desc)))
                deviceName_ = util::WideToUtf8(desc.Description);
            ad->Release();
        }
        dxgiDev->Release();
    }

    // ---- 管线
    if (!d->BuildPipeline(err)) return false;

    // 常量缓冲
    D3D11_BUFFER_DESC bd{};
    bd.ByteWidth = sizeof(CBuffer);
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    hr = d->device->CreateBuffer(&bd, nullptr, &d->cb);
    if (FAILED(hr)) { err = "创建常量缓冲失败"; return false; }

    // 采样器：线性 + 钳制（越界采样必须钳制，否则边缘出现黑边）
    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    hr = d->device->CreateSamplerState(&sd, &d->sampler);
    if (FAILED(hr)) { err = "创建采样器失败"; return false; }

    // 光栅器：不做背面剔除（全屏三角形方向不固定）
    D3D11_RASTERIZER_DESC rd{};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    rd.DepthClipEnable = TRUE;
    hr = d->device->CreateRasterizerState(&rd, &d->raster);
    if (FAILED(hr)) { err = "创建光栅器状态失败"; return false; }

    // 不混合：我们自己输出 alpha，不需要管线混合
    D3D11_BLEND_DESC bld{};
    bld.RenderTarget[0].BlendEnable = FALSE;
    bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    hr = d->device->CreateBlendState(&bld, &d->blendOpaque);
    if (FAILED(hr)) { err = "创建混合状态失败"; return false; }

    // 固定管线状态
    d->ctx->IASetInputLayout(nullptr);
    d->ctx->RSSetState(d->raster);
    const float bf[4] = {0, 0, 0, 0};
    d->ctx->OMSetBlendState(d->blendOpaque, bf, 0xFFFFFFFF);
    d->ctx->PSSetSamplers(0, 1, &d->sampler);

    ready_ = true;
    return true;
}

// ---------------------------------------------------------------- 渲染

bool Renderer::Render(const Image& backdrop, const Image& wallpaper,
                      Material material, const Params& p,
                      int canvasW, int canvasH, float inset, float radius,
                      Image& out, std::string& err) {
    if (!ready_) { err = "渲染器未初始化"; return false; }
    if (canvasW <= 0 || canvasH <= 0) { err = "画布尺寸非法"; return false; }

    Impl* d = impl_;
    if (!d->EnsureCanvas(canvasW, canvasH, err)) return false;

    const double t0 = util::NowMs();

    const int panelW = canvasW - static_cast<int>(inset * 2);
    const int panelH = canvasH - static_cast<int>(inset * 2);
    if (panelW <= 0 || panelH <= 0) { err = "inset 过大"; return false; }

    // ---- 选源图：云母用壁纸，其余用窗口背后的画面
    const Image& srcImg =
        (material == Material::Mica && wallpaper.Valid()) ? wallpaper
                                                          : backdrop;
    if (!srcImg.Valid()) { err = "背景图为空"; return false; }

    // 背景平均亮度，作为亚克力亮度层的 pivot。
    // 声明必须在使用点之前 —— 之前放在 base lambda 前，编译报未声明。
    float lumPivot = 0.5f;

    // 上传源图（尺寸不符时用行拷贝让 GPU 线性缩放）
    {
        D3D11_MAPPED_SUBRESOURCE ms{};
        // 直接 UpdateSubresource 要求尺寸匹配，所以先把源缩放到画布尺寸。
        // 缩放在 CPU 做一次（372x204 很快），换取管线简单。
        std::vector<uint8_t> scaled(
            static_cast<size_t>(canvasW) * canvasH * 4);
        // 最近邻 + 线性混合的简易缩放
        for (int y = 0; y < canvasH; ++y) {
            const float sy = (y + 0.5f) * srcImg.height / canvasH - 0.5f;
            int y0 = static_cast<int>(std::floor(sy));
            const float fy = sy - y0;
            const int y1 = y0 + 1;
            y0 = std::max(0, std::min(srcImg.height - 1, y0));
            const int y1c = std::max(0, std::min(srcImg.height - 1, y1));
            for (int x = 0; x < canvasW; ++x) {
                const float sx = (x + 0.5f) * srcImg.width / canvasW - 0.5f;
                int x0 = static_cast<int>(std::floor(sx));
                const float fx = sx - x0;
                const int x1 = x0 + 1;
                x0 = std::max(0, std::min(srcImg.width - 1, x0));
                const int x1c = std::max(0, std::min(srcImg.width - 1, x1));

                const uint8_t* p00 = &srcImg.rgba[(y0 * srcImg.width + x0) * 4];
                const uint8_t* p10 = &srcImg.rgba[(y0 * srcImg.width + x1c) * 4];
                const uint8_t* p01 = &srcImg.rgba[(y1c * srcImg.width + x0) * 4];
                const uint8_t* p11 = &srcImg.rgba[(y1c * srcImg.width + x1c) * 4];
                uint8_t* dst = &scaled[(static_cast<size_t>(y) * canvasW + x) * 4];
                for (int c = 0; c < 3; ++c) {
                    const float top = p00[c] * (1 - fx) + p10[c] * fx;
                    const float bot = p01[c] * (1 - fx) + p11[c] * fx;
                    dst[c] = static_cast<uint8_t>(
                        std::max(0.0f, std::min(255.0f, top * (1 - fy) + bot * fy)));
                }
                dst[3] = 255;
            }
        }
        // 背景平均亮度 -> 亮度层的 pivot。
        // 用 CPU 已缩放到画布尺寸的这份数据算，不需要额外 pass。
        {
            const size_t n = static_cast<size_t>(canvasW) * canvasH;
            double sum = 0.0;
            for (size_t i = 0; i < n; ++i) {
                // 不要叫 p —— 会遮蔽函数参数 p（C4457）
                const uint8_t* px = &scaled[i * 4];
                sum += 0.2126 * px[0] + 0.7152 * px[1] + 0.0722 * px[2];
            }
            lumPivot = static_cast<float>(
                sum / static_cast<double>(std::max<size_t>(n, 1)) / 255.0);
        }

        d->ctx->UpdateSubresource(d->texA, 0, nullptr, scaled.data(),
                                  static_cast<UINT>(canvasW * 4), 0);
    }

    const float panelCx = canvasW * 0.5f;
    const float panelCy = canvasH * 0.5f;
    const float halfW = panelW * 0.5f;
    const float halfH = panelH * 0.5f;

    auto base = [&]() {
        CBuffer c{};
        c.size[0] = static_cast<float>(canvasW);
        c.size[1] = static_cast<float>(canvasH);
        c.size[2] = panelCx;
        c.size[3] = panelCy;
        c.shape[0] = halfW;
        c.shape[1] = halfH;
        c.shape[2] = radius;
        c.shape[3] = 0.0f;
        c.liquid[0] = p.refraction;
        c.liquid[1] = p.dispersion;
        c.liquid[2] = p.splay;
        c.liquid[3] = p.depth;
        c.light[0] = p.frost;
        c.light[1] = p.lightAngle;
        c.light[2] = p.lightIntensity;
        c.light[3] = p.grain;
        // 表面色必须来自参数，不能留 0（留 0 = 混向黑色）
        c.surface[0] = p.surfaceR / 255.0f;
        c.surface[1] = p.surfaceG / 255.0f;
        c.surface[2] = p.surfaceB / 255.0f;
        c.surface[3] = p.opacity;
        c.misc[0] = (material == Material::Liquid) ? 0.0f
                  : (material == Material::Acrylic) ? 1.0f : 2.0f;
        c.misc[1] = p.dark;
        c.misc[2] = p.saturate;
        c.misc[3] = p.blur;
        c.extra[0] = lumPivot;      // 亮度层基准
        c.extra[1] = p.contrast;    // 亮度层对比强度
        c.shadow[0] = p.shadowSigma;
        c.shadow[1] = p.shadowStrength;
        c.shadow[2] = p.shadowOffsetY;
        c.shadow[3] = 1.25f;        // 圆角抗锯齿宽度
        return c;
    };

    D3D11_VIEWPORT vp{};
    vp.Width = static_cast<float>(canvasW);
    vp.Height = static_cast<float>(canvasH);
    vp.MaxDepth = 1.0f;
    d->ctx->RSSetViewports(1, &vp);

    // ---- Pass 1：blit（顺便做云母的降饱和）
    {
        CBuffer c = base();
        d->UpdateCB(c);
        d->UnbindSRV();
        ID3D11RenderTargetView* rtv = d->rtvB;
        d->ctx->OMSetRenderTargets(1, &rtv, nullptr);
        d->DrawFullscreen(d->psBlit, d->srvA);
    }

    // ---- Pass 2/3：可分离模糊
    const float blurRadius =
        (material == Material::Mica) ? p.blur
      : (material == Material::Liquid) ? (2.0f + p.frost * 26.0f)
                                       : p.blur;
    bool blurred = false;
    if (blurRadius > 0.3f) {
        // 自适应降采样倍率。
        //
        // 固定用 1/4 是错的：云母 sigma=2 时，1/4 的盒式降采样一次就跨越
        // 整整一个条纹周期（8px 条纹、周期 16px、盒宽 16px），细节在模糊
        // 开始前就被盒滤波抹掉了（实测残留振幅仅 6.6%，而画面看条纹还在，
        // 说明是盒滤波而非高斯在做"模糊"）。
        // 同时低分辨率下 sigma 只有 0.5 纹素，离散核会跨越整个低分辨率
        // 条纹周期，把反相位的像素平均掉，等效模糊远大于预期。
        //
        // 原则：只有 sigma 足够大时才降采样，并保证低分辨率 sigma >= 2，
        // 这样高斯核在低分辨率上仍被充分采样。
        const int maxFactor = Impl::kDownscale;
        int factor = 1;
        for (int f = maxFactor; f >= 2; f /= 2) {
            if (blurRadius / static_cast<float>(f) >= 2.0f) {
                factor = f;
                break;
            }
        }

        if (factor == 1) {
            // ---- 全分辨率两趟可分离模糊：B -> C -> B
            {
                CBuffer c = base();
                c.shape[3] = 0.0f;              // 横向
                c.misc[3] = blurRadius;
                d->UpdateCB(c);
                d->UnbindSRV();
                ID3D11RenderTargetView* rtv = d->rtvC;
                d->ctx->OMSetRenderTargets(1, &rtv, nullptr);
                d->DrawFullscreen(d->psBlur, d->srvB);
            }
            {
                CBuffer c = base();
                c.shape[3] = 1.0f;              // 纵向
                c.misc[3] = blurRadius;
                d->UpdateCB(c);
                // 这一趟要写 rtvB，而输入 srvB 还绑着；D3D11 不允许同一张
                // 纹理同时作输入与渲染目标，不解绑写入会被静默丢弃。
                d->UnbindSRV();
                ID3D11RenderTargetView* rtv = d->rtvB;
                d->ctx->OMSetRenderTargets(1, &rtv, nullptr);
                d->DrawFullscreen(d->psBlur, d->srvC);
            }
            blurred = true;
        } else {
            // ---- 降采样路径：盒式限带 -> 低分辨率两趟模糊 -> 双线性放大
            const float lw = static_cast<float>(d->hw);
            const float lh = static_cast<float>(d->hh);
            const float loRadius = blurRadius / static_cast<float>(factor);

            D3D11_VIEWPORT lvp{};
            lvp.Width = lw;
            lvp.Height = lh;
            lvp.MaxDepth = 1.0f;

            // (0) 盒式降采样：srvB（全尺寸）-> rtvH1（1/factor）
            {
                CBuffer c = base();
                c.size[0] = lw;             // 目标尺寸
                c.size[1] = lh;
                c.size[2] = vp.Width;       // 源尺寸
                c.size[3] = vp.Height;
                c.misc[3] = static_cast<float>(factor);
                d->UpdateCB(c);
                d->UnbindSRV();
                ID3D11RenderTargetView* rtv = d->rtvH1;
                d->ctx->OMSetRenderTargets(1, &rtv, nullptr);
                d->ctx->RSSetViewports(1, &lvp);
                d->DrawFullscreen(d->psDown, d->srvB);
            }
            // (1) 横向：srvH1 -> rtvH2
            {
                CBuffer c = base();
                c.size[0] = lw;
                c.size[1] = lh;
                c.shape[3] = 0.0f;          // 横向
                c.misc[3] = loRadius;
                d->UpdateCB(c);
                d->UnbindSRV();
                ID3D11RenderTargetView* rtv = d->rtvH2;
                d->ctx->OMSetRenderTargets(1, &rtv, nullptr);
                d->DrawFullscreen(d->psBlur, d->srvH1);
            }
            // (2) 纵向：srvH2 -> rtvH1
            {
                CBuffer c = base();
                c.size[0] = lw;
                c.size[1] = lh;
                c.shape[3] = 1.0f;          // 纵向
                c.misc[3] = loRadius;
                d->UpdateCB(c);
                d->UnbindSRV();
                ID3D11RenderTargetView* rtv = d->rtvH1;
                d->ctx->OMSetRenderTargets(1, &rtv, nullptr);
                d->DrawFullscreen(d->psBlur, d->srvH2);
            }
            // 恢复全尺寸视口
            d->ctx->RSSetViewports(1, &vp);

            // (3) 双线性放大回全尺寸 B
            {
                CBuffer c = base();
                c.shape[3] = 0.0f;
                c.misc[3] = 0.0f;           // 0 = 直接拷贝（双线性放大）
                d->UpdateCB(c);
                d->UnbindSRV();
                ID3D11RenderTargetView* rtv = d->rtvB;
                d->ctx->OMSetRenderTargets(1, &rtv, nullptr);
                d->DrawFullscreen(d->psBlur, d->srvH1);
            }
            blurred = true;
        }
    }

    // ---- Pass 4：主合成 -> C
    {
        CBuffer c = base();
        c.shape[3] = 0.0f;
        d->UpdateCB(c);
        d->UnbindSRV();
        ID3D11RenderTargetView* rtv = d->rtvC;
        d->ctx->OMSetRenderTargets(1, &rtv, nullptr);

        // **必须清空**：主着色器对面板外侧之外的像素 `discard`，
        // 而 discard 不会写任何东西 —— 那些纹素会保留**上一帧**的内容。
        // 之前从未 ClearRenderTargetView，后果实测可见：
        //   * 画布角落读到 200（应为背景白 245），是上一帧残留
        //   * 阴影剖面是平的而不是高斯衰减，因为混进了旧像素
        //   * 每帧阴影逐渐累积变黑 —— 也就是用户说的"阴影太重"
        // 清成全透明（预乘 alpha 下 RGBA 都为 0）即可。
        const float kClear[4] = {0.0f, 0.0f, 0.0f, 0.0f};
        d->ctx->ClearRenderTargetView(rtv, kClear);

        // 没做模糊时（半径过小）直接读原图 A，否则读模糊结果 B
        ID3D11ShaderResourceView* srvs[2] = {
            blurred ? d->srvB : d->srvA, d->srvA};
        d->ctx->PSSetShaderResources(0, 2, srvs);
        d->ctx->IASetInputLayout(nullptr);
        d->ctx->IASetPrimitiveTopology(
            D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        d->ctx->VSSetShader(d->vs, nullptr, 0);
        d->ctx->PSSetShader(d->psMain, nullptr, 0);
        d->ctx->PSSetConstantBuffers(0, 1, &d->cb);
        d->ctx->Draw(3, 0);
    }

    // ---- 读回
    d->UnbindSRV();     // 确保 texC 不再作为输入，CopyResource 才能拿到结果
    d->ctx->OMSetRenderTargets(0, nullptr, nullptr);
    d->ctx->CopyResource(d->staging, d->texC);
    D3D11_MAPPED_SUBRESOURCE ms{};
    HRESULT hr = d->ctx->Map(d->staging, 0, D3D11_MAP_READ, 0, &ms);
    if (FAILED(hr)) { err = "读回纹理失败"; return false; }

    out.Resize(canvasW, canvasH);
    for (int y = 0; y < canvasH; ++y) {
        const uint8_t* src =
            static_cast<const uint8_t*>(ms.pData) + y * ms.RowPitch;
        std::memcpy(&out.rgba[static_cast<size_t>(y) * canvasW * 4],
                    src, static_cast<size_t>(canvasW) * 4);
    }
    d->ctx->Unmap(d->staging, 0);

    lastMs_ = util::NowMs() - t0;
    return true;
}

double Renderer::BenchmarkMs(const Image& backdrop, const Image& wallpaper,
                             Material material, const Params& p,
                             int canvasW, int canvasH, float inset,
                             float radius, int iterations) {
    if (!ready_ || iterations <= 0) return -1.0;
    Image out;
    std::string err;
    // 预热（首次包含纹理创建与着色器编译）
    Render(backdrop, wallpaper, material, p, canvasW, canvasH, inset, radius,
           out, err);
    const double t0 = util::NowMs();
    for (int i = 0; i < iterations; ++i) {
        if (!Render(backdrop, wallpaper, material, p, canvasW, canvasH,
                    inset, radius, out, err)) {
            return -1.0;
        }
    }
    return (util::NowMs() - t0) / iterations;
}

// ---------------------------------------------------------------- 工具

void SwapRedBlue(Image& img) {
    uint8_t* p = img.rgba.data();
    const size_t n = img.rgba.size();
    for (size_t i = 0; i + 2 < n; i += 4) {
        const uint8_t r = p[i + 0];
        p[i + 0] = p[i + 2];
        p[i + 2] = r;
    }
}

Image SolidImage(int w, int h, uint8_t r, uint8_t g, uint8_t b, uint8_t a) {
    Image img;
    img.Resize(w, h);
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            uint8_t* p = &img.rgba[(static_cast<size_t>(y) * w + x) * 4];
            p[0] = r; p[1] = g; p[2] = b; p[3] = a;
        }
    }
    return img;
}

// ---------------------------------------------------------------- 自检

namespace {

// 生成测试用条纹背景（对比强，任何位移/模糊都能测出来）
Image MakeStripes(int w, int h, int period = 8) {
    Image img;
    img.Resize(w, h);
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            const bool on = ((x / period) % 2) == 0;
            uint8_t* p = &img.rgba[(static_cast<size_t>(y) * w + x) * 4];
            p[0] = on ? 235 : 20;
            p[1] = on ? 90 : 40;
            p[2] = on ? 60 : 200;
            p[3] = 255;
        }
    }
    return img;
}

// 灰度条纹：三通道相等，任何通道差异只可能来自色散
Image MakeGrayStripes(int w, int h, int period = 7) {
    Image img;
    img.Resize(w, h);
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            const uint8_t v = ((x / period) % 2) == 0 ? 230 : 30;
            uint8_t* p = &img.rgba[(static_cast<size_t>(y) * w + x) * 4];
            p[0] = p[1] = p[2] = v;
            p[3] = 255;
        }
    }
    return img;
}

double MeanAbsDiff(const Image& a, const Image& b) {
    if (a.width != b.width || a.height != b.height) return -1.0;
    double sum = 0;
    size_t n = 0;
    for (size_t i = 0; i < a.rgba.size(); i += 4) {
        for (int c = 0; c < 3; ++c) {
            sum += std::abs(static_cast<int>(a.rgba[i + c]) -
                            static_cast<int>(b.rgba[i + c]));
            ++n;
        }
    }
    return n ? sum / n : 0.0;
}

// 边缘带的平均通道差（R 与 B），用于测色散
double EdgeChannelSpread(const Image& img, int x0, int x1) {
    double sum = 0;
    size_t n = 0;
    for (int y = 0; y < img.height; ++y) {
        for (int x = x0; x < x1 && x < img.width; ++x) {
            const uint8_t* p =
                &img.rgba[(static_cast<size_t>(y) * img.width + x) * 4];
            sum += std::abs(static_cast<int>(p[0]) - static_cast<int>(p[2]));
            ++n;
        }
    }
    return n ? sum / n : 0.0;
}

// 区域内亮度均值（只统计覆盖率 > 0.5 的像素）
double LumaInPanel(const Image& img, int inset) {
    double sum = 0;
    size_t n = 0;
    for (int y = inset; y < img.height - inset; ++y) {
        for (int x = inset; x < img.width - inset; ++x) {
            const uint8_t* p =
                &img.rgba[(static_cast<size_t>(y) * img.width + x) * 4];
            if (p[3] < 128) continue;
            sum += 0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2];
            ++n;
        }
    }
    return n ? sum / n : 0;
}

}  // namespace

int RunSelfTest() {
    util::Print(u8"\n=== GPU 材质渲染自检 ===\n");

    Renderer r;
    std::string err;
    if (!r.Init(err)) {
        util::Print(u8"  ✗ 初始化失败: %s\n", err.c_str());
        return 1;
    }
    util::Print(u8"  设备: %s\n", r.deviceName().c_str());

    int fails = 0;
    auto check = [&](bool ok, const char* what) {
        util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s\n", what);
        if (!ok) ++fails;
    };

    const int W = 340, H = 172, INSET = 16, RAD = 8;
    const int CW = W + INSET * 2, CH = H + INSET * 2;

    const Image stripes = MakeStripes(CW, CH);
    const Image gray = MakeGrayStripes(CW, CH);

    // 基参：液态玻璃。渲染每种材质前会套用该材质自己的默认值。
    Params p;
    p.ApplyMaterialDefaults(Material::Liquid);

    // ---- 1) 三种材质都能渲染出内容（不是全透明、也不是全空）
    util::Print(u8"\n[1] 三种材质渲染\n");
    Image liquid, acrylic, mica;
    for (int i = 0; i < 3; ++i) {
        const Material m = static_cast<Material>(i);
        Params pm = p;
        pm.ApplyMaterialDefaults(m);   // 每种材质用各自的默认参数
        Image out;
        if (!r.Render(stripes, stripes, m, pm, CW, CH, INSET, RAD, out,
                      err)) {
            util::Print(u8"  ✗ %s 渲染失败: %s\n", MaterialName(m),
                        err.c_str());
            ++fails;
            continue;
        }
        if (m == Material::Liquid) liquid = out;
        else if (m == Material::Acrylic) acrylic = out;
        else mica = out;

        const double luma = LumaInPanel(out, INSET);
        util::Print(u8"  %s: 面板内平均亮度 %.1f\n", MaterialName(m), luma);
        if (luma < 1.0) {
            util::Print(u8"  ✗ %s 渲染出空白\n", MaterialName(m));
            ++fails;
        }
    }
    check(liquid.Valid() && acrylic.Valid() && mica.Valid(),
          u8"三种材质均产出有效图像");

    // ---- 2) 三种材质结果必须彼此不同（不是同一张图）
    util::Print(u8"\n[2] 三材质差异（应彼此明显不同）\n");
    const struct { const char* a; const char* b; const Image* ia;
                   const Image* ib; } pairs[] = {
        {"液态/亚克力", "", &liquid, &acrylic},
        {"液态/云母", "", &liquid, &mica},
        {"亚克力/云母", "", &acrylic, &mica},
    };
    for (const auto& pr : pairs) {
        if (!pr.ia->Valid() || !pr.ib->Valid()) continue;
        const double d = MeanAbsDiff(*pr.ia, *pr.ib);
        util::Print(u8"  %s: 平均绝对差 %.2f\n", pr.a, d);
        if (d <= 3.0) {
            util::Print(u8"  ✗ %s 差异过小，可能是同一种渲染\n", pr.a);
            ++fails;
        }
    }
    check(true, u8"三材质差异检查完成");

    // ---- 3) 折射必须真的生效
    // 只改 refraction，其余参数不动，看边缘是否变化。
    // 这是 Python 版踩过的坑：画布没有为面板外侧留边距时，
    // 向外采样全部越界被钳制，折射静默失效（实测变化 0.00）。
    util::Print(u8"\n[3] 折射隔离测试（只改 refraction）\n");
    {
        // frost 必须关掉：折射是"把面板外侧的画面挤进来"，如果先把背景
        // 重度模糊掉，条纹本身就没了，位移再大也测不出差异
        // （实测 frost=0.35 时模糊半径约 11px，16px 周期的条纹被抹平，
        //   边缘变化只有 4.35，看着像"折射无效"，其实是测法把变量混了）。
        Params p0 = p; p0.refraction = 0.0f; p0.frost = 0.0f;
        Params p1 = p; p1.refraction = 1.0f; p1.frost = 0.0f;
        Image a, b;
        r.Render(stripes, stripes, Material::Liquid, p0, CW, CH, INSET, RAD,
                 a, err);
        r.Render(stripes, stripes, Material::Liquid, p1, CW, CH, INSET, RAD,
                 b, err);
        // 边缘带 vs 中央：折射应只作用于边缘
        double edgeSum = 0, midSum = 0;
        size_t en = 0, mn = 0;
        for (int y = 0; y < CH; ++y) {
            for (int x = 0; x < CW; ++x) {
                const size_t i = (static_cast<size_t>(y) * CW + x) * 4;
                const double d = (std::abs(a.rgba[i] - b.rgba[i])
                                + std::abs(a.rgba[i + 1] - b.rgba[i + 1])
                                + std::abs(a.rgba[i + 2] - b.rgba[i + 2])) / 3.0;
                if (x >= INSET && x < INSET + 16) { edgeSum += d; ++en; }
                else if (x > CW / 2 - 8 && x < CW / 2 + 8) { midSum += d; ++mn; }
            }
        }
        const double dEdge = en ? edgeSum / en : 0;
        const double dMid = mn ? midSum / mn : 0;
        util::Print(u8"  左边缘带变化 %.2f   中央变化 %.2f\n", dEdge, dMid);
        if (dEdge < 5.0) {
            util::Print(u8"  ✗ 折射无效（边缘仅变化 %.2f）—— 检查画布是否"
                        u8"为面板外侧留了采样边距\n", dEdge);
            ++fails;
        } else if (dMid > 1.0) {
            util::Print(u8"  ✗ 折射影响到了中央区域 (%.2f)，不符合玻璃行为\n",
                        dMid);
            ++fails;
        } else {
            util::Print(u8"  ✓ 折射只作用在边缘\n");
        }
    }

    // ---- 4) 色散：灰度背景下，通道差异必须随色散增大
    util::Print(u8"\n[4] 色散隔离测试（灰度背景）\n");
    {
        Params p0 = p; p0.dispersion = 0.0f; p0.frost = 0.0f;
        Params p1 = p; p1.dispersion = 1.0f; p1.frost = 0.0f;
        Image a, b;
        r.Render(gray, gray, Material::Liquid, p0, CW, CH, INSET, RAD, a, err);
        r.Render(gray, gray, Material::Liquid, p1, CW, CH, INSET, RAD, b, err);
        const double s0 = EdgeChannelSpread(a, INSET, INSET + 16);
        const double s1 = EdgeChannelSpread(b, INSET, INSET + 16);
        util::Print(u8"  关闭色散 通道差 %.3f   开启 %.3f   放大 %.1f 倍\n",
                    s0, s1, s1 / (s0 > 1e-6 ? s0 : 1e-6));
        if (s1 < s0 * 1.5) {
            util::Print(u8"  ✗ 色散不可测\n");
            ++fails;
        } else {
            util::Print(u8"  ✓ 色散生效\n");
        }
    }

    // ---- 5) 云母必须取壁纸，而不是窗口后方
    util::Print(u8"\n[5] 云母取壁纸验证\n");
    {
        const Image redWall = SolidImage(CW, CH, 200, 40, 40);
        const Image blueBack = SolidImage(CW, CH, 40, 40, 200);
        Params pm = p;
        pm.ApplyMaterialDefaults(Material::Mica);
        Image out;
        r.Render(blueBack, redWall, Material::Mica, pm, CW, CH, INSET, RAD,
                 out, err);
        // 取面板内部一点的色相
        const size_t i =
            (static_cast<size_t>(CH / 2) * CW + CW / 2) * 4;
        util::Print(u8"  红壁纸+蓝背景 -> 面板内 RGB(%d,%d,%d)\n",
                    out.rgba[i], out.rgba[i + 1], out.rgba[i + 2]);
        if (out.rgba[i] <= out.rgba[i + 2]) {
            util::Print(u8"  ✗ 云母没有取壁纸色调（应偏红）\n");
            ++fails;
        } else {
            util::Print(u8"  ✓ 云母取的是壁纸而非窗口后方\n");
        }
    }

    // ---- 5.5) 模糊有效性
    //
    // 这一项**必须**有：模糊失效时平均亮度几乎不变，[1] 的亮度检查和
    // test_gpu_vs_python 的亮度比都会照样通过。实测因此漏过两次真 bug：
    //   1) sigma 写成 radius*0.5 且 9 抽头只覆盖 ±1σ -> 模糊太弱
    //   2) 抽头间隔 0.625σ，大 sigma 下超过条纹周期 -> 同相位采样，等于没模糊
    //
    // 判据用**条纹幅度**（每列求平均后取列间标准差），因为它直接测
    // "条纹信号还在不在"，不受面板内部竖直渐变影响。
    //
    // 但**不能**拿它和原图做绝对比值：云母 opacity=0.86，只有 14% 的背景
    // 透出来，其残留幅度天然被压到 ~10%（高斯 σ=2 保留 73.5%，
    // 0.735×0.14 ≈ 10.3%，与实测 9.4% 吻合）。所以这里改为和**其它材质
    // 相对比较** —— 三种材质共用同一张背景，只有云母几乎不模糊，
    // 因此云母的残留幅度必须最高。这个判据与 opacity 无关。
    util::Print(u8"\n[5.5] 模糊有效性（条纹幅度：越接近 0 越模糊）\n");
    {
        auto stripeAmplitude = [](const Image& img, int inset) {
            if (img.width <= inset * 2 + 4) return 0.0;
            std::vector<double> colMean;
            for (int x = inset + 2; x < img.width - inset - 2; ++x) {
                double sum = 0;
                int cnt = 0;
                for (int y = inset + 2; y < img.height - inset - 2; ++y) {
                    const size_t k =
                        (static_cast<size_t>(y) * img.width + x) * 4;
                    sum += 0.299 * img.rgba[k] + 0.587 * img.rgba[k + 1]
                         + 0.114 * img.rgba[k + 2];
                    ++cnt;
                }
                if (cnt) colMean.push_back(sum / cnt);
            }
            if (colMean.size() < 2) return 0.0;
            double mean = 0;
            for (double v : colMean) mean += v;
            mean /= colMean.size();
            double var = 0;
            for (double v : colMean) var += (v - mean) * (v - mean);
            var /= colMean.size();
            return var > 0 ? std::sqrt(var) : 0.0;
        };

        const double srcAmp = stripeAmplitude(stripes, 0);
        util::Print(u8"  原始条纹幅度 %.2f（作参照，不直接比较绝对比值）\n",
                    srcAmp);

        double ampAcrylic = 0, ampLiquid = 0, ampMica = 0;
        bool okA = false, okL = false, okM = false;

        auto runOne = [&](Material mat, const char* name, double& amp,
                          bool& ok) {
            Params pc = p;
            pc.ApplyMaterialDefaults(mat);
            Image out;
            if (!r.Render(stripes, stripes, mat, pc, CW, CH,
                          static_cast<float>(INSET), static_cast<float>(RAD),
                          out, err)) {
                util::Print(u8"  ✗ %s 渲染失败\n", name);
                ++fails;
                return;
            }
            amp = stripeAmplitude(out, INSET);
            const double ratio = srcAmp > 1e-6 ? amp / srcAmp : 0.0;
            util::Print(u8"  %s  幅度 %.2f  占原图 %.1f%%\n", name, amp,
                        ratio * 100);
            ok = true;
        };

        runOne(Material::Acrylic, u8"亚克力  ", ampAcrylic, okA);
        runOne(Material::Liquid, u8"液态玻璃", ampLiquid, okL);
        runOne(Material::Mica, u8"云母    ", ampMica, okM);

        if (okA && ampAcrylic > srcAmp * 0.20) {
            util::Print(u8"    ✗ 亚克力模糊不足（应低于原图 20%%）—— 检查"
                        u8"模糊抽头间隔是否超过条纹周期\n");
            ++fails;
        } else if (okA) {
            util::Print(u8"    ✓ 亚克力模糊充分\n");
        }

        if (okL && ampLiquid > srcAmp * 0.20) {
            util::Print(u8"    ✗ 液态玻璃模糊不足\n");
            ++fails;
        } else if (okL) {
            util::Print(u8"    ✓ 液态玻璃模糊充分\n");
        }

        // 云母：必须比另外两种都更清晰（它几乎不模糊）
        if (okM && okA && okL) {
            if (ampMica > ampAcrylic && ampMica > ampLiquid) {
                util::Print(u8"    ✓ 云母保留最多细节（比亚克力/液态玻璃清晰，"
                            u8"符合预期）\n");
            } else {
                util::Print(u8"    ✗ 云母比亚克力(%.2f)或液态玻璃(%.2f)还模糊"
                            u8"(%.2f)，不符合预期\n", ampAcrylic, ampLiquid,
                            ampMica);
                ++fails;
            }
        }
    }

    // ---- 6) 性能
    util::Print(u8"\n[6] 性能（%dx%d 画布）\n", CW, CH);
    for (int i = 0; i < 3; ++i) {
        const Material m = static_cast<Material>(i);
        Params pm = p;
        pm.ApplyMaterialDefaults(m);
        const double ms = r.BenchmarkMs(stripes, stripes, m, pm, CW, CH,
                                        INSET, RAD, 50);
        util::Print(u8"  %s: %.3f ms/帧  (约 %.0f fps)\n", MaterialName(m),
                    ms, ms > 0 ? 1000.0 / ms : 0);
        if (ms > 8.0) {
            util::Print(u8"  ✗ %s 超过 8ms 预算\n", MaterialName(m));
            ++fails;
        }
    }

    util::Print(u8"\n");
    if (fails) {
        util::Print(u8"GPU 自检失败 %d 项\n", fails);
        return 1;
    }
    util::Print(u8"GPU 材质渲染自检全部通过 ✓\n");
    return 0;
}

// ---------------------------------------------------------------- 结果导出

namespace {

// 写 32 位 BMP（BGRA，自下而上）。选 BMP 是因为无需任何第三方库，
// 而 PIL 能直接读，方便和 Python 版做对比。
bool WriteBmp32(const std::string& pathUtf8, const Image& img) {
    const std::wstring wpath = util::Utf8ToWide(pathUtf8);
    HANDLE f = CreateFileW(wpath.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;

    const uint32_t rowBytes = static_cast<uint32_t>(img.width) * 4;
    const uint32_t pixelBytes = rowBytes * static_cast<uint32_t>(img.height);
    const uint32_t fileSize = 14 + 40 + pixelBytes;

    uint8_t header[54] = {0};
    // BITMAPFILEHEADER
    header[0] = 'B'; header[1] = 'M';
    std::memcpy(&header[2], &fileSize, 4);
    const uint32_t offset = 54;
    std::memcpy(&header[10], &offset, 4);
    // BITMAPINFOHEADER
    const uint32_t hdrSize = 40;
    std::memcpy(&header[14], &hdrSize, 4);
    const int32_t w = img.width, h = img.height;
    std::memcpy(&header[18], &w, 4);
    std::memcpy(&header[22], &h, 4);          // 正高度 = 自下而上
    const uint16_t planes = 1, bpp = 32;
    std::memcpy(&header[26], &planes, 2);
    std::memcpy(&header[28], &bpp, 2);
    std::memcpy(&header[34], &pixelBytes, 4);

    DWORD written = 0;
    bool ok = WriteFile(f, header, 54, &written, nullptr) && written == 54;

    // 自下而上写
    std::vector<uint8_t> row(rowBytes);
    for (int y = img.height - 1; y >= 0 && ok; --y) {
        for (int x = 0; x < img.width; ++x) {
            const uint8_t* p =
                &img.rgba[(static_cast<size_t>(y) * img.width + x) * 4];
            uint8_t* d = &row[static_cast<size_t>(x) * 4];
            d[0] = p[2];    // B
            d[1] = p[1];    // G
            d[2] = p[0];    // R
            d[3] = p[3];    // A
        }
        ok = WriteFile(f, row.data(), rowBytes, &written, nullptr)
             && written == rowBytes;
    }
    CloseHandle(f);
    return ok;
}

Material ParseMaterial(const std::string& s) {
    if (s == "liquid" || s == u8"液态玻璃") return Material::Liquid;
    if (s == "mica" || s == u8"云母") return Material::Mica;
    return Material::Acrylic;
}

}  // namespace

bool DumpMaterial(const std::string& material, const std::string& outPath,
                  const std::string& err) {
    (void)err;
    Renderer r;
    std::string e;
    if (!r.Init(e)) {
        util::Print(u8"初始化失败: %s\n", e.c_str());
        return false;
    }

    const int W = 340, H = 172, INSET = 16, RAD = 8;
    const int CW = W + INSET * 2, CH = H + INSET * 2;

    // 与 Python 对比脚本用的同一张条纹背景
    Image stripes;
    stripes.Resize(CW, CH);
    for (int y = 0; y < CH; ++y) {
        for (int x = 0; x < CW; ++x) {
            const bool on = ((x / 8) % 2) == 0;
            uint8_t* p = &stripes.rgba[
                (static_cast<size_t>(y) * CW + x) * 4];
            p[0] = on ? 235 : 20;
            p[1] = on ? 90 : 40;
            p[2] = on ? 60 : 200;
            p[3] = 255;
        }
    }

    const Material mat = ParseMaterial(material);
    Params p;
    p.ApplyMaterialDefaults(mat);      // 必须套用，否则 opacity 等用的是别家的值
    Image out;
    if (!r.Render(stripes, stripes, mat, p, CW, CH,
                  INSET, RAD, out, e)) {
        util::Print(u8"渲染失败: %s\n", e.c_str());
        return false;
    }
    if (!WriteBmp32(outPath, out)) {
        util::Print(u8"写文件失败: %s\n", outPath.c_str());
        return false;
    }
    util::Print(u8"已写出 %s (%dx%d) 耗时 %.2f ms\n", outPath.c_str(),
                out.width, out.height, r.lastMs());
    return true;
}

}  // namespace gpu
