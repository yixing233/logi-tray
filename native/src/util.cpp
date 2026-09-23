// ============================================================================
//  util.cpp — 通用工具实现
// ============================================================================

#include "util.h"

#include <windows.h>
#include <shlobj.h>

#include <cstdarg>
#include <cstdio>
#include <chrono>
#include <vector>

namespace util {
namespace {

bool g_consoleReady = false;

}  // namespace

void InitConsole() {
    if (g_consoleReady) return;
    // 把输入/输出代码页都设为 UTF-8，否则中文输出是乱码
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleCP(CP_UTF8);
    g_consoleReady = true;
}

void Print(const char* fmt, ...) {
    InitConsole();

    char stackBuf[2048];
    va_list args;
    va_start(args, fmt);
    const int n = std::vsnprintf(stackBuf, sizeof(stackBuf), fmt, args);
    va_end(args);

    if (n < 0) return;

    if (n < static_cast<int>(sizeof(stackBuf))) {
        std::fputs(stackBuf, stdout);
        std::fflush(stdout);
        return;
    }

    // 超长则动态分配
    std::vector<char> big(static_cast<size_t>(n) + 1);
    va_start(args, fmt);
    std::vsnprintf(big.data(), big.size(), fmt, args);
    va_end(args);
    std::fputs(big.data(), stdout);
    std::fflush(stdout);
}

void PrintLine(const std::string& s) {
    std::fputs(s.c_str(), stdout);
    std::fputc('\n', stdout);
    std::fflush(stdout);
}

double NowSec() {
    static const auto start = std::chrono::steady_clock::now();
    return std::chrono::duration<double>(
               std::chrono::steady_clock::now() - start).count();
}

double NowMs() {
    static const auto start = std::chrono::steady_clock::now();
    return std::chrono::duration<double, std::milli>(
               std::chrono::steady_clock::now() - start).count();
}

std::wstring DataDir() {
    wchar_t* base = nullptr;
    std::wstring dir;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr,
                                       &base)) && base) {
        dir = base;
        dir += L"\\MouseBatteryTray";
        CoTaskMemFree(base);
    } else {
        wchar_t buf[MAX_PATH] = {0};
        if (GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH) > 0) {
            dir = buf;
            dir += L"\\MouseBatteryTray";
        } else {
            dir = L".";
        }
    }
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir;
}

std::string DataDirUtf8() { return WideToUtf8(DataDir()); }

std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(),
                                      static_cast<int>(s.size()),
                                      nullptr, 0);
    if (n <= 0) return {};
    std::wstring out(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()),
                        out.data(), n);
    return out;
}

namespace {

// 返回 UTF-8 序列的字节长度（1~4）；非法首字节按 1 处理，保证前进
int Utf8SeqLen(unsigned char c) {
    if (c < 0x80) return 1;
    if ((c & 0xE0) == 0xC0) return 2;
    if ((c & 0xF0) == 0xE0) return 3;
    if ((c & 0xF8) == 0xF0) return 4;
    return 1;   // 非法字节，按单字节前进，避免死循环
}

}  // namespace

size_t Utf8CharCount(const std::string& s) {
    size_t n = 0;
    for (size_t i = 0; i < s.size();) {
        i += static_cast<size_t>(Utf8SeqLen(static_cast<unsigned char>(s[i])));
        ++n;
    }
    return n;
}

std::string TruncateUtf8Chars(const std::string& s, size_t maxChars) {
    size_t n = 0;
    size_t i = 0;
    while (i < s.size() && n < maxChars) {
        i += static_cast<size_t>(Utf8SeqLen(static_cast<unsigned char>(s[i])));
        ++n;
    }
    // i 落在字符边界上；若最后一个序列超出字符串（被截断的输入），
    // 退回到该序列起点，避免输出半个字符。
    if (i > s.size()) return s;
    return s.substr(0, i);
}

std::string WideToUtf8(const std::wstring& s) {
    if (s.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(),
                                      static_cast<int>(s.size()),
                                      nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string out(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()),
                        out.data(), n, nullptr, nullptr);
    return out;
}

}  // namespace util
