// ============================================================================
//  hidpp.h — 罗技 HID++ 2.0 协议层（读取无线鼠标电量）
//
//  这是从已验证的 Python 版 (mouse_battery_core.py) 移植而来，
//  协议细节与踩过的坑全部保留，注释中标注了原因，改动前请先读。
// ============================================================================
#pragma once

#include <cstdint>
#include <string>
#include <vector>
#include <optional>
namespace hidpp {

// ---------------------------------------------------------------- 协议常量

constexpr uint16_t kLogitechVendor  = 0x046D;
constexpr uint16_t kVendorUsagePage = 0xFF00;
constexpr uint8_t  kHidppLong       = 0x11;

constexpr uint16_t kFeatureRoot             = 0x0000;
constexpr uint16_t kFeatureDeviceName       = 0x0005;
constexpr uint16_t kFeatureBatteryStatus    = 0x1000;
constexpr uint16_t kFeatureBatteryVoltage   = 0x1001;
constexpr uint16_t kFeatureUnifiedBattery   = 0x1004;
constexpr uint16_t kFeatureAdcMeasurement   = 0x1F20;
constexpr uint16_t kFeatureCenturionSoc     = 0x0104;

// function **序号**（不是内核写法里那个已左移的字节值）
constexpr uint8_t kFnGetFeature             = 0x0;
constexpr uint8_t kFnGetCapabilities         = 0x0;
constexpr uint8_t kFnGetStatus               = 0x1;
constexpr uint8_t kFnBatteryStatusGetStatus  = 0x0;
constexpr uint8_t kFnNameLength      = 0x0;
constexpr uint8_t kFnNameChunk       = 0x1;

// 软件 ID 必须非零。
// HID++ 2.0 的 function 字节 = (function << 4) | 软件ID；
// 设备主动推送的通知用软件 ID 0，与 function 0 的应答**字节完全相同**。
// 取非零值并要求 function 字节严格匹配，才能保证读到的是真应答。
constexpr uint8_t kClientId = 0x0A;

// 单次读电量要串起 4~6 次 HID 往返。若每个环节各自计时，
// 一次"读电量"最坏能阻塞数秒（实测 Python 版曾达 4.7 秒）。
// 因此全部调用共享一个总体 deadline。
constexpr double kDefaultBudgetSec = 1.5;

// 探测超时：不存在的配对槽位会一直等到超时（实测约 300ms）；
// 在线设备中位应答约 57ms，但休眠唤醒时首帧可能慢到约 850ms。
constexpr double kProbeTimeoutSec     = 0.12;
constexpr double kPreferredProbeSec   = 0.60;

// ---------------------------------------------------------------- 数据结构

struct Reading {
    std::string name;             // 设备名，如 "PRO X Wireless"
    int         percent = 0;      // 电量百分比
    std::string level;            // 档位文字（满/良好/偏低/极低），可能为空
    int         chargingState = 0xFF;
    std::string chargingText;     // 充电状态文字
    int         externalPower = -1;
    int         deviceIndex = 0;
    int         voltageMv = 0;    // 0 表示未读到
    uint16_t    sourceFeature = 0;// 读数来自哪个电量特性（便于诊断）
    bool        percentInferred = false;  // 百分比由档位/电压推算而非设备直报
};

// 充电状态（对应内核 hidpp20_unifiedbattery_map_status）
//
// 实现已移到 battery.cpp 的纯解析层（那里可脱机单测），这里保留同名包装，
// 避免调用点散落两份文字表而悄悄分叉。
std::string ChargingText(int state);

// BatteryStatus(0x1000) 状态码（与 UnifiedBattery 的状态码不同）。
// 转成统一内部状态，便于历史与通知共用一套判断。
int NormalizeBatteryStatus(int status);

std::string BatteryStatusText(int status);

bool IsChargingState(int state);

// 电量档位位掩码（内核 FLAG_UNIFIED_BATTERY_LEVEL_*）
std::string LevelFromFlags(int bits);

// ---------------------------------------------------------------- 接口

// 读一次当前所有在线罗技鼠标的电量。
// budgetSec 是**整个函数**的时间预算，不是每次 HID 往返的预算。
// 鼠标休眠时返回空列表（这是正常状态，不是错误）。
//
// scanAllDevices = false（默认）时，每个接收器找到第一台设备就停止 ——
// 这是应用轮询走的路径。原因：应用以 `--once` **每轮新起一个进程**，
// 槽位发现缓存无法跨轮次保留，因此继续探测其余槽位只会在"只有一个鼠标"
// 的常见情形下白白多花约 0.7 秒（每个空槽位都要等到超时），
// 而应用本身只取第一台设备的读数。
//
// scanAllDevices = true 时扫完所有槽位，报告同一接收器上的多台设备，
// 供 --probe / --all 与诊断使用。
std::vector<Reading> ReadAll(double budgetSec = kDefaultBudgetSec,
                             bool scanAllDevices = false);

// 供自检使用：列出找到的接收器
struct ReceiverInfo {
    uint16_t vendorId = 0;
    uint16_t productId = 0;
    std::wstring longPath;    // usage 0x0002（HID++ 长报文）
    std::wstring shortPath;   // usage 0x0001，可能为空
};
std::vector<ReceiverInfo> FindReceivers();

// 自检：验证协议层能否工作，打印诊断
int RunSelfTest();

// 诊断：列出每台在线设备实际实现了哪些电量特性，便于确认某个型号
// 走的是哪条读取路径（0x1004 / 0x1000 / 0x1001 / 0x1F20 / 0x0104）。
int ProbeFeatures();

// ---------------------------------------------------------------- 帧构造
//
// 单独暴露出来供测试逐字节对照 Python 版。
// Receiver::Call 内部也调用它 —— 保证测试覆盖的是**真实代码路径**，
// 而不是测试里另抄一份（另抄一份迟早会与实现悄悄分叉）。
struct Frame {
    uint8_t bytes[20];
};

Frame BuildFrame(int deviceIndex, int featureIndex, int function,
                 const uint8_t* params = nullptr, int paramCount = 0);

// 打印一批典型帧，供与 Python 版做 diff
int DumpFrames();

}  // namespace hidpp
