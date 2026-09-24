// ============================================================================
//  battery.cpp — 罗技电量特性的纯解析实现（无 IO，可脱机单测）
//
//  每个解析函数的字节布局都在注释里写明出处，改动前请先核对 battery.h
//  顶部列出的权威依据。这些格式一旦搞错不会报错，只会静默显示错电量。
// ============================================================================

#include "battery.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>

namespace battery {

// ---------------------------------------------------------------- 文字与映射

std::string ChargingText(int state) {
    switch (state) {
        case kStateDischarging: return u8"放电中";
        case kStateCharging:    return u8"充电中";
        case kStateSlowCharge:  return u8"充电中（慢充）";
        case kStateFull:        return u8"已充满";
        case kStateError:       return u8"充电异常";
        default: {
            char buf[32];
            std::snprintf(buf, sizeof(buf), u8"未知 (0x%02X)", state);
            return buf;
        }
    }
}

std::string BatteryStatusText(int status) {
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

int NormalizeBatteryStatus(int status) {
    // 0x1000 的状态码与 0x1004 不是同一套编号，这里统一到内部状态
    switch (status) {
        case 0: return kStateDischarging;
        case 1: return kStateCharging;
        case 2: return kStateCharging;       // almost full, still charging
        case 3: return kStateFull;
        case 4: return kStateSlowCharge;
        case 5:                              // invalid battery
        case 6: return kStateError;          // thermal error
        default: return kStateUnknown;
    }
}

int NormalizeUnifiedCharging(int status) {
    switch (status) {
        case 0: return kStateDischarging;
        case 1: return kStateCharging;
        case 2: return kStateSlowCharge;
        case 3: return kStateFull;
        case 4: return kStateError;
        default: return kStateUnknown;
    }
}

bool IsChargingState(int state) {
    return state == kStateCharging || state == kStateSlowCharge;
}

std::string LevelFromFlags(int bits) {
    // 内核 FLAG_UNIFIED_BATTERY_LEVEL_*：BIT(3)=FULL BIT(2)=GOOD
    //                                    BIT(1)=LOW  BIT(0)=CRITICAL
    if (bits & 0x08) return u8"满";
    if (bits & 0x04) return u8"良好";
    if (bits & 0x02) return u8"偏低";
    if (bits & 0x01) return u8"极低";
    return {};
}

int ApproximatePercentFromLevel(int bits) {
    // Solaar BatteryLevelApproximation：FULL=90 GOOD=50 LOW=20 CRITICAL=5
    if (bits & 0x08) return 90;
    if (bits & 0x04) return 50;
    if (bits & 0x02) return 20;
    if (bits & 0x01) return 5;
    return -1;
}

int EstimatePercentFromVoltage(int millivolt) {
    // Solaar estimate_battery_level_percentage 的放电曲线（电压 mV -> 百分比）。
    // 表按电压降序排列，相邻两点之间线性插值。
    static const struct { int mv; int pct; } kCurve[] = {
        {4186, 100}, {4067, 90}, {3989, 80}, {3922, 70},
        {3859, 60},  {3811, 50}, {3778, 40}, {3751, 30},
        {3717, 20},  {3671, 10}, {3646, 5},  {3579, 2},
        {3500, 0},
    };
    constexpr int kCount = static_cast<int>(sizeof(kCurve) / sizeof(kCurve[0]));

    if (millivolt >= kCurve[0].mv) return kCurve[0].pct;
    if (millivolt <= kCurve[kCount - 1].mv) {
        // 低于曲线最低点：视为耗尽，但明显离谱的读数（比如 0 或负数）
        // 当作读不到，避免把无效数据说成 0%。
        return millivolt <= 0 ? -1 : kCurve[kCount - 1].pct;
    }

    for (int i = 0; i + 1 < kCount; ++i) {
        const int vHigh = kCurve[i].mv;
        const int pHigh = kCurve[i].pct;
        const int vLow = kCurve[i + 1].mv;
        const int pLow = kCurve[i + 1].pct;

        if (millivolt <= vHigh && millivolt >= vLow) {
            const double ratio =
                static_cast<double>(millivolt - vLow) / (vHigh - vLow);
            const double pct = pLow + (pHigh - pLow) * ratio;
            return static_cast<int>(std::lround(pct));
        }
    }

    return -1;
}

// ---------------------------------------------------------------- 特性解析

namespace {

int ClampPercent(int value) { return std::max(0, std::min(100, value)); }

}  // namespace

Parsed ParseUnifiedBattery(const uint8_t* status, int statusLen,
                           const uint8_t* caps, int capsLen) {
    Parsed out;
    if (status == nullptr || statusLen < 1) {
        return out;
    }

    // 能力字节：BIT(1) = STATE_OF_CHARGE（设备会直报百分比）
    // 内核注释：若支持 state of charge 就优先用它，否则回落到档位。
    bool hasStateOfCharge = true;
    if (caps != nullptr && capsLen >= 2) {
        hasStateOfCharge = (caps[1] & 0x02) != 0;
    }

    // 档位字节可能与能力掩码对照后再判断（内核做法）
    int levelBits = statusLen > 1 ? status[1] : 0;
    if (caps != nullptr && capsLen >= 1 && !hasStateOfCharge) {
        levelBits &= caps[0];
    }

    out.level = LevelFromFlags(levelBits);

    if (statusLen > 2) {
        out.chargingState = NormalizeUnifiedCharging(status[2]);
        out.chargingText = ChargingText(out.chargingState);
    }
    if (statusLen > 3) {
        out.externalPower = status[3];
    }

    if (hasStateOfCharge) {
        out.percent = ClampPercent(status[0]);
        out.percentInferred = false;
    } else {
        // 只报档位：字节 0 无意义，用档位近似值代替，
        // 否则会把它当百分比读出荒谬的数字。
        const int approx = ApproximatePercentFromLevel(levelBits);
        out.percent = approx;
        out.percentInferred = approx >= 0;
    }

    out.valid = out.percent >= 0 || !out.level.empty() ||
                out.chargingState != kStateUnknown;
    return out;
}

Parsed ParseBatteryStatus(const uint8_t* status, int statusLen) {
    Parsed out;
    if (status == nullptr || statusLen < 1) {
        return out;
    }

    // [电量百分比, 下一档阈值, 电池状态]
    out.percent = ClampPercent(status[0]);
    out.percentInferred = false;

    if (statusLen > 2) {
        out.chargingState = NormalizeBatteryStatus(status[2]);
        out.chargingText = BatteryStatusText(status[2]);
    }

    // 0x1000 不提供档位位掩码，但内核按电量映射档位（<11 极低 / <30 偏低 /
    // <81 良好 / 其余 满），这里沿用同一阈值，保证文字与百分比一致。
    if (out.percent < 11)       out.level = u8"极低";
    else if (out.percent < 30)  out.level = u8"偏低";
    else if (out.percent < 81)  out.level = u8"良好";
    else                        out.level = u8"满";

    out.valid = true;
    return out;
}

Parsed ParseBatteryVoltage(const uint8_t* data, int len) {
    Parsed out;
    if (data == nullptr || len < 2) {
        return out;
    }

    // 内核 hidpp20_battery_map_status_voltage：
    //   电压 = be16(data[0..1])，flags = data[2]
    //   BIT(7) 置位表示正在充电，此时 bits[2:0] 是充电阶段：
    //       0 = 充电中, 1 = 已充满, 2 = 未在充电, 其它 = 未知
    //   BIT(3) = 快充, BIT(4) = 涓流, BIT(5) = 电量临界
    out.voltageMv = (static_cast<int>(data[0]) << 8) | data[1];

    const int flags = len > 2 ? data[2] : 0;

    if (flags & 0x80) {
        switch (flags & 0x07) {
            case 0: out.chargingState = kStateCharging; break;
            case 1: out.chargingState = kStateFull; break;
            case 2: out.chargingState = kStateDischarging; break;
            default: out.chargingState = kStateUnknown; break;
        }
    } else {
        out.chargingState = kStateDischarging;
    }

    if (flags & 0x10) {                       // BIT(4) 涓流（慢充）
        out.chargingState = kStateSlowCharge;
    } else if (flags & 0x08) {                // BIT(3) 快充仍属充电中
        if (out.chargingState == kStateDischarging) {
            out.chargingState = kStateCharging;
        }
    }

    out.chargingText = ChargingText(out.chargingState);
    out.percent = EstimatePercentFromVoltage(out.voltageMv);
    out.percentInferred = out.percent >= 0;

    // 临界标志：档位文字由电压推出的百分比统一给出，这里只在明显低电量时
    // 覆盖一次，保证"极低"不会因为电压插值偏高而消失。
    if (flags & 0x20) {
        out.level = u8"极低";
    }

    out.valid = out.percent >= 0;
    return out;
}

Parsed ParseAdcMeasurement(const uint8_t* data, int len) {
    Parsed out;
    if (data == nullptr || len < 2) {
        return out;
    }

    // Solaar decipher_adc_measurement：[电压 be16, 标志]
    //   BIT(0) 置位表示有充电信息，此时 BIT(1) = 正在充电
    out.voltageMv = (static_cast<int>(data[0]) << 8) | data[1];

    const int flags = len > 2 ? data[2] : 0;
    if (flags & 0x01) {
        out.chargingState = (flags & 0x02) ? kStateCharging : kStateDischarging;
    } else {
        out.chargingState = kStateDischarging;
    }

    out.chargingText = ChargingText(out.chargingState);
    out.percent = EstimatePercentFromVoltage(out.voltageMv);
    out.percentInferred = out.percent >= 0;
    out.valid = out.percent >= 0;
    return out;
}

Parsed ParseCenturionSoc(const uint8_t* data, int len) {
    Parsed out;
    if (data == nullptr || len < 1) {
        return out;
    }

    // Solaar decipher_battery_centurion（0x0104）：
    //   byte0 = 电量百分比，byte1 = 重复的百分比，byte2 = 充电状态
    //   充电状态 1/2 = 充电中，3 = 已充满，其余 = 放电中
    out.percent = ClampPercent(data[0]);
    out.percentInferred = false;

    const int charging = len >= 3 ? data[2] : 0;
    if (charging == 1 || charging == 2) {
        out.chargingState = kStateCharging;
    } else if (charging == 3) {
        out.chargingState = kStateFull;
    } else {
        out.chargingState = kStateDischarging;
    }

    out.chargingText = ChargingText(out.chargingState);
    out.valid = true;
    return out;
}

// ---------------------------------------------------------------- 自检

namespace {

int g_pass = 0;
int g_fail = 0;

void Check(bool ok, const char* what, const std::string& got,
           const std::string& want) {
    if (ok) {
        ++g_pass;
        std::printf("  [OK]   %s\n", what);
    } else {
        ++g_fail;
        std::printf("  [FAIL] %s   got=%s want=%s\n", what,
                    got.c_str(), want.c_str());
    }
}

void CheckInt(bool ok, const char* what, int got, int want) {
    char g[32], w[32];
    std::snprintf(g, sizeof(g), "%d", got);
    std::snprintf(w, sizeof(w), "%d", want);
    Check(ok, what, g, w);
}

std::string LevelText(int bits) {
    const std::string s = LevelFromFlags(bits);
    return s.empty() ? std::string("(none)") : s;
}

}  // namespace

int RunParseTests() {
    g_pass = 0;
    g_fail = 0;

    std::printf(u8"\n=== 电量解析层单测（合成帧） ===\n");

    // ---------- 0x1004：支持 state of charge（直报百分比）----------
    std::printf(u8"\n[0x1004 UnifiedBattery · 直报百分比]\n");
    {
        // caps[1] BIT(1) 置位 = 有百分比；档位掩码 0x0F
        const uint8_t caps[3]  = {0x0F, 0x02, 0x00};
        const uint8_t status[4] = {82, 0x04, 0x00, 0x00};  // 82%, 良好, 放电
        const Parsed p = ParseUnifiedBattery(status, 4, caps, 3);
        CheckInt(p.percent == 82, "percent = 82", p.percent, 82);
        Check(p.level == u8"良好", "level = 良好", p.level, u8"良好");
        CheckInt(p.chargingState == kStateDischarging, "state = 放电",
                 p.chargingState, kStateDischarging);
        Check(!p.percentInferred, "percentInferred = false",
              p.percentInferred ? "true" : "false", "false");
    }
    {
        // 100% 与充电中
        const uint8_t caps[3]  = {0x0F, 0x02, 0x00};
        const uint8_t status[4] = {100, 0x08, 0x01, 0x01};
        const Parsed p = ParseUnifiedBattery(status, 4, caps, 3);
        CheckInt(p.percent == 100, "percent = 100", p.percent, 100);
        Check(p.level == u8"满", "level = 满", p.level, u8"满");
        CheckInt(p.chargingState == kStateCharging, "state = 充电中",
                 p.chargingState, kStateCharging);
        CheckInt(p.externalPower == 1, "externalPower = 1", p.externalPower, 1);
    }

    // ---------- 0x1004：只报档位（关键新增能力）----------
    std::printf(u8"\n[0x1004 UnifiedBattery · 仅档位（无百分比）]\n");
    {
        // caps[1] BIT(1) 清零 => 字节 0 不是百分比。老代码会把它当百分比，
        // 这里必须改用档位近似值。
        const uint8_t caps[3]  = {0x0F, 0x00, 0x00};
        const uint8_t status[4] = {0x00, 0x04, 0x00, 0x00};  // 字节0 无意义
        const Parsed p = ParseUnifiedBattery(status, 4, caps, 3);
        CheckInt(p.percent == 50, "percent 用档位近似 = 50", p.percent, 50);
        Check(p.level == u8"良好", "level = 良好", p.level, u8"良好");
        Check(p.percentInferred, "标记为推算值", 
              p.percentInferred ? "true" : "false", "true");
    }
    {
        const uint8_t caps[3]  = {0x0F, 0x00, 0x00};
        const uint8_t status[4] = {0x00, 0x08, 0x03, 0x00};  // FULL 档
        const Parsed p = ParseUnifiedBattery(status, 4, caps, 3);
        CheckInt(p.percent == 90, "FULL 档 -> 90", p.percent, 90);
        CheckInt(p.chargingState == kStateFull, "state = 已充满",
                 p.chargingState, kStateFull);
    }
    {
        // 能力未知（没查 capabilities）：沿用历史行为，把字节 0 当百分比，
        // 保证老设备的读数不回归。
        const uint8_t status[4] = {77, 0x04, 0x00, 0x00};
        const Parsed p = ParseUnifiedBattery(status, 4, nullptr, 0);
        CheckInt(p.percent == 77, "能力未知时沿用旧行为 = 77", p.percent, 77);
        Check(!p.percentInferred, "不标记为推算值",
              p.percentInferred ? "true" : "false", "false");
    }
    {
        // 档位字节需与能力掩码相与：设备报 0xFF，但只声明支持 GOOD|LOW
        // （0x06，不含 BIT(3)=FULL）。不相与就会误判成"满 / 90%"，
        // 相与后 0xFF & 0x06 = 0x06 -> 良好 / 50%。
        const uint8_t caps[3]  = {0x06, 0x00, 0x00};
        const uint8_t status[4] = {0x00, 0xFF, 0x00, 0x00};
        const Parsed p = ParseUnifiedBattery(status, 4, caps, 3);
        Check(p.level == u8"良好", "掩码后 level = 良好", p.level, u8"良好");
        CheckInt(p.percent == 50, "掩码后 percent = 50", p.percent, 50);
    }

    // ---------- 0x1000：G304 等老设备 ----------
    std::printf(u8"\n[0x1000 BatteryStatus]\n");
    {
        const uint8_t status[3] = {89, 20, 0};  // 89%, 放电
        const Parsed p = ParseBatteryStatus(status, 3);
        CheckInt(p.percent == 89, "percent = 89", p.percent, 89);
        CheckInt(p.chargingState == kStateDischarging, "state = 放电",
                 p.chargingState, kStateDischarging);
        Check(p.level == u8"满", "89% -> 满", p.level, u8"满");
    }
    {
        const uint8_t status[3] = {45, 20, 1};  // 充电中
        const Parsed p = ParseBatteryStatus(status, 3);
        CheckInt(p.chargingState == kStateCharging, "state = 充电中",
                 p.chargingState, kStateCharging);
        Check(p.level == u8"良好", "45% -> 良好", p.level, u8"良好");
    }
    {
        const uint8_t status[3] = {8, 20, 0};   // 临界
        const Parsed p = ParseBatteryStatus(status, 3);
        Check(p.level == u8"极低", "8% -> 极低", p.level, u8"极低");
    }
    {
        const uint8_t status[3] = {3, 20, 6};   // 温度异常
        const Parsed p = ParseBatteryStatus(status, 3);
        CheckInt(p.chargingState == kStateError, "状态 6 -> 异常",
                 p.chargingState, kStateError);
    }

    // ---------- 0x1001：只有电压 ----------
    std::printf(u8"\n[0x1001 BatteryVoltage]\n");
    {
        // 4186 mV -> 100%，flags=0 放电
        const uint8_t d[3] = {0x10, 0x5A, 0x00};
        const Parsed p = ParseBatteryVoltage(d, 3);
        CheckInt(p.voltageMv == 4186, "voltage = 4186 mV", p.voltageMv, 4186);
        CheckInt(p.percent == 100, "4186 mV -> 100%", p.percent, 100);
        CheckInt(p.chargingState == kStateDischarging, "state = 放电",
                 p.chargingState, kStateDischarging);
    }
    {
        // 3811 mV -> 50%，BIT(7) 置位且阶段 0 => 充电中
        const uint8_t d[3] = {0x0E, 0xE3, 0x80};
        const Parsed p = ParseBatteryVoltage(d, 3);
        CheckInt(p.voltageMv == 3811, "voltage = 3811 mV", p.voltageMv, 3811);
        CheckInt(p.percent == 50, "3811 mV -> 50%", p.percent, 50);
        CheckInt(p.chargingState == kStateCharging, "state = 充电中",
                 p.chargingState, kStateCharging);
    }
    {
        // BIT(7) + 阶段 1 => 已充满
        const uint8_t d[3] = {0x10, 0x5A, 0x81};
        const Parsed p = ParseBatteryVoltage(d, 3);
        CheckInt(p.chargingState == kStateFull, "阶段 1 -> 已充满",
                 p.chargingState, kStateFull);
    }
    {
        // BIT(4) 涓流 => 慢充
        const uint8_t d[3] = {0x0E, 0xE3, 0x90};
        const Parsed p = ParseBatteryVoltage(d, 3);
        CheckInt(p.chargingState == kStateSlowCharge, "BIT(4) -> 慢充",
                 p.chargingState, kStateSlowCharge);
    }
    {
        // BIT(5) 临界 => 极低
        const uint8_t d[3] = {0x0E, 0x62, 0x20};   // 3682 mV, 临界
        const Parsed p = ParseBatteryVoltage(d, 3);
        Check(p.level == u8"极低", "BIT(5) -> 极低", p.level, u8"极低");
    }
    {
        // 无效电压（0）不能报成 0%
        const uint8_t d[3] = {0x00, 0x00, 0x00};
        const Parsed p = ParseBatteryVoltage(d, 3);
        CheckInt(p.percent == -1, "0 mV -> 读不到（不是 0%）", p.percent, -1);
    }

    // ---------- 电压插值边界 ----------
    std::printf(u8"\n[电压 -> 百分比 插值]\n");
    CheckInt(EstimatePercentFromVoltage(4300) == 100, "4300 mV -> 100",
             EstimatePercentFromVoltage(4300), 100);
    CheckInt(EstimatePercentFromVoltage(3500) == 0, "3500 mV -> 0",
             EstimatePercentFromVoltage(3500), 0);
    CheckInt(EstimatePercentFromVoltage(3000) == 0, "3000 mV -> 0",
             EstimatePercentFromVoltage(3000), 0);
    {
        // 两点之间线性插值：(4067+3989)/2 = 4028 -> 约 85%
        const int mid = EstimatePercentFromVoltage(4028);
        CheckInt(mid >= 84 && mid <= 86, "4028 mV -> 约 85", mid, 85);
    }

    // ---------- 0x1F20 ADC ----------
    std::printf(u8"\n[0x1F20 ADC_MEASUREMENT]\n");
    {
        const uint8_t d[3] = {0x0E, 0xE3, 0x03};   // 3811 mV, BIT0|BIT1 = 充电
        const Parsed p = ParseAdcMeasurement(d, 3);
        CheckInt(p.voltageMv == 3811, "voltage = 3811 mV", p.voltageMv, 3811);
        CheckInt(p.percent == 50, "-> 50%", p.percent, 50);
        CheckInt(p.chargingState == kStateCharging, "state = 充电中",
                 p.chargingState, kStateCharging);
    }

    // ---------- 0x0104 Centurion ----------
    std::printf(u8"\n[0x0104 CenturionBatterySOC]\n");
    {
        const uint8_t d[3] = {64, 64, 1};
        const Parsed p = ParseCenturionSoc(d, 3);
        CheckInt(p.percent == 64, "percent = 64", p.percent, 64);
        CheckInt(p.chargingState == kStateCharging, "状态 1 -> 充电中",
                 p.chargingState, kStateCharging);
    }
    {
        const uint8_t d[3] = {100, 100, 3};
        const Parsed p = ParseCenturionSoc(d, 3);
        CheckInt(p.chargingState == kStateFull, "状态 3 -> 已充满",
                 p.chargingState, kStateFull);
    }
    {
        const uint8_t d[3] = {30, 30, 0};
        const Parsed p = ParseCenturionSoc(d, 3);
        CheckInt(p.chargingState == kStateDischarging, "状态 0 -> 放电",
                 p.chargingState, kStateDischarging);
    }

    // ---------- 短帧 / 空指针健壮性 ----------
    std::printf(u8"\n[健壮性：短帧不应崩溃或给出假值]\n");
    {
        const Parsed a = ParseUnifiedBattery(nullptr, 0, nullptr, 0);
        Check(!a.valid, "空 status 不产生有效读数",
              a.valid ? "valid" : "invalid", "invalid");
        const uint8_t one[1] = {50};
        const Parsed b = ParseUnifiedBattery(one, 1, nullptr, 0);
        CheckInt(b.percent == 50, "仅 1 字节也能读出百分比", b.percent, 50);
        CheckInt(b.chargingState == kStateUnknown, "缺状态字节 -> 未知",
                 b.chargingState, kStateUnknown);

        const Parsed c = ParseBatteryVoltage(nullptr, 0);
        Check(!c.valid, "空电压帧无效", c.valid ? "valid" : "invalid", "invalid");

        const uint8_t sv[2] = {89, 20};
        const Parsed d = ParseBatteryStatus(sv, 2);
        CheckInt(d.percent == 89, "0x1000 缺状态字节仍可读百分比", d.percent, 89);
        CheckInt(d.chargingState == kStateUnknown, "缺状态字节 -> 未知",
                 d.chargingState, kStateUnknown);
    }

    // ---------- 边界：0 与 100 ----------
    std::printf(u8"\n[边界值]\n");
    {
        const uint8_t caps[3] = {0x0F, 0x02, 0x00};
        const uint8_t zero[4] = {0, 0x00, 0x00, 0x00};
        const Parsed p = ParseUnifiedBattery(zero, 4, caps, 3);
        CheckInt(p.percent == 0, "0% 是合法读数", p.percent, 0);

        const uint8_t over[4] = {120, 0x08, 0x00, 0x00};
        const Parsed q = ParseUnifiedBattery(over, 4, caps, 3);
        CheckInt(q.percent == 100, "超过 100 被钳制", q.percent, 100);
    }

    std::printf(u8"\n----------------------------------------\n");
    std::printf(u8"通过 %d 项，失败 %d 项\n", g_pass, g_fail);
    if (g_fail == 0) {
        std::printf(u8"电量解析层自检通过 ✓\n");
        return 0;
    }
    std::printf(u8"电量解析层自检失败 ✗\n");
    return 1;
}

}  // namespace battery
