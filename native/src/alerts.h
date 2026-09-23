// ============================================================================
//  alerts.h — 低电量通知策略（迟滞 + 分级 + 持久化）
//
//  从已验证的 Python 版 (alerts.py) 移植。
//
//  三个机制，缺一不可：
//    1. 迟滞：低于阈值触发一次，之后必须回升到"阈值 + 恢复间距"以上
//       才重新武装。否则电量在阈值附近抖动会反复弹窗。
//    2. 分级：越过低电量阈值提醒一次；更低一级再提醒一次。
//    3. 持久化：已提醒的档位写盘，重启不重复骚扰。
//  另外充电时不提醒低电量（正在充呢，提醒没意义）。
// ============================================================================
#pragma once

#include <string>

namespace alerts {

constexpr int kDefaultLow      = 20;   // 低电量阈值
constexpr int kDefaultCritical = 10;   // 严重低电量阈值
constexpr int kRecoverMargin   = 5;    // 回升超过阈值这么多才重新武装

struct Verdict {
    bool fire = false;
    std::string title;
    std::string message;
};

class Notifier {
public:
    Notifier(const std::string& statePathUtf8,
             int low = kDefaultLow, int critical = kDefaultCritical);

    void SetThresholds(int low, int critical);

    // 返回是否应弹通知。调用方负责真正弹出，这样本模块可独立测试。
    Verdict Evaluate(int percent, bool charging,
                     double hoursRemaining = -1.0);

    bool lowFired() const { return lowFired_; }
    bool criticalFired() const { return criticalFired_; }

    // 退出时把状态落盘（原本是私有的，但应用需要在关闭前显式保存）
    void Save() const;

private:
    void Load();
    static std::string EtaText(double hoursRemaining);

    std::string path_;
    int low_;
    int critical_;
    bool lowFired_ = false;
    bool criticalFired_ = false;
};

// 自检：复刻 Python 版的 8 个断言
int RunSelfTest();

}  // namespace alerts
