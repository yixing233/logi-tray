// ============================================================================
//  alerts.cpp — 低电量通知策略实现
//  判定顺序与常量逐条对照 alerts.py（有 test_alerts.py 做行为对拍）。
// ============================================================================

#include "alerts.h"
#include "util.h"

#include <windows.h>   // DeleteFileW

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <sstream>

namespace alerts {

namespace {

double FindNumber(const std::string& text, const std::string& key,
                  double fallback) {
    const std::string pat = "\"" + key + "\"";
    size_t p = text.find(pat);
    if (p == std::string::npos) return fallback;
    p = text.find(':', p + pat.size());
    if (p == std::string::npos) return fallback;
    ++p;
    char* endp = nullptr;
    const double v = std::strtod(text.c_str() + p, &endp);
    if (endp == text.c_str() + p) return fallback;
    return v;
}

bool FindBool(const std::string& text, const std::string& key, bool fallback) {
    const std::string pat = "\"" + key + "\"";
    size_t p = text.find(pat);
    if (p == std::string::npos) return fallback;
    p = text.find(':', p + pat.size());
    if (p == std::string::npos) return fallback;
    ++p;
    while (p < text.size() && (text[p] == ' ' || text[p] == '\t')) ++p;
    if (text.compare(p, 4, "true") == 0) return true;
    if (text.compare(p, 5, "false") == 0) return false;
    return fallback;
}

}  // namespace

Notifier::Notifier(const std::string& statePathUtf8, int low, int critical)
    : path_(statePathUtf8), low_(low), critical_(critical) {
    Load();
}

void Notifier::Load() {
    std::ifstream f(path_, std::ios::binary);
    if (!f) return;
    std::stringstream ss;
    ss << f.rdbuf();
    const std::string text = ss.str();
    if (text.empty()) return;
    lowFired_ = FindBool(text, "low_fired", false);
    criticalFired_ = FindBool(text, "critical_fired", false);
}

void Notifier::Save() const {
    const std::wstring wpath = util::Utf8ToWide(path_);
    std::ofstream f(wpath.c_str(), std::ios::binary | std::ios::trunc);
    if (!f) return;
    f << "{\"low_fired\": " << (lowFired_ ? "true" : "false")
      << ", \"critical_fired\": " << (criticalFired_ ? "true" : "false")
      << "}";
}

void Notifier::SetThresholds(int low, int critical) {
    if (low != low_ || critical != critical_) {
        low_ = low;
        critical_ = critical;
        // 阈值变了，重新武装，让新阈值立即生效
        lowFired_ = false;
        criticalFired_ = false;
        Save();
    }
}

std::string Notifier::EtaText(double hoursRemaining) {
    if (hoursRemaining <= 0) return "";
    char buf[96];
    if (hoursRemaining >= 24) {
        std::snprintf(buf, sizeof(buf), u8"，预计还能用约 %.1f 天",
                      hoursRemaining / 24.0);
    } else if (hoursRemaining >= 1) {
        std::snprintf(buf, sizeof(buf), u8"，预计还能用约 %.0f 小时",
                      hoursRemaining);
    } else {
        std::snprintf(buf, sizeof(buf), u8"，预计很快耗尽");
    }
    return buf;
}

Verdict Notifier::Evaluate(int percent, bool charging,
                           double hoursRemaining) {
    Verdict v;

    if (charging) {
        // 充电中：解除武装，这样下次拔线后仍会正常提醒
        if (lowFired_ || criticalFired_) {
            lowFired_ = false;
            criticalFired_ = false;
            Save();
        }
        return v;
    }

    // 回升到阈值 + 间距以上，重新武装
    if (percent >= low_ + kRecoverMargin) {
        bool changed = false;
        if (lowFired_) { lowFired_ = false; changed = true; }
        if (criticalFired_) { criticalFired_ = false; changed = true; }
        if (changed) Save();
        return v;
    }

    const std::string eta = EtaText(hoursRemaining);

    // 严重低电量（比低电量优先，避免同时弹两条）
    if (percent <= critical_ && !criticalFired_) {
        criticalFired_ = true;
        lowFired_ = true;      // 严重档已覆盖低电量档
        Save();
        v.fire = true;
        v.title = u8"鼠标电量严重不足";
        char buf[160];
        std::snprintf(buf, sizeof(buf), u8"剩余 %d%%%s，请尽快充电。",
                      percent, eta.c_str());
        v.message = buf;
        return v;
    }

    // 低电量
    if (percent <= low_ && !lowFired_) {
        lowFired_ = true;
        Save();
        v.fire = true;
        v.title = u8"鼠标电量偏低";
        char buf[160];
        std::snprintf(buf, sizeof(buf), u8"剩余 %d%%%s。", percent,
                      eta.c_str());
        v.message = buf;
        return v;
    }

    return v;
}

// ---------------------------------------------------------------- 自检

int RunSelfTest() {
    util::Print(u8"\n=== 通知策略自检 ===\n");
    const std::string dir = util::DataDirUtf8();
    const std::string path = dir + "\\alert_selftest.json";
    DeleteFileW(util::Utf8ToWide(path).c_str());

    int fails = 0;
    auto check = [&](bool ok, const char* what) {
        if (!ok) { util::Print(u8"  ✗ %s\n", what); ++fails; }
        else util::Print(u8"  ✓ %s\n", what);
    };

    Notifier n(path, 20, 10);

    // 1) 正常电量不提醒
    check(!n.Evaluate(80, false).fire, "80% 不提醒");

    // 2) 掉到 20% 应提醒
    Verdict r = n.Evaluate(20, false);
    check(r.fire, "20% 触发低电量提醒");
    if (r.fire) util::Print(u8"     %s | %s\n", r.title.c_str(),
                            r.message.c_str());

    // 3) 阈值附近抖动不应重复提醒（迟滞生效）
    bool quiet = true;
    for (int pct : {19, 18, 21, 22, 17})
        if (n.Evaluate(pct, false).fire) quiet = false;
    check(quiet, "19/18/21/22/17 抖动不重复提醒（迟滞）");

    // 4) 掉到 9% 触发严重提醒
    Verdict c = n.Evaluate(9, false, 0.5);
    check(c.fire, "9% 触发严重提醒");
    if (c.fire) util::Print(u8"     %s | %s\n", c.title.c_str(),
                            c.message.c_str());

    // 5) 已提醒过不重复
    check(!n.Evaluate(8, false).fire, "严重档不重复");

    // 6) 充电中不提醒
    check(!n.Evaluate(5, true).fire, "充电中不提醒");

    // 7) 回升后重新武装，再跌破应能再提醒
    n.Evaluate(50, false);
    check(n.Evaluate(15, false).fire, "回升后重新武装");

    // 8) 持久化：新实例应记住已提醒状态
    Notifier n2(path, 20, 10);
    check(n2.lowFired(), "状态持久化");

    util::Print(fails ? u8"通知策略自检失败 %d 项\n"
                      : u8"通知策略自检全部通过 ✓\n", fails);
    return fails ? 1 : 0;
}

}  // namespace alerts
