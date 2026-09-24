// ============================================================================
//  battery.h — 罗技电量特性的纯解析层
//
//  这里只做「字节 -> 语义」的转换，不碰 HID、不做 IO，因此可以用合成帧
//  离线单测（--test-battery）。协议依据全部来自权威实现，不是猜的：
//
//    * Linux 内核 drivers/hid/hid-logitech-hidpp.c
//        - 0x1004 能力字节 params[1] 的 BIT(1) = STATE_OF_CHARGE
//          （只有置位时 params[0] 才是百分比，否则它无意义）
//        - 0x1004 档位需与能力字节 params[0] 掩码后再判断
//        - 0x1001 flags: BIT(7)=充电中，bits[2:0]=充电阶段，BIT(3)=快充，
//          BIT(4)=涓流，BIT(5)=临界
//    * Solaar lib/logitech_receiver/hidpp20.py
//        - 电压 -> 百分比的分段线性插值表
//        - 无百分比时用档位近似值（FULL=90 / GOOD=50 / LOW=20 / CRITICAL=5）
//        - Centurion 0x0104 的响应布局
//
//  覆盖的特性：
//      0x1004 UNIFIED_BATTERY      （首选，支持百分比或仅档位两种模式）
//      0x1000 BATTERY_STATUS       （G304 等老设备）
//      0x1001 BATTERY_VOLTAGE      （只有电压的设备）
//      0x1F20 ADC_MEASUREMENT      （另一路电压测量）
//      0x0104 CENTURION_BATTERY_SOC（较新的 Centurion 系列）
// ============================================================================
#pragma once

#include <cstdint>
#include <string>

namespace battery {

// 统一后的充电状态（与内核 hidpp20_unifiedbattery_map_status 对齐）
//   0 放电中 / 1 充电中 / 2 慢充 / 3 已充满 / 4 异常 / 0xFF 未知
constexpr int kStateDischarging = 0;
constexpr int kStateCharging    = 1;
constexpr int kStateSlowCharge  = 2;
constexpr int kStateFull        = 3;
constexpr int kStateError       = 4;
constexpr int kStateUnknown     = 0xFF;

// 一条读数的解析结果。percent = -1 表示确实读不到电量。
struct Parsed {
    int         percent = -1;
    bool        percentInferred = false;  // true = 由档位或电压推算，非设备直报
    std::string level;                    // 档位文字，可能为空
    int         chargingState = kStateUnknown;
    std::string chargingText;
    int         externalPower = -1;
    int         voltageMv = 0;            // 0 = 未读到
    bool        valid = false;            // 是否至少拿到了电量信息
};

// ---------------------------------------------------------------- 文字与映射

std::string ChargingText(int state);
std::string BatteryStatusText(int status);

// BatteryStatus(0x1000) 的状态码 -> 统一状态（两者编号不同）
int NormalizeBatteryStatus(int status);

// 0x1004 的充电状态字节（0..4）本身就是统一编号，仅做范围校验
int NormalizeUnifiedCharging(int status);

bool IsChargingState(int state);

// 0x1004 档位位掩码 -> 文字（内核 FLAG_UNIFIED_BATTERY_LEVEL_*）
std::string LevelFromFlags(int bits);

// 只有档位、没有百分比时的近似百分比（取自 Solaar BatteryLevelApproximation）。
// 返回 -1 表示档位位掩码为空、无法判断。
int ApproximatePercentFromLevel(int bits);

// 电压 -> 百分比：按 Solaar 的电池放电曲线分段线性插值。
// 返回 -1 表示电压明显不在合理范围（视为读不到，避免给出假电量）。
int EstimatePercentFromVoltage(int millivolt);

// ---------------------------------------------------------------- 特性解析

// 0x1004 UNIFIED_BATTERY
//   status: GET_STATUS(function 1) 的应答，[电量%, 档位, 充电状态, 外接电源]
//   caps:   GET_CAPABILITIES(function 0) 的应答，[支持的档位掩码, 能力标志, ...]
// caps 传 nullptr 表示能力未知，此时沿用历史行为把 status[0] 当作百分比
// （老版本就是这么读的，保证不回归）。
Parsed ParseUnifiedBattery(const uint8_t* status, int statusLen,
                           const uint8_t* caps, int capsLen);

// 0x1000 BATTERY_STATUS：[电量%, 下一档阈值, 电池状态]
Parsed ParseBatteryStatus(const uint8_t* status, int statusLen);

// 0x1001 BATTERY_VOLTAGE：[电压高字节, 电压低字节, 标志]
Parsed ParseBatteryVoltage(const uint8_t* data, int len);

// 0x1F20 ADC_MEASUREMENT：[电压高字节, 电压低字节, 标志]
Parsed ParseAdcMeasurement(const uint8_t* data, int len);

// 0x0104 CENTURION_BATTERY_SOC：[电量%, 电量%(重复), 充电状态]
Parsed ParseCenturionSoc(const uint8_t* data, int len);

// ---------------------------------------------------------------- 自检

// 用合成帧跑解析层单测，返回 0 表示全部通过。供 --test-battery 使用。
int RunParseTests();

}  // namespace battery
