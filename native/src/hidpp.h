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
};

// 充电状态（对应内核 hidpp20_unifiedbattery_map_status）
inline std::string ChargingText(int state) {
    switch (state) {
        case 0: return u8"放电中";
        case 1: return u8"充电中";
        case 2: return u8"充电中（慢充）";
        case 3: return u8"已充满";
        case 4: return u8"充电异常";
        default: {
            char buf[32];
            std::snprintf(buf, sizeof(buf), u8"未知 (0x%02X)", state);
            return buf;
        }
    }
}

// BatteryStatus(0x1000) 状态码（与 UnifiedBattery 的状态码不同）。
// 转成统一内部状态，便于历史与通知共用一套判断。
inline int NormalizeBatteryStatus(int status) {
    switch (status) {
        case 0: return 0;  // discharging
        case 1: return 1;  // recharging
        case 2: return 1;  // almost full, still charging
        case 3: return 3;  // full
        case 4: return 2;  // slow recharge
        case 5: case 6: return 4;  // invalid battery / thermal error
        default: return 0xFF;
    }
}

inline std::string BatteryStatusText(int status) {
    switch (status) {
        case 0: return u8"放电中";
        case 1: return u8"充电中";
        case 2: return u8"充电中（接近充满）";
        case 3: return u8"已充满";
        case 4: return u8"充电中（慢充）";
        case 5: return u8"电池异常";
        case 6: return u8"温度异常";
        default: return u8"未知电池状态";
    }
}

inline bool IsChargingState(int state) {
    return state == 1 || state == 2;
}

// 电量档位位掩码（内核 FLAG_UNIFIED_BATTERY_LEVEL_*）
inline std::string LevelFromFlags(int bits) {
    if (bits & 0x08) return u8"满";
    if (bits & 0x04) return u8"良好";
    if (bits & 0x02) return u8"偏低";
    if (bits & 0x01) return u8"极低";
    return {};
}

// ---------------------------------------------------------------- 接口

// 读一次当前所有在线罗技鼠标的电量。
// budgetSec 是**整个函数**的时间预算，不是每次 HID 往返的预算。
// 鼠标休眠时返回空列表（这是正常状态，不是错误）。
std::vector<Reading> ReadAll(double budgetSec = kDefaultBudgetSec);

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
