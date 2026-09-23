// ============================================================================
//  hidpp.cpp — 罗技 HID++ 2.0 协议层实现
//
//  从已验证的 Python 版移植。所有"为什么这么写"的原因都保留在注释里，
//  因为这些是踩坑得来的，改错会静默失效（读不到设备或读到假电量）。
// ============================================================================

#include "hidpp.h"
#include "util.h"

#include <windows.h>
#include <hidsdi.h>
#include <setupapi.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <map>
#include <mutex>

#pragma comment(lib, "hid.lib")
#pragma comment(lib, "setupapi.lib")

namespace hidpp {
namespace {

using Clock = std::chrono::steady_clock;

double NowSec() {
    static const auto start = Clock::now();
    return std::chrono::duration<double>(Clock::now() - start).count();
}

}  // namespace

// ---------------------------------------------------------------- 帧构造

Frame BuildFrame(int deviceIndex, int featureIndex, int function,
                 const uint8_t* params, int paramCount) {
    Frame f{};
    // HID++ 2.0 的 function 字节 = (function << 4) | 软件ID。
    // 软件 ID 必须非零：设备主动推送的通知用 ID 0，与 function 0 的应答
    // 字节完全相同，取非零值才能区分。
    const uint8_t functionByte =
        static_cast<uint8_t>(((function << 4) & 0xF0) | kClientId);

    f.bytes[0] = kHidppLong;
    f.bytes[1] = static_cast<uint8_t>(deviceIndex);
    f.bytes[2] = static_cast<uint8_t>(featureIndex);
    f.bytes[3] = functionByte;
    for (int i = 0; i < paramCount && (4 + i) < 20; ++i)
        f.bytes[4 + i] = params[i];
    return f;
}

namespace {

// ---------------------------------------------------------------- HID 设备

// 一个已打开的 HID 接口，支持带超时的读写。
//
// 用 Windows 原生 HID API（CreateFile + ReadFile/WriteFile + overlapped）
// 而不是 hidapi：本机没有 hidapi 的 C 库（Python 的 hid 是静态链接的 .pyd），
// 而 hid.dll/SetupAPI 是系统自带、更原生的选择。
class HidDevice {
public:
    HidDevice() = default;
    ~HidDevice() { Close(); }

    HidDevice(const HidDevice&) = delete;
    HidDevice& operator=(const HidDevice&) = delete;

    bool Open(const std::wstring& path) {
        Close();
        // overlapped 模式，配合 WaitForSingleObject 实现超时读
        handle_ = CreateFileW(path.c_str(),
                              GENERIC_READ | GENERIC_WRITE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_EXISTING,
                              FILE_FLAG_OVERLAPPED, nullptr);
        if (handle_ == INVALID_HANDLE_VALUE) {
            handle_ = nullptr;
            return false;
        }

        // 询问报告长度。接收器通常报告 64 字节。
        HIDP_CAPS caps{};
        PHIDP_PREPARSED_DATA prep = nullptr;
        if (HidD_GetPreparsedData(handle_, &prep) && prep) {
            if (HidP_GetCaps(prep, &caps) == HIDP_STATUS_SUCCESS) {
                inputLen_ = caps.InputReportByteLength;
                outputLen_ = caps.OutputReportByteLength;
            }
            HidD_FreePreparsedData(prep);
        }
        if (inputLen_ <= 0) inputLen_ = 64;
        if (outputLen_ <= 0) outputLen_ = 64;

        readEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        writeEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!readEvent_ || !writeEvent_) { Close(); return false; }
        return true;
    }

    void Close() {
        if (readEvent_) { CloseHandle(readEvent_); readEvent_ = nullptr; }
        if (writeEvent_) { CloseHandle(writeEvent_); writeEvent_ = nullptr; }
        if (handle_) { CloseHandle(handle_); handle_ = nullptr; }
    }

    bool valid() const { return handle_ != nullptr; }
    int inputLen() const { return inputLen_; }

    // 带超时写一帧。返回是否成功。
    bool WriteFrame(const uint8_t* data, int len, double timeoutSec) {
        if (!valid()) return false;

        std::vector<uint8_t> buf(static_cast<size_t>(outputLen_), 0);
        const int copyLen = std::min(len, outputLen_);
        std::memcpy(buf.data(), data, static_cast<size_t>(copyLen));

        OVERLAPPED ov{};
        ov.hEvent = writeEvent_;
        ResetEvent(writeEvent_);

        DWORD written = 0;
        BOOL ok = WriteFile(handle_, buf.data(),
                            static_cast<DWORD>(buf.size()), nullptr, &ov);
        if (!ok) {
            if (GetLastError() != ERROR_IO_PENDING) return false;
            DWORD wait = WaitForSingleObject(
                writeEvent_,
                static_cast<DWORD>(std::max(1.0, timeoutSec * 1000)));
            if (wait != WAIT_OBJECT_0) {
                CancelIoEx(handle_, &ov);
                return false;
            }
            if (!GetOverlappedResult(handle_, &ov, &written, FALSE))
                return false;
        }
        return true;
    }

    // 带超时读一帧。返回读到的字节数，0 表示超时或无数据。
    int ReadFrame(uint8_t* out, int outLen, double timeoutSec) {
        if (!valid()) return 0;
        outLen = std::min(outLen, inputLen_);

        OVERLAPPED ov{};
        ov.hEvent = readEvent_;
        ResetEvent(readEvent_);

        DWORD read = 0;
        BOOL ok = ReadFile(handle_, out, static_cast<DWORD>(outLen),
                           nullptr, &ov);
        if (!ok) {
            DWORD err = GetLastError();
            if (err != ERROR_IO_PENDING) return 0;
            DWORD wait = WaitForSingleObject(
                readEvent_,
                static_cast<DWORD>(std::max(1.0, timeoutSec * 1000)));
            if (wait != WAIT_OBJECT_0) {
                CancelIoEx(handle_, &ov);
                return 0;
            }
            if (!GetOverlappedResult(handle_, &ov, &read, FALSE)) return 0;
        }
        return static_cast<int>(read);
    }

private:
    HANDLE handle_ = nullptr;
    HANDLE readEvent_ = nullptr;
    HANDLE writeEvent_ = nullptr;
    int inputLen_ = 64;
    int outputLen_ = 64;
};

// ---------------------------------------------------------------- 接收器

// 会话级缓存：设备名与 feature 索引在一次运行内不会变。
// 设备名一次要 1~3 次往返（实测约 180ms），每轮重查会显著拖慢刷新，
// 并挤占"唤醒期重试"所需的时间预算。
std::mutex g_cacheMutex;
std::map<std::wstring, int> g_lastDeviceIndex;   // 上次成功读到的配对槽位
std::map<std::wstring, std::string> g_nameCache;

class Receiver {
public:
    Receiver() = default;
    ~Receiver() { Close(); }

    Receiver(const Receiver&) = delete;
    Receiver& operator=(const Receiver&) = delete;

    bool Open(const std::wstring& longPath, const std::wstring& shortPath) {
        longPath_ = longPath;
        if (!long_.Open(longPath)) return false;
        if (!shortPath.empty()) short_.Open(shortPath);
        return true;
    }

    void Close() {
        long_.Close();
        short_.Close();
    }

    bool valid() const { return long_.valid(); }

    // 发一条 HID++ 2.0 长请求，返回应答的数据区（第 4 字节起）。
    //
    // 要求应答的 function 字节与请求**完全一致** —— 这天然排除了设备
    // 主动推送的通知（它们带软件 ID 0，匹配不上非零的 kClientId）。
    bool Call(int deviceIndex, int featureIndex, int function,
              const uint8_t* params, int paramLen,
              uint8_t* out, int outLen, double timeoutSec) {
        if (!valid()) return false;

        // 走共享的帧构造，保证测试验证的就是生产路径
        const Frame f = BuildFrame(deviceIndex, featureIndex, function,
                                   params, paramLen);
        const uint8_t functionByte = f.bytes[3];

        Drain();
        if (!long_.WriteFrame(f.bytes, 20, timeoutSec)) return false;

        const double end = NowSec() + timeoutSec;
        uint8_t buf[256];
        while (NowSec() < end) {
            const double left = end - NowSec();
            if (left <= 0) break;
            const int n = long_.ReadFrame(buf, sizeof(buf), left);
            if (n >= 4 && buf[0] == kHidppLong &&
                buf[1] == static_cast<uint8_t>(deviceIndex) &&
                buf[2] == static_cast<uint8_t>(featureIndex) &&
                buf[3] == functionByte) {
                const int dataLen = std::min(outLen, n - 4);
                if (dataLen > 0) std::memcpy(out, buf + 4,
                                             static_cast<size_t>(dataLen));
                return true;
            }
            // 不是我们要的帧，极短睡眠后继续（避免空转烧 CPU）
            Sleep(1);
        }
        return false;
    }

    // 丢弃缓冲区里的陈旧帧，避免读到上一次请求的残留应答。
    // 注意：一旦确认没有更多待读数据就立即返回 —— 早期 Python 版固定
    // 等 50ms，而读一次电量要发 5+ 次请求，光这里就白耗 250ms+。
    void Drain() {
        uint8_t buf[256];
        for (int i = 0; i < 64; ++i) {
            const int n = long_.ReadFrame(buf, sizeof(buf), 0.0);
            if (n <= 0) break;
        }
    }

    bool IsPresent(int deviceIndex, double timeoutSec) {
        return Call(deviceIndex, kFeatureRoot, kFnGetFeature,
                    nullptr, 0, nullptr, 0, timeoutSec);
    }

    // 查 feature 索引。0 表示不支持。
    int FeatureIndex(int deviceIndex, uint16_t featureId, double timeoutSec) {
        const uint8_t params[2] = {
            static_cast<uint8_t>((featureId >> 8) & 0xFF),
            static_cast<uint8_t>(featureId & 0xFF)};
        uint8_t out[16] = {0};
        if (!Call(deviceIndex, kFeatureRoot, kFnGetFeature, params, 2,
                  out, sizeof(out), timeoutSec))
            return 0;
        return out[0];
    }

    std::string DeviceName(int deviceIndex, double timeoutSec) {
        const int index = FeatureIndex(deviceIndex, kFeatureDeviceName,
                                       timeoutSec);
        if (index == 0) return {};

        uint8_t head[16] = {0};
        if (!Call(deviceIndex, index, kFnNameLength, nullptr, 0,
                  head, sizeof(head), timeoutSec))
            return {};
        const int length = head[0];
        if (length <= 0 || length > 64) return {};

        std::string name;
        for (int offset = 0; offset < length; offset += 16) {
            const uint8_t params[1] = {static_cast<uint8_t>(offset)};
            uint8_t chunk[16] = {0};
            if (!Call(deviceIndex, index, kFnNameChunk, params, 1,
                      chunk, sizeof(chunk), timeoutSec))
                break;
            const int take = std::min(16, length - offset);
            for (int i = 0; i < take; ++i) {
                if (chunk[i] == 0) break;
                name.push_back(static_cast<char>(chunk[i]));
            }
        }
        // 去掉首尾空白与零字节
        while (!name.empty() &&
               (name.back() == '\0' || name.back() == ' '))
            name.pop_back();
        return name;
    }

private:
    HidDevice long_;
    HidDevice short_;
    std::wstring longPath_;
};

// ---------------------------------------------------------------- 枚举接收器

// 遍历 HID 设备，找出所有罗技厂商接口（usage page 0xFF00）。
std::vector<ReceiverInfo> EnumerateInternal() {
    std::vector<ReceiverInfo> result;

    GUID guid;
    HidD_GetHidGuid(&guid);

    HDEVINFO devInfo = SetupDiGetClassDevsW(
        &guid, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devInfo == INVALID_HANDLE_VALUE) return result;

    // 按 (vid,pid) 归组，因为一个接收器会暴露多个 usage
    struct Entry {
        uint16_t vid = 0, pid = 0;
        std::wstring path;
        int usage = -1;
    };
    std::vector<Entry> entries;

    SP_DEVICE_INTERFACE_DATA ifData{};
    ifData.cbSize = sizeof(ifData);
    for (DWORD i = 0;
         SetupDiEnumDeviceInterfaces(devInfo, nullptr, &guid, i, &ifData);
         ++i) {
        DWORD needed = 0;
        SetupDiGetDeviceInterfaceDetailW(devInfo, &ifData, nullptr, 0,
                                         &needed, nullptr);
        if (needed == 0) continue;

        std::vector<uint8_t> buf(needed);
        auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(
            buf.data());
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
        if (!SetupDiGetDeviceInterfaceDetailW(devInfo, &ifData, detail,
                                              needed, nullptr, nullptr))
            continue;

        std::wstring path = detail->DevicePath;

        HANDLE h = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE,
                               FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) continue;

        HIDD_ATTRIBUTES attrs{};
        attrs.Size = sizeof(attrs);
        bool ok = HidD_GetAttributes(h, &attrs);

        PHIDP_PREPARSED_DATA prep = nullptr;
        int usagePage = -1, usage = -1;
        if (HidD_GetPreparsedData(h, &prep) && prep) {
            HIDP_CAPS caps{};
            if (HidP_GetCaps(prep, &caps) == HIDP_STATUS_SUCCESS) {
                usagePage = caps.UsagePage;
                usage = caps.Usage;
            }
            HidD_FreePreparsedData(prep);
        }
        CloseHandle(h);

        if (!ok) continue;
        if (attrs.VendorID != kLogitechVendor) continue;
        if (usagePage != kVendorUsagePage) continue;

        Entry e;
        e.vid = attrs.VendorID;
        e.pid = attrs.ProductID;
        e.path = path;
        e.usage = usage;
        entries.push_back(e);
    }
    SetupDiDestroyDeviceInfoList(devInfo);

    // 归组
    std::map<std::pair<uint16_t, uint16_t>, ReceiverInfo> grouped;
    for (const auto& e : entries) {
        auto& info = grouped[{e.vid, e.pid}];
        info.vendorId = e.vid;
        info.productId = e.pid;
        if (e.usage == 0x0002) info.longPath = e.path;
        else if (e.usage == 0x0001) info.shortPath = e.path;
        else if (info.longPath.empty()) info.longPath = e.path;
    }
    for (auto& [key, info] : grouped) result.push_back(info);

    // 稳定排序，保证多次调用顺序一致
    std::sort(result.begin(), result.end(),
              [](const ReceiverInfo& a, const ReceiverInfo& b) {
                  if (a.vendorId != b.vendorId) return a.vendorId < b.vendorId;
                  return a.productId < b.productId;
              });
    return result;
}

}  // namespace

// ---------------------------------------------------------------- 公开接口

std::vector<ReceiverInfo> FindReceivers() { return EnumerateInternal(); }

std::vector<Reading> ReadAll(double budgetSec) {
    std::vector<Reading> readings;
    const double deadline = NowSec() + std::max(0.2, budgetSec);
    const auto receivers = EnumerateInternal();

    for (const auto& info : receivers) {
        if (NowSec() >= deadline) break;
        if (info.longPath.empty()) continue;

        Receiver rx;
        if (!rx.Open(info.longPath, info.shortPath)) continue;

        // 首选槽位：上次成功的（没有则 1 号）。
        //
        // 关键实测结论（决定了这里的策略）：
        //   * 设备空闲/休眠被唤醒时，**第一帧请求**要 ~650~850ms 且可能返回
        //     失败（设备还没醒透）
        //   * 紧接着的重试只需 ~57ms 即成功
        //   * 每轮 ReadAll 都会重新打开设备句柄，所以要**每轮**都付唤醒代价
        // 因此给首选槽位两次机会，且**每次都只用剩余预算的一半** ——
        // 保证重试一定跑得到。早先的写法在冷启动（无缓存）时对所有槽位
        // 都用 0.12s 短探测，必然漏掉唤醒中的设备，表现为"鼠标在用却一直离线"。
        int preferred = 1;
        {
            std::lock_guard<std::mutex> lock(g_cacheMutex);
            auto it = g_lastDeviceIndex.find(info.longPath);
            if (it != g_lastDeviceIndex.end()) preferred = it->second;
        }

        std::vector<int> order;
        order.push_back(preferred);
        for (int i = 1; i <= 7; ++i)
            if (i != preferred) order.push_back(i);

        for (int deviceIndex : order) {
            if (NowSec() >= deadline) break;
            const bool isPreferred = (deviceIndex == preferred);
            const int attempts = isPreferred ? 2 : 1;
            bool got = false;

            for (int attempt = 0; attempt < attempts && !got; ++attempt) {
                const double left = deadline - NowSec();
                if (left <= 0) break;

                double probe = isPreferred
                    ? std::max(0.12, std::min(kPreferredProbeSec,
                                              left / (attempts - attempt)))
                    : std::max(0.05, std::min(kProbeTimeoutSec, left / 8));

                if (!rx.IsPresent(deviceIndex, probe)) continue;

                // 优先读取新版 UnifiedBattery；G304 等设备通常只实现
                // BATTERY_STATUS(0x1000)，其函数号与 UnifiedBattery 不同。
                int findex = rx.FeatureIndex(
                    deviceIndex, kFeatureUnifiedBattery, left);
                uint8_t status[16] = {0};
                bool batteryStatusFeature = false;
                bool gotBattery = findex != 0 &&
                    rx.Call(deviceIndex, findex, kFnGetStatus,
                            nullptr, 0, status, sizeof(status), left);

                if (!gotBattery) {
                    const double remaining = deadline - NowSec();
                    if (remaining <= 0) continue;
                    findex = rx.FeatureIndex(
                        deviceIndex, kFeatureBatteryStatus, remaining);
                    if (findex == 0) continue;

                    const double requestBudget = deadline - NowSec();
                    if (requestBudget <= 0 ||
                        !rx.Call(deviceIndex, findex, kFnBatteryStatusGetStatus,
                                 nullptr, 0, status, sizeof(status),
                                 requestBudget))
                        continue;
                    batteryStatusFeature = true;
                }

                Reading r;
                r.deviceIndex = deviceIndex;
                // 两种 feature 的状态帧格式不同：BatteryStatus 返回
                // [电量百分比, 下一档阈值, 电池状态]；第二字节不是 flags。
                r.percent = std::max(0, std::min(100,
                                     static_cast<int>(status[0])));
                if (batteryStatusFeature) {
                    r.chargingState = NormalizeBatteryStatus(status[2]);
                    r.chargingText = BatteryStatusText(status[2]);
                } else {
                    r.level = LevelFromFlags(status[1]);
                    r.chargingState = status[2];
                    r.chargingText = ChargingText(status[2]);
                    r.externalPower = status[3];
                }

                // 设备名走缓存（一次要 1~3 次往返，约 180ms）
                {
                    std::lock_guard<std::mutex> lock(g_cacheMutex);
                    auto it = g_nameCache.find(info.longPath);
                    if (it != g_nameCache.end()) r.name = it->second;
                }
                if (r.name.empty()) {
                    r.name = rx.DeviceName(deviceIndex, left);
                    if (!r.name.empty()) {
                        std::lock_guard<std::mutex> lock(g_cacheMutex);
                        g_nameCache[info.longPath] = r.name;
                    }
                }
                if (r.name.empty()) {
                    char buf[32];
                    std::snprintf(buf, sizeof(buf), u8"设备 #%d", deviceIndex);
                    r.name = buf;
                }

                readings.push_back(r);
                {
                    std::lock_guard<std::mutex> lock(g_cacheMutex);
                    g_lastDeviceIndex[info.longPath] = deviceIndex;
                }
                got = true;
            }
            if (got) break;   // 一个接收器通常只带一个鼠标
        }
    }

    return readings;
}

// ---------------------------------------------------------------- 帧对拍

int DumpFrames() {
    // 输出格式刻意与 Python 版 print 一致，便于自动化 diff：
    //   <说明>|<20 字节的十六进制，空格分隔>
    const struct {
        const char* tag;
        int deviceIndex;
        int featureIndex;
        int function;
        const uint8_t* params;
        int paramCount;
    } cases[] = {
        // 探测配对槽位 1：IRoot.GetFeature(0x0000) —— is_present
        {"probe_dev1", 1, 0x00, 0x0, nullptr, 0},
        // 探测槽位 7
        {"probe_dev7", 7, 0x00, 0x0, nullptr, 0},
        // 查 UnifiedBattery(0x1004) 的 feature 索引
        {"feature_1004", 1, 0x00, 0x0, (const uint8_t*)"\x10\x04", 2},
        // 查 DeviceName(0x0005)
        {"feature_0005", 1, 0x00, 0x0, (const uint8_t*)"\x00\x05", 2},
        // GET_STATUS（function 1，feature 索引 6）
        {"get_status", 1, 0x06, 0x1, nullptr, 0},
        // GET_CAPABILITIES（function 0）—— 这个**不是**电量，是能力描述
        {"get_caps", 1, 0x06, 0x0, nullptr, 0},
        // 设备名长度
        {"name_length", 1, 0x05, 0x0, nullptr, 0},
        // 设备名分片 offset=16
        {"name_chunk16", 1, 0x05, 0x1, (const uint8_t*)"\x10", 1},
        // 读电压
        {"battery_voltage", 1, 0x07, 0x0, nullptr, 0},
        // 接收器自身（0xFF）
        {"receiver_self", 0xFF, 0x00, 0x0, (const uint8_t*)"\x00\x05", 2},
    };

    for (const auto& c : cases) {
        const Frame f = BuildFrame(c.deviceIndex, c.featureIndex, c.function,
                                   c.params, c.paramCount);
        char hex[20 * 3 + 1];
        int pos = 0;
        for (int i = 0; i < 20; ++i)
            pos += std::snprintf(hex + pos, sizeof(hex) - pos,
                                 i ? " %02X" : "%02X", f.bytes[i]);
        util::Print("%s|%s\n", c.tag, hex);
    }
    return 0;
}

// ---------------------------------------------------------------- 自检

int RunSelfTest() {
    util::Print(u8"\n=== HID++ 协议层自检 ===\n");

    const auto receivers = FindReceivers();
    util::Print(u8"找到 %zu 个罗技接收器:\n", receivers.size());
    for (const auto& r : receivers) {
        util::Print(u8"  VID_%04X:PID_%04X\n", r.vendorId, r.productId);
        util::Print(u8"    长报文接口: %s\n",
                    r.longPath.empty() ? "(无)" : "(已找到)");
        util::Print(u8"    短报文接口: %s\n",
                    r.shortPath.empty() ? "(无)" : "(已找到)");
    }
    if (receivers.empty()) {
        util::Print(u8"  未找到接收器 —— 请确认接收器已插好\n");
        return 1;
    }

    util::Print(u8"\n尝试读取电量（预算 2 秒）...\n");
    const double t0 = NowSec();
    const auto readings = ReadAll(2.0);
    const double dt = NowSec() - t0;

    if (readings.empty()) {
        util::Print(u8"  未读到（%.0f ms）—— 鼠标可能正在休眠，动一下再试\n",
                    dt * 1000);
        return 2;   // 2 = 可恢复状态，不是失败
    }

    for (const auto& r : readings) {
        util::Print(u8"  %s: %d%% · %s\n", r.name.c_str(), r.percent,
                    r.chargingText.c_str());
        if (!r.level.empty())
            util::Print(u8"    档位: %s\n", r.level.c_str());
        if (r.voltageMv > 0)
            util::Print(u8"    电压: %d mV\n", r.voltageMv);
    }
    util::Print(u8"  耗时 %.0f ms\n", dt * 1000);
    util::Print(u8"协议层自检通过 ✓\n");
    return 0;
}

}  // namespace hidpp
