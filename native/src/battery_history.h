// ============================================================================
//  battery_history.h — 电量采样历史与续航预测
//
//  从已验证的 Python 版 (battery_history.py) 移植。
//
//  为什么不能简单用「两次读数之差」算放电速度：
//    1. 电量是整数百分比，1% 的跳变本身就有量化噪声
//    2. 鼠标休眠时读不到数据，采样间隔不均匀
//    3. 充电过程会插入反向跳变
//  因此用最小二乘拟合放电斜率，并设两道门槛；达不到就**不给预测值** ——
//  宁可显示"数据积累中"，也不要给一个错的数字。
// ============================================================================
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace battery {

// ---- 预测参数（必须与 Python 版一致，测试会逐项核对）
constexpr double kMinSpanHours      = 0.35;   // 至少跨越 21 分钟
constexpr int    kMinDropPercent    = 3;      // 至少下降 3%
constexpr int    kMaxSampleAgeDays  = 14;     // 超过两周的样本不再参考
constexpr size_t kMaxSamples        = 2000;   // 采样上限，超出丢最旧的
constexpr double kRebuildWindowHours = 72.0;  // 优先用最近 72 小时拟合

struct Sample {
    double t = 0;          // Unix 时间戳（秒）
    int    percent = 0;
    bool   charging = false;
};

struct Estimate {
    double ratePerHour = 0;      // 放电速度（百分点/小时）
    double hoursRemaining = 0;   // 预计还能用多少小时
    double spanHours = 0;        // 拟合所依据的时间跨度
    int    dropPercent = 0;      // 拟合所依据的降幅
    int    sampleCount = 0;
    std::string confidence;      // 高 / 中 / 低
};

// 一个小时的汇总。用于"每个时间段的电量"展示。
//
// 注意：即使电量没有变化（例如现在是恒定的 94%），这个结构仍然有用 ——
// count 反映**采样覆盖率**，能看出哪些时段鼠标在休眠（读不到数据）。
struct HourBucket {
    int64_t hourStart = 0;   // 该小时的起点（Unix 秒，已按本地时间对齐）
    int minPercent = 0;
    int maxPercent = 0;
    int lastPercent = 0;     // 该小时最后一次读数（用于画阶梯线）
    int count = 0;           // 该小时的样本数（0 表示这一小时没有数据）
    bool anyCharging = false;
};

class History {
public:
    explicit History(const std::string& pathUtf8);

    // 记录一个采样点。返回是否真的写入了。
    // 与上次采样间隔不足 20 秒且充电状态未变时会被忽略（重复读数无意义）。
    bool Add(int percent, bool charging, double nowUnix);

    // 基于历史放电段拟合斜率并预测。数据不足时返回 false。
    bool EstimateRemaining(int currentPercent, Estimate& out) const;

    // 取最近 countHours 个小时的汇总（含没有数据的空桶）。
    // 返回的向量按时间升序，最后一个元素是"当前小时"。
    // nowUnix <= 0 时用当前时间。
    std::vector<HourBucket> RecentHours(int countHours,
                                        double nowUnix = 0) const;

    // 整体统计（用于"简单分析"）
    struct Summary {
        int sampleCount = 0;
        double spanHours = 0;
        int minPercent = 0;
        int maxPercent = 0;
        int lastPercent = 0;
        int firstPercent = 0;
        int chargedSamples = 0;
        int hoursWithData = 0;      // 有样本的小时数
        int hoursCovered = 0;       // 统计窗口内的小时数
    };
    Summary Analyze(int windowHours = 24, double nowUnix = 0) const;

    void Load();
    void Save() const;

    const std::vector<Sample>& samples() const { return samples_; }
    size_t size() const { return samples_.size(); }

    // 测试用：直接塞样本
    std::vector<Sample>& mutable_samples() { return samples_; }

private:
    std::vector<std::vector<Sample>> DischargeSegments() const;
    static bool LeastSquaresRate(const std::vector<Sample>& seg, double& out);
    static std::string Confidence(double span, int drop, int count);
    void Prune(double nowUnix);

    std::string path_;
    std::vector<Sample> samples_;
};

// 把小时数格式化成"约 3 天 5 小时"这样的中文描述
std::string FormatDuration(double hours);

// 当前 Unix 时间戳（秒）
double NowUnix();

// 自检：用合成数据验证按小时聚合与统计
int RunSelfTest();

}  // namespace battery
