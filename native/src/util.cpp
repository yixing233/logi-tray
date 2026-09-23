// ============================================================================
//  util.cpp — UTF-8 console output
// ============================================================================

#include "util.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <vector>

namespace util {
namespace {

bool g_consoleReady = false;

}  // namespace

void InitConsole() {
    if (g_consoleReady) return;
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

    std::vector<char> big(static_cast<size_t>(n) + 1);
    va_start(args, fmt);
    std::vsnprintf(big.data(), big.size(), fmt, args);
    va_end(args);
    std::fputs(big.data(), stdout);
    std::fflush(stdout);
}

}  // namespace util
