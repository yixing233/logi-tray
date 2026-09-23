// ============================================================================
//  battery_history.cpp — 电量历史与续航预测实现
//
//  算法逐行对照 battery_history.py，常量与判定顺序完全一致
//  （有 test_history.py 做数值对拍）。
//
//  JSON：本机没有可用的 JSON 库（无 vcpkg），而这里的数据结构极简单
//  （一个对象，内含 samples 数组，每项三个字段），所以手写一个最小解析器，
//  比引入依赖更省事，也避免版本/构建问题。
// ============================================================================

#include "battery_history.h"
#include "util.h"

#include <windows.h>   // FILETIME / GetSystemTimeAsFileTime

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <sstream>

namespace battery {

double NowUnix() {
    // Windows FILETIME 起点是 1601-01-01，Unix 起点是 1970-01-01。
    // 两者相差 11644473600 秒。
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER li;
    li.LowPart = ft.dwLowDateTime;
    li.HighPart = ft.dwHighDateTime;
    // 100 纳秒为单位
    const double seconds = static_cast<double>(li.QuadPart) / 1e7;
    return seconds - 11644473600.0;
}

namespace {

// ---------------------------------------------------------------- 极简 JSON

// 从文本里找 "key" 后的数值。找不到返回 fallback。
double FindNumber(const std::string& text, const std::string& key,
                  size_t from, double fallback) {
    const std::string pat = "\"" + key + "\"";
    size_t p = text.find(pat, from);
    if (p == std::string::npos) return fallback;
    p = text.find(':', p + pat.size());
    if (p == std::string::npos) return fallback;
    ++p;
    while (p < text.size() && (text[p] == ' ' || text[p] == '\t')) ++p;
    char* endp = nullptr;
    const double v = std::strtod(text.c_str() + p, &endp);
    if (endp == text.c_str() + p) return fallback;
    return v;
}

bool FindBool(const std::string& text, const std::string& key, size_t from,
              bool fallback) {
    const std::string pat = "\"" + key + "\"";
    size_t p = text.find(pat, from);
    if (p == std::string::npos) return fallback;
    p = text.find(':', p + pat.size());
    if (p == std::string::npos) return fallback;
    ++p;
    while (p < text.size() && (text[p] == ' ' || text[p] == '\t')) ++p;
    if (text.compare(p, 4, "true") == 0) return true;
    if (text.compare(p, 5, "false") == 0) return false;
    return fallback;
}

// 找 key 之后的位置（用于在对象内定位字段）
size_t FindKey(const std::string& text, const std::string& key, size_t from) {
    const std::string pat = "\"" + key + "\"";
    size_t p = text.find(pat, from);
    if (p == std::string::npos) return std::string::npos;
    return text.find(':', p + pat.size());
}

}  // namespace

// ---------------------------------------------------------------- History

History::History(const std::string& pathUtf8) : path_(pathUtf8) { Load(); }

void History::Load() {
    samples_.clear();
    std::ifstream f(path_, std::ios::binary);
    if (!f) return;
    std::stringstream ss;
    ss << f.rdbuf();
    const std::string text = ss.str();
    if (text.empty()) return;

    // 逐个解析 samples 数组里的对象。数据由本程序写出，格式固定：
    //   {"t": 123.4, "percent": 94, "charging": false}
    const size_t arr = text.find("\"samples\"");
    if (arr == std::string::npos) return;

    size_t pos = text.find('[', arr);
    if (pos == std::string::npos) return;
    ++pos;

    while (true) {
        const size_t objStart = text.find('{', pos);
        if (objStart == std::string::npos) break;
        const size_t objEnd = text.find('}', objStart);
        if (objEnd == std::string::npos) break;
        // 若在对象之前就出现了数组结束符，说明数组已结束
        const size_t arrEnd = text.find(']', pos);
        if (arrEnd != std::string::npos && arrEnd < objStart) break;

        const std::string obj = text.substr(objStart, objEnd - objStart + 1);
        Sample s;
        s.t = FindNumber(obj, "t", 0, -1.0);
        s.percent = static_cast<int>(FindNumber(obj, "percent", 0, -1));
        s.charging = FindBool(obj, "charging", 0, false);
        if (s.t >= 0 && s.percent >= 0) samples_.push_back(s);

        pos = objEnd + 1;
    }
}

void History::Save() const {
    const std::wstring wpath = util::Utf8ToWide(path_);
    std::ofstream f(wpath.c_str(), std::ios::binary | std::ios::trunc);
    if (!f) return;

    f << "{\n \"samples\": [";
    for (size_t i = 0; i < samples_.size(); ++i) {
        const Sample& s = samples_[i];
        if (i) f << ",";
        char buf[128];
        std::snprintf(buf, sizeof(buf),
                      "\n  {\"t\": %.3f, \"percent\": %d, \"charging\": %s}",
                      s.t, s.percent, s.charging ? "true" : "false");
        f << buf;
    }
    if (!samples_.empty()) f << "\n ";
    f << "]\n}\n";
}

bool History::Add(int percent, bool charging, double nowUnix) {
    // 同一秒内的重复读数没有意义；但充电状态变化时仍然记录
    if (!samples_.empty() && nowUnix - samples_.back().t < 20.0) {
        if (samples_.back().charging == charging) return false;
    }
    Sample s;
    s.t = nowUnix;
    s.percent = percent;
    s.charging = charging;
    samples_.push_back(s);
    Prune(nowUnix);
    return true;
}

void History::Prune(double nowUnix) {
    const double cutoff = nowUnix - kMaxSampleAgeDays * 86400.0;
    samples_.erase(
        std::remove_if(samples_.begin(), samples_.end(),
                       [cutoff](const Sample& s) { return s.t < cutoff; }),
        samples_.end());
    if (samples_.size() > kMaxSamples)
        samples_.erase(samples_.begin(),
                       samples_.begin() +
                           (samples_.size() - kMaxSamples));
}

// 把历史切成若干"持续放电、电量单调不增"的片段
std::vector<std::vector<Sample>> History::DischargeSegments() const {
    std::vector<std::vector<Sample>> segments;
    std::vector<Sample> current;

    for (const auto& s : samples_) {
        if (s.charging) {
            if (current.size() >= 2) segments.push_back(current);
            current.clear();
            continue;
        }
        if (!current.empty() && s.percent > current.back().percent) {
            // 电量回升却没标记充电：视为一次断点
            if (current.size() >= 2) segments.push_back(current);
            current.clear();
        }
        current.push_back(s);
    }
    if (current.size() >= 2) segments.push_back(current);
    return segments;
}

// 对 (时间, 电量) 做最小二乘，返回放电速度（百分点/小时）
bool History::LeastSquaresRate(const std::vector<Sample>& seg, double& out) {
    if (seg.size() < 2) return false;
    const double t0 = seg.front().t;

    const size_t n = seg.size();
    double sumX = 0, sumY = 0;
    std::vector<double> xs(n), ys(n);
    for (size_t i = 0; i < n; ++i) {
        xs[i] = (seg[i].t - t0) / 3600.0;      // 小时
        ys[i] = static_cast<double>(seg[i].percent);
        sumX += xs[i];
        sumY += ys[i];
    }
    const double meanX = sumX / n;
    const double meanY = sumY / n;

    double denom = 0, numer = 0;
    for (size_t i = 0; i < n; ++i) {
        denom += (xs[i] - meanX) * (xs[i] - meanX);
        numer += (xs[i] - meanX) * (ys[i] - meanY);
    }
    if (denom <= 1e-9) return false;
    const double slope = numer / denom;
    // 斜率为负表示电量下降，取绝对值作为放电速度
    out = -slope;
    return true;
}

std::string History::Confidence(double span, int drop, int count) {
    if (span >= 3 && drop >= 10 && count >= 6) return u8"高";
    if (span >= 1 && drop >= 5 && count >= 3) return u8"中";
    return u8"低";
}

bool History::EstimateRemaining(int currentPercent, Estimate& out) const {
    const auto segments = DischargeSegments();
    if (segments.empty()) return false;

    // 从最近的一段开始往前累积，直到满足门槛；
    // 若全部累积完仍不达标，就老实返回失败，绝不拿不足的数据硬算。
    const double now = NowUnix();
    const double windowStart = now - kRebuildWindowHours * 3600.0;

    std::vector<Sample> chosen;
    bool qualified = false;

    for (auto it = segments.rbegin(); it != segments.rend(); ++it) {
        std::vector<Sample> seg;
        for (const auto& s : *it)
            if (s.t >= windowStart) seg.push_back(s);
        if (seg.size() < 2) continue;

        // chosen = seg + chosen（新的一段插到前面）
        std::vector<Sample> merged = seg;
        merged.insert(merged.end(), chosen.begin(), chosen.end());
        chosen.swap(merged);

        const double span = (chosen.back().t - chosen.front().t) / 3600.0;
        const int drop = chosen.front().percent - chosen.back().percent;
        if (span >= kMinSpanHours && drop >= kMinDropPercent) {
            qualified = true;
            break;
        }
    }

    if (!qualified || chosen.size() < 2) return false;

    const double span = (chosen.back().t - chosen.front().t) / 3600.0;
    const int drop = chosen.front().percent - chosen.back().percent;
    if (span <= 0 || drop <= 0) return false;

    double rate = 0;
    if (!LeastSquaresRate(chosen, rate)) return false;
    if (rate <= 0.01) return false;

    out.ratePerHour = std::round(rate * 100.0) / 100.0;
    out.hoursRemaining = std::round((currentPercent / rate) * 10.0) / 10.0;
    out.spanHours = std::round(span * 10.0) / 10.0;
    out.dropPercent = drop;
    out.sampleCount = static_cast<int>(chosen.size());
    out.confidence = Confidence(span, drop, out.sampleCount);
    return true;
}

// ---------------------------------------------------------------- 按小时聚合

namespace {

// 把 Unix 时间戳对齐到它所在小时的起点（本地时间）。
// 用本地时间对齐，这样"09-22 14 时"就是用户眼里的 14 点。
//
// 时区偏移必须**按样本各自的时间**计算，不能用全局的 _timezone：
// 后者不区分夏令时，跨 DST 切换的样本会被错分到相邻小时。
// 做法是把本地的年月日时分秒用 _mkgmtime 反算回 UTC —— 它会按该时间点
// 自身的 DST 状态来换算。
// **必须用 mktime，不能用 _mkgmtime。**
//
//   mktime(tm)    把 tm 当作**本地时间**解释，并按该时刻的 DST 换算出 time_t
//   _mkgmtime(tm) 把 tm 当作 **UTC** 解释
//
// 早期这里用了 _mkgmtime，而输入是 localtime_s 给出的本地字段，
// 于是每个小时桶都被平移了一个时区偏移（本机 +8 小时）：
// 数据实际在 14:00~19:00，标签却显示 21:00~02:00。
// 因为样本与小时网格用的是同一个错误变换，分桶**计数**看起来完全正常，
// 只有**标签**是错的 —— 这类 bug 很容易蒙混过关，必须靠严格断言挡住。
//
// tm_isdst = -1 交给 CRT 自行判断该时刻是否处于夏令时。
int64_t FloorToLocalHour(double unixSec) {
    const time_t t = static_cast<time_t>(unixSec);
    struct tm lt{};
    localtime_s(&lt, &t);
    lt.tm_min = 0;
    lt.tm_sec = 0;
    lt.tm_isdst = -1;
    return static_cast<int64_t>(std::mktime(&lt));
}

}  // namespace

std::vector<HourBucket> History::RecentHours(int countHours,
                                             double nowUnix) const {
    std::vector<HourBucket> out;
    if (countHours <= 0) return out;

    const double now = (nowUnix > 0) ? nowUnix : NowUnix();
    const int64_t thisHour = FloorToLocalHour(now);
    const int64_t firstHour =
        thisHour - static_cast<int64_t>(countHours - 1) * 3600;

    // 先建好所有桶（包括没有数据的），这样调用方能画出"断档"
    out.resize(static_cast<size_t>(countHours));
    for (int i = 0; i < countHours; ++i) {
        out[static_cast<size_t>(i)].hourStart = firstHour + i * 3600LL;
        out[static_cast<size_t>(i)].count = 0;
    }

    for (const auto& s : samples_) {
        const int64_t h = FloorToLocalHour(s.t);
        if (h < firstHour || h > thisHour) continue;
        const size_t idx =
            static_cast<size_t>((h - firstHour) / 3600);
        if (idx >= out.size()) continue;
        HourBucket& b = out[idx];
        if (b.count == 0) {
            b.minPercent = b.maxPercent = b.lastPercent = s.percent;
        } else {
            if (s.percent < b.minPercent) b.minPercent = s.percent;
            if (s.percent > b.maxPercent) b.maxPercent = s.percent;
            b.lastPercent = s.percent;   // samples_ 是升序的
        }
        ++b.count;
        if (s.charging) b.anyCharging = true;
    }
    return out;
}

History::Summary History::Analyze(int windowHours, double nowUnix) const {
    Summary s;
    const double now = (nowUnix > 0) ? nowUnix : NowUnix();
    const int64_t thisHour = FloorToLocalHour(now);
    const int64_t firstHour =
        thisHour - static_cast<int64_t>(windowHours - 1) * 3600;

    int64_t lo = 0, hi = 0;
    int hoursWithData = 0;
    std::vector<bool> seen(static_cast<size_t>(windowHours), false);

    for (const auto& x : samples_) {
        if (x.t < static_cast<double>(firstHour) ||
            x.t > now + 3600) continue;
        if (s.sampleCount == 0) {
            s.firstPercent = s.minPercent = s.maxPercent = x.percent;
            lo = hi = static_cast<int64_t>(x.t);
        } else {
            if (x.percent < s.minPercent) s.minPercent = x.percent;
            if (x.percent > s.maxPercent) s.maxPercent = x.percent;
            if (static_cast<int64_t>(x.t) < lo) lo = static_cast<int64_t>(x.t);
            if (static_cast<int64_t>(x.t) > hi) hi = static_cast<int64_t>(x.t);
        }
        s.lastPercent = x.percent;
        ++s.sampleCount;
        if (x.charging) ++s.chargedSamples;

        const int64_t h = FloorToLocalHour(x.t);
        const int idx = static_cast<int>((h - firstHour) / 3600);
        if (idx >= 0 && idx < windowHours && !seen[static_cast<size_t>(idx)]) {
            seen[static_cast<size_t>(idx)] = true;
            ++hoursWithData;
        }
    }
    s.spanHours = (s.sampleCount > 1) ? (hi - lo) / 3600.0 : 0.0;
    s.hoursWithData = hoursWithData;
    s.hoursCovered = windowHours;
    return s;
}

// ---------------------------------------------------------------- 格式化

std::string FormatDuration(double hours) {
    if (hours <= 0) return u8"未知";
    const long long totalMinutes =
        static_cast<long long>(std::llround(hours * 60.0));
    const long long days = totalMinutes / 1440;
    const long long rem = totalMinutes % 1440;
    const long long hrs = rem / 60;
    const long long mins = rem % 60;

    char buf[96];
    if (days > 0) {
        if (hrs) std::snprintf(buf, sizeof(buf), u8"约 %lld 天 %lld 小时",
                               days, hrs);
        else std::snprintf(buf, sizeof(buf), u8"约 %lld 天", days);
    } else if (hrs > 0) {
        if (mins) std::snprintf(buf, sizeof(buf), u8"约 %lld 小时 %lld 分",
                                hrs, mins);
        else std::snprintf(buf, sizeof(buf), u8"约 %lld 小时", hrs);
    } else {
        std::snprintf(buf, sizeof(buf), u8"约 %lld 分钟", mins);
    }
    return buf;
}

// ---------------------------------------------------------------- 自检

int RunSelfTest() {
    util::Print(u8"\n=== 电量历史与聚合自检 ===\n");
    int fails = 0;
    auto check = [&](bool ok, const char* what) {
        util::Print(ok ? u8"  ✓ %s\n" : u8"  ✗ %s\n", what);
        if (!ok) ++fails;
    };

    const double now = NowUnix();
    History h(":memory:");

    // 造数据：过去 6 小时，每小时 2 个样本，电量从 90 缓降
    // 第 3 小时故意留空（模拟鼠标休眠，读不到）
    //
    // **必须锚定到整点**，不能写成 now - hour*3600 - 600：
    // 那样当 now 位于整点后不足 10 分钟时，`-600` 的样本会掉进**上一个**
    // 小时桶，导致"第 3 小时为空"的断档断言随机失败。
    // 实测就是这个原因：18:50 运行时通过，20:05 运行时报 3 项失败。
    // 做法是先求出当前小时起点，再按整点偏移，样本放在该小时内的固定位置。
    {
        const time_t tn = static_cast<time_t>(now);
        struct tm ltn{};
        localtime_s(&ltn, &tn);
        ltn.tm_min = 0;
        ltn.tm_sec = 0;
        ltn.tm_isdst = -1;
        const double thisHour = static_cast<double>(std::mktime(&ltn));

        auto& s = h.mutable_samples();
        for (int hour = 5; hour >= 0; --hour) {
            if (hour == 3) continue;        // 空桶
            const double base = thisHour - hour * 3600.0;
            const int pct = 90 - (5 - hour) * 2;
            // 该小时内的第 10 分钟与第 50 分钟，绝不可能越界到相邻小时
            Sample a; a.t = base + 600; a.percent = pct; a.charging = false;
            Sample b; b.t = base + 3000; b.percent = pct; b.charging = false;
            s.push_back(a);
            s.push_back(b);
        }
    }

    util::Print(u8"\n[1] 按小时聚合（6 小时窗口）\n");
    const auto buckets = h.RecentHours(6, now);
    check(buckets.size() == 6, u8"返回 6 个桶（含空桶）");
    int empty = 0, nonEmpty = 0;
    for (const auto& b : buckets) {
        char ts[32];
        const time_t tt = static_cast<time_t>(b.hourStart);
        struct tm lt{};
        localtime_s(&lt, &tt);
        std::snprintf(ts, sizeof(ts), "%02d:%02d", lt.tm_hour, lt.tm_min);
        if (b.count == 0) {
            ++empty;
            util::Print(u8"    %s  （无数据）\n", ts);
        } else {
            ++nonEmpty;
            util::Print(u8"    %s  %d 个样本  %d%%~%d%%  末值 %d%%\n", ts,
                        b.count, b.minPercent, b.maxPercent, b.lastPercent);
        }
    }
    util::Print(u8"    有数据 %d 桶，空 %d 桶\n", nonEmpty, empty);
    check(nonEmpty == 5, u8"5 个小时有数据");
    check(empty == 1, u8"1 个小时为空（断档被如实反映）");

    // 末位桶必须是"当前小时"。
    //
    // **不能**放宽成"允许 1 小时误差"：正是因为放宽，才让
    // "标签整体偏移 8 小时"的真 bug 逃过检查。严格断言才能锁住时区换算。
    {
        const int64_t lastStart = buckets.back().hourStart;
        const int64_t nowI = static_cast<int64_t>(now);
        const int64_t diff = nowI - lastStart;
        check(diff >= 0 && diff < 3600,
              u8"最后一个桶正是当前小时（0 <= 差 < 3600 秒）");
        if (!(diff >= 0 && diff < 3600)) {
            util::Print(u8"      差 %lld 秒\n", static_cast<long long>(diff));
        }
    }

    // 桶标签必须升序、间隔正好 1 小时、整体跨越 5 小时
    {
        bool ordered = true, spaced = true;
        for (size_t i = 0; i + 1 < buckets.size(); ++i) {
            if (buckets[i + 1].hourStart <= buckets[i].hourStart)
                ordered = false;
            if (buckets[i + 1].hourStart - buckets[i].hourStart != 3600)
                spaced = false;
        }
        check(ordered, u8"桶按时间升序");
        check(spaced, u8"相邻桶相差正好 1 小时");
        const int64_t span =
            buckets.back().hourStart - buckets.front().hourStart;
        check(span == 5 * 3600, u8"6 个桶跨越 5 小时");
    }

    util::Print(u8"\n[2] 统计（24 小时窗口）\n");
    const auto sum = h.Analyze(24, now);
    util::Print(u8"    样本 %d  跨度 %.1f 小时  %d%%~%d%%  "
                u8"有数据 %d/%d 小时  充电样本 %d\n",
                sum.sampleCount, sum.spanHours, sum.minPercent,
                sum.maxPercent, sum.hoursWithData, sum.hoursCovered,
                sum.chargedSamples);
    check(sum.sampleCount == 10, u8"统计到 10 个样本");
    check(sum.minPercent == 80 && sum.maxPercent == 90,
          u8"电量范围 80~90");
    check(sum.hoursWithData == 5, u8"有数据的小时数 5");
    check(sum.chargedSamples == 0, u8"无充电样本");

    util::Print(u8"\n[3] 空小时数（0 小时窗口）\n");
    check(h.RecentHours(0, now).empty(), u8"0 小时窗口返回空");

    util::Print(u8"\n[4] 预测（需要足够降幅）\n");
    Estimate est;
    const bool has = h.EstimateRemaining(80, est);
    util::Print(u8"    有预测 %s\n", has ? u8"是" : u8"否（数据不足）");
    check(true, u8"预测调用不崩溃");

    util::Print(u8"\n");
    if (fails) {
        util::Print(u8"历史聚合自检失败 %d 项\n", fails);
        return 1;
    }
    util::Print(u8"历史聚合自检全部通过 ✓\n");
    return 0;
}

}  // namespace battery
