// ============================================================================
//  hidpp.cpp — 罗技 HID++ 2.0 协议层实现
//
//  从已验证的 Python 版移植。所有"为什么这么写"的原因都保留在注释里，
//  因为这些是踩坑得来的，改错会静默失效（读不到设备或读到假电量）。
// ============================================================================

#include "hidpp.h"
#include "battery.h"
#include "util.h"

#include <windows.h>
#include <hidsdi.h>
#include <setupapi.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <tuple>

#pragma comment(lib, "hid.lib")
#pragma comment(lib, "setupapi.lib")

namespace hidpp {
namespace {

using Clock = std::chrono::steady_clock;

double NowSec() {
    static const auto start = Clock::now();
    return std::chrono::duration<double>(Clock::now() - start).count();
}

// 诊断开关：设置 MOUSE_TRAY_DEBUG=1 时打印短集合读取细节。
//
// 保留原因：短集合是「接收器主动回绝不可达槽位」的唯一通道，一旦现场再出现
// 「空槽位吃掉预算」或「短帧读不到」的问题，只有这行输出能区分
// 「接收器没回」和「我们没读到」。默认关闭时不产生任何输出。
bool DebugEnabled() {
    static const bool on = [] {
        char buf[8] = {0};
        const DWORD n = GetEnvironmentVariableA("MOUSE_TRAY_DEBUG", buf,
                                                sizeof(buf));
        return n > 0 && buf[0] != '0';
    }();
    return on;
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
        // 关句柄会顺带取消挂起的读，但要把状态清干净，免得复用对象时误判。
        readPending_ = false;
        readBuf_.clear();
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
    //
    // 实现要点（踩坑得来，改错会静默丢帧）：**读请求要一直挂着**。
    //
    // 早先的实现是「每次调用发一个 ReadFile，超时就 CancelIoEx 收尾」。那在
    // 本机是致命的：HID 输入报告到达时只会投递给**当前挂在队列上的**那个 IRP。
    // 超时取消 → 重新排队之间存在空窗，恰好落在空窗里的报告（接收器的 1.0
    // 错误帧在 3~5ms 内就到达）会被丢掉且永不重发。
    // 实测对照（_native_repro.py）：用 hidapi 的等价序列 12/12 命中，而
    // CancelIoEx 版几乎每次都读到 0 字节 —— 差别就在有没有取消。
    //
    // 因此这里改为：ReadFile 只发一次，之后每次调用只用 WaitForSingleObject
    // 等已有的事件；超时**不取消**，下次继续等同一个 IRP。等到数据后再挂下一个。
    int ReadFrame(uint8_t* out, int outLen, double timeoutSec) {
        if (!valid()) return 0;
        outLen = std::min(outLen, inputLen_);

        if (!readPending_) {
            readOv_ = OVERLAPPED{};
            readOv_.hEvent = readEvent_;
            ResetEvent(readEvent_);
            readBuf_.assign(static_cast<size_t>(inputLen_), 0);
            DWORD read = 0;
            const BOOL ok = ReadFile(handle_, readBuf_.data(),
                                     static_cast<DWORD>(readBuf_.size()),
                                     nullptr, &readOv_);
            if (!ok) {
                const DWORD err = GetLastError();
                if (err != ERROR_IO_PENDING) return 0;
            } else {
                // 同步完成（罕见）：数据已在 readBuf_。
                readPending_ = false;
                const int n = static_cast<int>(read);
                const int copy = std::min(outLen, n);
                std::memcpy(out, readBuf_.data(), static_cast<size_t>(copy));
                return copy;
            }
            readPending_ = true;
        }

        const DWORD wait = WaitForSingleObject(
            readEvent_, static_cast<DWORD>(std::max(1.0, timeoutSec * 1000)));
        if (wait != WAIT_OBJECT_0) return 0;   // 不取消：保留这个挂起的读

        DWORD read = 0;
        const BOOL done = GetOverlappedResult(handle_, &readOv_, &read, FALSE);
        readPending_ = false;
        if (!done) return 0;
        const int n = static_cast<int>(read);
        const int copy = std::min(outLen, n);
        if (copy > 0) std::memcpy(out, readBuf_.data(),
                                  static_cast<size_t>(copy));
        return copy;
    }

private:
    HANDLE handle_ = nullptr;
    HANDLE readEvent_ = nullptr;
    HANDLE writeEvent_ = nullptr;
    int inputLen_ = 64;
    int outputLen_ = 64;

    // 常驻的挂起读：见 ReadFrame 的注释。readPending_ 为真时 readOv_ 正挂在
    // 队列上，readBuf_ 是它写入的目标缓冲。
    bool readPending_ = false;
    OVERLAPPED readOv_{};
    std::vector<uint8_t> readBuf_;
};

// ---------------------------------------------------------------- 接收器

// 会话级缓存：设备名与 feature 索引在一次运行内不会变。
// 设备名一次要 1~3 次往返（实测约 180ms），每轮重查会显著拖慢刷新，
// 并挤占"唤醒期重试"所需的时间预算。
std::mutex g_cacheMutex;
std::map<std::wstring, int> g_lastDeviceIndex;   // 上次成功读到的配对槽位

// 设备名缓存按「路径 + 槽位」索引。
// 只按路径索引是错的：一个 Unifying 接收器可以同时带多台设备，
// 那样第二台设备会显示成第一台的名字。
std::map<std::pair<std::wstring, int>, std::string> g_nameCache;

// 已知在线的配对槽位（按接收器路径）。
//
// 一个接收器可以同时带多台设备，但探测空槽位必须等超时（实测约 300ms），
// 每轮都扫满 7 个槽位会白白吃掉整个时间预算，让单设备用户的读数变慢。
// 因此记住「哪些槽位确实有设备」，之后只扫这些槽位，
// 并在预算充裕时额外探测一个未知槽位以发现新设备。
std::map<std::wstring, std::vector<int>> g_presentSlots;

// 已确认没有设备的槽位，避免反复探测。
std::map<std::wstring, std::vector<int>> g_absentSlots;

// 每隔多少轮做一次全量槽位发现，以便发现新配对的设备。
// 取值权衡：太频繁会反复为空槽位付超时代价；太大则新设备要等较久才被看到。
constexpr int kRediscoverEvery = 20;

// ReadAll 调用计数，用于决定本轮是否做全量发现。
std::atomic<int> g_pollCounter{0};


// feature 索引在一次会话内不会变（设备不会中途换特性表），
// 缓存后可省掉每轮 1~2 次 HID 往返。键 = 路径 + 槽位 + 特性 ID。
//
// 只缓存"查到"的结果（index != 0）。查不到可能是设备正在休眠而非真的不支持，
// 把失败也缓存下来会让设备永久被判定为不支持该特性。
std::map<std::tuple<std::wstring, int, uint16_t>, int> g_featureCache;

// 0x1004 的能力字节同样在一次会话内不变，缓存它可省掉每轮一次往返
// （约 57ms）。同样只缓存成功读到的结果，失败不缓存。
std::map<std::tuple<std::wstring, int, uint16_t>, std::array<uint8_t, 16>>
    g_capsCache;

// ------------------------------------------------- 跨进程的配对槽位提示
//
// 为什么需要：应用以 `--once` 每轮新起一个进程（BatteryService.cs 里
// Process.Start），g_lastDeviceIndex 这类进程内缓存**跨轮次归零**，
// 于是每一轮都从 1 号槽位开始盲试。设备若配在别的槽位，就要等前面的槽位
// 全部超时之后才轮到它 —— 而它恰恰可能正处于最需要那段时间的唤醒过程中。
//
// 写在哪：可执行文件旁边的 `mouse-tray.slot-hint`（GetModuleFileNameW 定位）。
// 三个版本各自复制了自己的 mouse-tray.exe，因此天然互不干扰；开发态下
// 共用 native\build\mouse-tray.exe，三个版本共用一份也无害 —— 它只是
// 「先试哪个槽位」的排序提示，猜错只是多花几十毫秒，不影响正确性。
//
// 刻意只存一个数字：它测错了也不影响功能，不值得为它引入解析、迁移与容错。
std::wstring SlotHintPath() {
    wchar_t buf[MAX_PATH] = {0};
    const DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return std::wstring();
    std::wstring path(buf, n);
    const size_t slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return std::wstring();
    return path.substr(0, slash + 1) + L"mouse-tray.slot-hint";
}

int LoadSlotHint() {
    const std::wstring path = SlotHintPath();
    if (path.empty()) return 0;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return 0;

    char buf[16] = {0};
    DWORD read = 0;
    const BOOL ok = ReadFile(h, buf, sizeof(buf) - 1, &read, nullptr);
    CloseHandle(h);
    if (!ok || read == 0) return 0;

    const int slot = std::atoi(buf);
    return (slot >= 1 && slot <= 7) ? slot : 0;
}

void SaveSlotHint(int slot) {
    if (slot < 1 || slot > 7) return;
    const std::wstring path = SlotHintPath();
    if (path.empty()) return;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE,
                           FILE_SHARE_READ, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    char buf[16];
    const int len = std::snprintf(buf, sizeof(buf), "%d", slot);
    DWORD written = 0;
    if (len > 0) WriteFile(h, buf, static_cast<DWORD>(len), &written, nullptr);
    CloseHandle(h);
}

// 一次请求的结果。
//
// 为什么不是简单的 bool：本机实测发现，目标设备不可达时接收器会在 3~5ms 内
// 从短集合回一条 HID++ 1.0 错误帧（判定见 battery.h 的短报文一节）。把它和
// 「什么都没收到」分开，预算分配才有依据 —— 只有后者值得继续等。
enum class CallStatus {
    Ok,       // 拿到本次请求的 2.0 长应答
    Absent,   // 接收器明确回了 1.0 错误帧：该槽位本轮不必再等
    Timeout,  // 什么应答都没有（设备可能正在唤醒，值得继续等）
};

struct CallOutcome {
    CallStatus status = CallStatus::Timeout;
    int        shortError = -1;   // 仅 Absent 时有意义（1.0 错误码）
};

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
        return CallEx(deviceIndex, featureIndex, function, params, paramLen,
                      out, outLen, timeoutSec, true).status == CallStatus::Ok;
    }

    // Call 的完整版：额外回报「接收器明确说该设备不可达」。
    //
    // 为什么要同时轮询短集合：本机实测（_col_probe.py，三次独立复现）——
    // 把 20 字节 2.0 请求写进长集合后
    //   * 若目标设备可达，应答是长集合上的 20 字节 0x11 帧；
    //   * 若目标设备不可达，接收器在 3~5ms 内从**短集合**回一条 7 字节
    //     0x10 错误帧（形状见 battery.h 的短报文一节）。
    // 只轮询长集合的实现永远收不到第二种应答，于是每个空槽位都要白等满整个
    // 探测窗口 —— 7 个槽位就吃掉 1.6 秒，真正在唤醒的设备反而没时间应答。
    //
    // watchShort 打开时会先给短集合一个极短窗口（不可达时 3~5ms 就有结果），
    // 然后**一次性**读完长集合的剩余时间。刻意不做 20ms 轮转切片：
    // 反复 CancelIoEx 会把长应答（唤醒时要 650~850ms）打断。
    CallOutcome CallEx(int deviceIndex, int featureIndex, int function,
                       const uint8_t* params, int paramLen,
                       uint8_t* out, int outLen, double timeoutSec,
                       bool watchShort) {
        CallOutcome outcome;
        if (!valid()) return outcome;

        // 走共享的帧构造，保证测试验证的就是生产路径
        const Frame f = BuildFrame(deviceIndex, featureIndex, function,
                                   params, paramLen);
        const uint8_t functionByte = f.bytes[3];

        Drain();
        if (!long_.WriteFrame(f.bytes, 20, timeoutSec)) return outcome;

        const double end = NowSec() + timeoutSec;

        // 先看短集合。窗口取 timeoutSec 的一小部分，短探测时不至于被它吃掉。
        //
        // 这里按 3ms 小片反复读，与已验证的 Python 探针一致
        // （h.read(64, timeout_ms=3)）：接收器对不可达槽位的回绝只需 3~5ms，
        // 小片读能让判定尽早返回，而不是先白等一整个长窗口。
        //
        // 注意：能稳定收到这一帧的前提是 HidDevice::ReadFrame 采用**常驻挂起读**
        // —— 早期实现每次调用发一个 ReadFile、超时就 CancelIoEx 收尾，
        // 取消/重建 IRP 之间的空窗恰好会丢掉这时到达的报告，导致短口恒读回 0 字节。
        // 详见 ReadFrame 的注释。
        if (watchShort && short_.valid()) {
            const double left0 = end - NowSec();
            int sn = 0;
            uint8_t sbuf[64];
            if (left0 > 0) {
                const double win = std::min(kPollSliceSec, left0 * 0.25);
                const double shortEnd = NowSec() + win;
                for (;;) {
                    const double left = shortEnd - NowSec();
                    if (left <= 0) break;
                    sn = short_.ReadFrame(sbuf, sizeof(sbuf),
                                          std::min(kShortReadSliceSec, left));
                    if (sn > 0) break;
                }
                if (DebugEnabled()) {
                    util::Print(u8"[dbg] CallEx dev=%d feat=0x%02X fn=0x%02X "
                                u8"short读回 %d 字节 win=%.1fms\n",
                                deviceIndex, featureIndex, functionByte, sn,
                                win * 1000);
                }
                if (sn > 0) {
                    int err = -1;
                    if (battery::MatchShortError(sbuf, sn, deviceIndex,
                                                 featureIndex, functionByte,
                                                 &err)) {
                        // 接收器已明确回答：这个槽位本轮不必再等了。
                        outcome.status = CallStatus::Absent;
                        outcome.shortError = err;
                        return outcome;
                    }
                    // 不是本次请求的应答（可能是别的槽位的迟到帧或通知），
                    // 放着不管，继续等长集合上的真应答。
                }
            }
        }

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
                outcome.status = CallStatus::Ok;
                return outcome;
            }
            // 不是我们要的帧，极短睡眠后继续（避免空转烧 CPU）
            Sleep(1);
        }
        outcome.status = CallStatus::Timeout;
        return outcome;
    }

    // 丢弃缓冲区里的陈旧帧，避免读到上一次请求的残留应答。
    // 注意：一旦确认没有更多待读数据就立即返回 —— 早期 Python 版固定
    // 等 50ms，而读一次电量要发 5+ 次请求，光这里就白耗 250ms+。
    //
    // 两个集合都要排空：短集合上积压的 1.0 错误帧若不丢掉，下一次请求会把
    // 它误当成自己的应答（那条帧只镜像 featureIndex/functionByte，同槽位的
    // 连续探测字节完全相同，光看这两个字段分不出来）。
    void Drain() {
        uint8_t buf[256];
        for (int i = 0; i < 64; ++i) {
            const int n = long_.ReadFrame(buf, sizeof(buf), 0.0);
            if (n <= 0) break;
        }
        if (short_.valid()) {
            for (int i = 0; i < 64; ++i) {
                const int n = short_.ReadFrame(buf, sizeof(buf), 0.0);
                if (n <= 0) break;
            }
        }
    }

    // 探测某个槽位是否有设备。
    //
    // 除了 bool 还回报 Absent —— 接收器说"这个槽位没有设备"时，
    // ReadAll 可以把唤醒窗口让给别的槽位。
    CallOutcome ProbePresence(int deviceIndex, double timeoutSec) {
        return CallEx(deviceIndex, kFeatureRoot, kFnGetFeature,
                      nullptr, 0, nullptr, 0, timeoutSec, true);
    }

    bool IsPresent(int deviceIndex, double timeoutSec) {
        return ProbePresence(deviceIndex, timeoutSec).status == CallStatus::Ok;
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

    // 带缓存的特性查找。命中缓存不消耗时间预算。
    int CachedFeatureIndex(int deviceIndex, uint16_t featureId,
                           double timeoutSec) {
        const auto key = std::make_tuple(longPath_, deviceIndex, featureId);
        {
            std::lock_guard<std::mutex> lock(g_cacheMutex);
            auto it = g_featureCache.find(key);
            if (it != g_featureCache.end()) return it->second;
        }

        const int index = FeatureIndex(deviceIndex, featureId, timeoutSec);
        if (index != 0) {
            std::lock_guard<std::mutex> lock(g_cacheMutex);
            g_featureCache[key] = index;
        }
        return index;
    }

    // 读 0x1004 的能力字节（带缓存）。
    // 能力字节决定 status[0] 是百分比还是无意义的占位值，必须拿到才能正确解析；
    // 但它一次会话内不变，缓存后可省掉每轮一次 HID 往返（约 57ms）。
    bool CachedCapabilities(int deviceIndex, int featureIndex,
                            uint8_t* out, double timeoutSec) {
        const auto key = std::make_tuple(
            longPath_, deviceIndex, static_cast<uint16_t>(kFeatureUnifiedBattery));
        {
            std::lock_guard<std::mutex> lock(g_cacheMutex);
            auto it = g_capsCache.find(key);
            if (it != g_capsCache.end()) {
                std::memcpy(out, it->second.data(), it->second.size());
                return true;
            }
        }

        std::array<uint8_t, 16> buf{};
        if (!Call(deviceIndex, featureIndex, kFnGetCapabilities, nullptr, 0,
                  buf.data(), static_cast<int>(buf.size()), timeoutSec))
            return false;

        {
            std::lock_guard<std::mutex> lock(g_cacheMutex);
            g_capsCache[key] = buf;
        }
        std::memcpy(out, buf.data(), buf.size());
        return true;
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

// 以下四个包装把实现委托给 battery.cpp 的纯解析层。
// 这样文字表与状态映射只有一处定义，且能被 --test-battery 覆盖。
std::string ChargingText(int state) { return battery::ChargingText(state); }
std::string BatteryStatusText(int status) { return battery::BatteryStatusText(status); }
int NormalizeBatteryStatus(int status) { return battery::NormalizeBatteryStatus(status); }
bool IsChargingState(int state) { return battery::IsChargingState(state); }
std::string LevelFromFlags(int bits) { return battery::LevelFromFlags(bits); }

std::vector<ReceiverInfo> FindReceivers() { return EnumerateInternal(); }

std::vector<Reading> ReadAll(double budgetSec, bool scanAllDevices) {
    std::vector<Reading> readings;
    const double deadline = NowSec() + std::max(0.2, budgetSec);
    const auto receivers = EnumerateInternal();

    g_pollCounter.fetch_add(1);

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
        // 进程内没有记录（--once 的常态）时，用上一轮留下的槽位提示。
        // 这只是"先试哪个"的排序提示：猜错只多花几十毫秒，不会读错设备。
        if (preferred == 1) {
            const int hint = LoadSlotHint();
            if (hint != 0) preferred = hint;
        }

        std::vector<int> order;
        order.push_back(preferred);

        // 槽位发现结果缓存在 g_presentSlots / g_absentSlots 里。
        //
        // 为什么要缓存：这是给 --probe / --all 这类**同一进程内多次 ReadAll**
        // 的调用省时间的（多设备枚举时不必每轮重扫已知空槽位）。首次做全量
        // 发现，之后优先扫已知在线的槽位；每 kRediscoverEvery 轮再做一次
        // 全量发现，以便发现新配对的设备。
        //
        // 注意：fail-fast 之后全量发现本身已经很便宜（空槽位 3~5ms 被回绝），
        // 所以这份缓存只是锦上添花；绝不能把它当成"永久拉黑"的依据 ——
        // 设备随时可能换槽位，下一轮 rediscover 必须仍然全量重试。
        const bool rediscover = (g_pollCounter % kRediscoverEvery) == 0;

        std::vector<int> knownPresent;
        std::vector<int> knownAbsent;
        {
            std::lock_guard<std::mutex> lock(g_cacheMutex);
            auto it = g_presentSlots.find(info.longPath);
            if (it != g_presentSlots.end()) knownPresent = it->second;
            auto it2 = g_absentSlots.find(info.longPath);
            if (it2 != g_absentSlots.end()) knownAbsent = it2->second;
        }

        auto isKnownAbsent = [&](int slot) {
            return std::find(knownAbsent.begin(), knownAbsent.end(), slot) !=
                   knownAbsent.end();
        };
        auto isKnownPresent = [&](int slot) {
            return std::find(knownPresent.begin(), knownPresent.end(), slot) !=
                   knownPresent.end();
        };

        // 已知在线的槽位优先（多设备情形下能一次扫全）
        for (int slot : knownPresent) {
            if (slot != preferred) order.push_back(slot);
        }
        // 其余槽位：非重发现轮次跳过已确认空的槽位
        for (int i = 1; i <= 7; ++i) {
            if (i == preferred || isKnownPresent(i)) continue;
            if (!rediscover && isKnownAbsent(i)) continue;
            order.push_back(i);
        }

        // ---- 第一遍：轻探 ----
        //
        // 这里的做法从"给每个槽位留够等超时的时间"改成"两遍探测"，原因是
        // 发现了接收器的一个行为：目标设备不可达时，接收器会在**短报文口**
        // 用 1.0 错误帧在 3~5ms 内回绝我们（CallEx 识别出来即 CallStatus::Absent）。
        //
        // 于是：
        //   * 明确回 Absent 的槽位 → 立即淘汰，不再浪费任何时间；
        //   * 只有 Timeout 才是"设备可能正在唤醒、值得继续等"的信号。
        //
        // 好处是扫 7 个空槽位的总代价从"每个都等满超时 ≈1.6 秒"降到几十毫秒，
        // 省下来的预算全部留给真正在唤醒中的那一个槽位。
        std::vector<int> online;
        std::vector<int> timedOut;
        for (int deviceIndex : order) {
            if (NowSec() >= deadline) break;
            const bool isPreferred = (deviceIndex == preferred);
            // 首选槽位直接给完整唤醒窗口：它就是上次成功的那一个。
            const double quick = isPreferred ? kPatientProbeSec : kQuickProbeSec;
            const CallOutcome oc = rx.ProbePresence(deviceIndex, quick);
            if (oc.status == CallStatus::Ok) {
                online.push_back(deviceIndex);
            } else if (oc.status == CallStatus::Timeout) {
                timedOut.push_back(deviceIndex);
            } else {
                // 明确不在线：记入空槽位名单，后续轮次可跳过
                std::lock_guard<std::mutex> lock(g_cacheMutex);
                auto& absent = g_absentSlots[info.longPath];
                if (std::find(absent.begin(), absent.end(), deviceIndex) ==
                    absent.end()) {
                    absent.push_back(deviceIndex);
                }
            }
        }

        // ---- 第二遍：耐心等只轻探超时的槽位 ----
        //
        // 超时才是"可能正在唤醒"的唯一信号。首选槽位在第一遍已经等过一个
        // 完整窗口（实测第一帧可能失败、紧接着的重试只要 ~57ms），这里给
        // 较短的兜底窗口即可；其余槽位第一遍只轻探过，值得给完整唤醒窗口。
        for (int deviceIndex : timedOut) {
            const double left = deadline - NowSec();
            if (left <= 0) break;
            const bool isPreferred = (deviceIndex == preferred);
            double patient = isPreferred ? kMinPatientProbeSec : kPatientProbeSec;
            patient = std::max(0.05, std::min(patient, left));

            const CallOutcome oc = rx.ProbePresence(deviceIndex, patient);
            if (oc.status == CallStatus::Ok) {
                online.push_back(deviceIndex);
            } else {
                // 两遍都没应答：按空槽位处理。
                // 只是"下一轮 rediscover 还会再全量试一次"，不会永久拉黑。
                std::lock_guard<std::mutex> lock(g_cacheMutex);
                auto& absent = g_absentSlots[info.longPath];
                if (std::find(absent.begin(), absent.end(), deviceIndex) ==
                    absent.end()) {
                    absent.push_back(deviceIndex);
                }
            }
        }

        // ---- 读电量 ----
        //
        // 只对确认在线的槽位做这一长串往返（一次要 4~6 个来回）。
        bool got = false;
        for (int deviceIndex : online) {
            if (NowSec() >= deadline) break;

            // 该槽位确认在线：从"空"名单里移除，并记入在线名单
            {
                std::lock_guard<std::mutex> lock(g_cacheMutex);
                auto& absent = g_absentSlots[info.longPath];
                absent.erase(std::remove(absent.begin(), absent.end(),
                                         deviceIndex), absent.end());
            }
            const double left = deadline - NowSec();
            if (left <= 0) break;

            // ---- 依次尝试各条电量读取路径 ----
            //
            // 不同年代/系列的罗技设备实现的电量特性并不相同，因此这里
            // 按「信息量优先」的顺序回退，任意一条成功即采纳：
            //
            //   0x1004 UNIFIED_BATTERY   首选。支持百分比或仅档位两种模式，
            //                            需要先读 capabilities 才能判断。
            //   0x1000 BATTERY_STATUS    老设备（G304 等）走这条。
            //   0x0104 CENTURION_SOC     较新的 Centurion 系列。
            //   0x1001 BATTERY_VOLTAGE   只有电压，需按放电曲线换算百分比。
            //   0x1F20 ADC_MEASUREMENT   另一路电压测量。
            //
            // 每条路径都只花剩余预算的一小部分，保证后面还有机会尝试。
            battery::Parsed parsed;
            uint16_t usedFeature = 0;

            // 单次调用允许占用的最大时间片：剩余预算的一半，
            // 但要留出至少 60ms 给后续路径，避免第一条就把预算吃光。
            auto slice = [&]() {
                const double left = deadline - NowSec();
                if (left <= 0) return 0.0;
                return std::max(0.05, std::min(left * 0.5, left - 0.02));
            };

            // 1) 0x1004 UnifiedBattery
            {
                const int findex = rx.CachedFeatureIndex(
                    deviceIndex, kFeatureUnifiedBattery, slice());
                if (findex != 0) {
                    uint8_t caps[16] = {0};
                    const bool haveCaps = rx.CachedCapabilities(
                        deviceIndex, findex, caps, slice());

                    uint8_t status[16] = {0};
                    if (rx.Call(deviceIndex, findex, kFnGetStatus,
                                nullptr, 0, status, sizeof(status), slice())) {
                        parsed = battery::ParseUnifiedBattery(
                            status, static_cast<int>(sizeof(status)),
                            haveCaps ? caps : nullptr,
                            haveCaps ? static_cast<int>(sizeof(caps)) : 0);
                        if (parsed.valid) usedFeature = kFeatureUnifiedBattery;
                    }
                }
            }

            // 2) 0x1000 BatteryStatus（G304 等）
            if (!parsed.valid) {
                const int findex = rx.CachedFeatureIndex(
                    deviceIndex, kFeatureBatteryStatus, slice());
                if (findex != 0) {
                    uint8_t status[16] = {0};
                    if (rx.Call(deviceIndex, findex,
                                kFnBatteryStatusGetStatus, nullptr, 0,
                                status, sizeof(status), slice())) {
                        parsed = battery::ParseBatteryStatus(
                            status, static_cast<int>(sizeof(status)));
                        if (parsed.valid) usedFeature = kFeatureBatteryStatus;
                    }
                }
            }

            // 3) 0x0104 CenturionBatterySOC
            if (!parsed.valid) {
                const int findex = rx.CachedFeatureIndex(
                    deviceIndex, kFeatureCenturionSoc, slice());
                if (findex != 0) {
                    uint8_t data[16] = {0};
                    if (rx.Call(deviceIndex, findex, kFnGetCapabilities,
                                nullptr, 0, data, sizeof(data), slice())) {
                        parsed = battery::ParseCenturionSoc(
                            data, static_cast<int>(sizeof(data)));
                        if (parsed.valid) usedFeature = kFeatureCenturionSoc;
                    }
                }
            }

            // 4) 0x1001 BatteryVoltage
            if (!parsed.valid) {
                const int findex = rx.CachedFeatureIndex(
                    deviceIndex, kFeatureBatteryVoltage, slice());
                if (findex != 0) {
                    uint8_t data[16] = {0};
                    if (rx.Call(deviceIndex, findex, kFnGetCapabilities,
                                nullptr, 0, data, sizeof(data), slice())) {
                        parsed = battery::ParseBatteryVoltage(
                            data, static_cast<int>(sizeof(data)));
                        if (parsed.valid) usedFeature = kFeatureBatteryVoltage;
                    }
                }
            }

            // 5) 0x1F20 ADC_MEASUREMENT
            if (!parsed.valid) {
                const int findex = rx.CachedFeatureIndex(
                    deviceIndex, kFeatureAdcMeasurement, slice());
                if (findex != 0) {
                    uint8_t data[16] = {0};
                    if (rx.Call(deviceIndex, findex, kFnGetCapabilities,
                                nullptr, 0, data, sizeof(data), slice())) {
                        parsed = battery::ParseAdcMeasurement(
                            data, static_cast<int>(sizeof(data)));
                        if (parsed.valid) usedFeature = kFeatureAdcMeasurement;
                    }
                }
            }

            if (!parsed.valid) continue;

            Reading r;
            r.deviceIndex = deviceIndex;
            r.percent = std::max(0, std::min(100, parsed.percent));
            r.level = parsed.level;
            r.chargingState = parsed.chargingState;
            r.chargingText = parsed.chargingText;
            r.externalPower = parsed.externalPower;
            r.voltageMv = parsed.voltageMv;
            r.sourceFeature = usedFeature;
            r.percentInferred = parsed.percentInferred;

            // 设备名走缓存（一次要 1~3 次往返，约 180ms）。
            // 键含槽位：一个接收器可带多台设备，只按路径缓存会串名。
            const auto nameKey =
                std::make_pair(info.longPath, deviceIndex);
            {
                std::lock_guard<std::mutex> lock(g_cacheMutex);
                auto it = g_nameCache.find(nameKey);
                if (it != g_nameCache.end()) r.name = it->second;
            }
            if (r.name.empty()) {
                r.name = rx.DeviceName(deviceIndex, left);
                if (!r.name.empty()) {
                    std::lock_guard<std::mutex> lock(g_cacheMutex);
                    g_nameCache[nameKey] = r.name;
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

                // 记下这个槽位确实有设备，后续轮次可优先扫描
                auto& present = g_presentSlots[info.longPath];
                if (std::find(present.begin(), present.end(), deviceIndex) ==
                    present.end()) {
                    present.push_back(deviceIndex);
                }
            }

            // 把成功的槽位写到进程外：--once 每轮新进程，进程内缓存
            // 跨轮次归零，下一轮就会从 1 号槽位重新盲试。
            SaveSlotHint(deviceIndex);
            got = true;

            // 找到一台设备后是否继续扫其余槽位。
            //
            // 应用轮询走 --once（每轮新进程），缓存无法跨轮次保留，
            // 继续扫空槽位只会白白增加每次轮询的耗时，因此默认不再继续。
            // --probe / --all 传 scanAllDevices=true 以枚举同一接收器上的多台设备。
            if (got && !scanAllDevices) {
                break;
            }

            if (got) {
                const double remain = deadline - NowSec();
                if (remain < 0.20) break;
            }
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

// ---------------------------------------------------------------- 特性探测

int ProbeFeatures() {
    util::Print(u8"\n=== 各设备实现电量特性的情况 ===\n");

    const auto receivers = FindReceivers();
    if (receivers.empty()) {
        util::Print(u8"未找到罗技接收器 —— 请确认接收器已插好\n");
        return 1;
    }

    // 一个接收器可带多台设备，因此这里枚举所有槽位并同时验证读取路径，
    // 明确报告每台设备实际走的是哪条电量特性。
    const auto readings = ReadAll(6.0, /*scanAllDevices=*/true);

    const struct { uint16_t id; const char* label; } kFeatures[] = {
        {kFeatureUnifiedBattery,  "0x1004 UnifiedBattery"},
        {kFeatureBatteryStatus,   "0x1000 BatteryStatus "},
        {kFeatureCenturionSoc,    "0x0104 CenturionSOC  "},
        {kFeatureBatteryVoltage,  "0x1001 BatteryVoltage"},
        {kFeatureAdcMeasurement,  "0x1F20 AdcMeasurement"},
    };

    bool sawAnything = false;

    for (const auto& info : receivers) {
        if (info.longPath.empty()) continue;

        Receiver rx;
        if (!rx.Open(info.longPath, info.shortPath)) continue;

        util::Print(u8"\n接收器 VID_%04X:PID_%04X\n", info.vendorId,
                    info.productId);

        for (int deviceIndex = 1; deviceIndex <= 7; ++deviceIndex) {
            // 用完整的唤醒窗口探测，并把三态结果原样报告出来：
            //   Ok      = 设备在线
            //   Absent  = 接收器明确回"没有这个设备"（短报文 1.0 错误帧，
            //             0x08 UNKNOWN_DEVICE）—— 槽位真的空着
            //   Timeout = 没回错误也没回数据 —— 设备可能正在唤醒
            // 区分后两者是本项目排障的关键：早先两者都表现为"等满超时"，
            // 无法判断到底是设备在唤醒还是槽位空着。
            const CallOutcome oc = rx.ProbePresence(deviceIndex, kPatientProbeSec);
            if (oc.status != CallStatus::Ok) {
                util::Print(u8"  槽位 %d: %s%s\n", deviceIndex,
                            oc.status == CallStatus::Absent
                                ? u8"无设备"
                                : u8"无应答（可能在唤醒）",
                            oc.shortError >= 0 ? u8"（接收器已回绝）" : u8"");
                continue;
            }

            sawAnything = true;
            std::string name = rx.DeviceName(deviceIndex, 0.6);
            util::Print(u8"  槽位 %d: %s\n", deviceIndex,
                        name.empty() ? u8"(未命名设备)" : name.c_str());

            for (const auto& f : kFeatures) {
                const int index = rx.FeatureIndex(deviceIndex, f.id, 0.4);
                util::Print(u8"    %s %s\n", f.label,
                            index != 0 ? u8"支持" : u8"—");
            }
        }

        if (rx.IsPresent(0xFF, 0.25)) {
            util::Print(u8"  接收器自身 (0xFF) 可访问\n");
        }
    }

    if (!readings.empty()) {
        util::Print(u8"\n实际读取结果（每台设备走的路径）:\n");
        for (const auto& r : readings) {
            util::Print(u8"  %s: %d%%%s · %s · 来源 0x%04X", r.name.c_str(),
                        r.percent, r.percentInferred ? u8"（推算）" : u8"",
                        r.chargingText.c_str(), r.sourceFeature);
            if (r.voltageMv > 0) {
                util::Print(u8" · %d mV", r.voltageMv);
            }
            util::Print(u8"\n");
        }
    }

    if (!sawAnything) {
        util::Print(u8"\n未发现已配对的设备 —— 鼠标可能正在休眠，动一下再试\n");
        return 2;
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
        if (r.sourceFeature != 0)
            util::Print(u8"    来源特性: 0x%04X%s\n", r.sourceFeature,
                        r.percentInferred ? u8"（百分比为推算值）" : u8"");
    }
    util::Print(u8"  耗时 %.0f ms\n", dt * 1000);
    util::Print(u8"协议层自检通过 ✓\n");
    return 0;
}

}  // namespace hidpp
