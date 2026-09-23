// ============================================================================
//  util.h — 通用工具（控制台 UTF-8 输出、时间、路径）
// ============================================================================
#pragma once

// windows.h 会定义 min/max 宏，破坏 std::min/std::max，甚至让函数参数表被
// 误解析（表现为 "语法错误: )" 之类的怪错误）。在任何 Windows 头之前禁掉，
// 作为不依赖编译选项的兜底（build.bat 里也加了 /DNOMINMAX）。
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <cstdint>
#include <string>

namespace util {

// 把控制台切到 UTF-8 并输出。Windows 控制台默认是 GBK，
// 直接 printf 中文会变成乱码 —— 与 Python 版踩过的同类问题。
void InitConsole();

// 格式化输出到控制台（UTF-8 安全）
void Print(const char* fmt, ...);

// 输出字符串并换行
void PrintLine(const std::string& s);

// 自启动以来的秒数（单调，用于超时计算）
double NowSec();

// 自启动以来的毫秒数（单调，用于性能测量）
double NowMs();

// 用户数据目录：%LOCALAPPDATA%\MouseBatteryTray，并确保存在
std::wstring DataDir();
std::string DataDirUtf8();

// UTF-8 <-> UTF-16
std::wstring Utf8ToWide(const std::string& s);
std::string WideToUtf8(const std::wstring& s);

// 按**字符（码点）**数截断 UTF-8 字符串，且保证不切断多字节字符。
// 不要用 std::string::resize 来限长：它数的是字节，会把中文切成半个字，
// 产生非法 UTF-8（托盘 tooltip 会显示成乱码）。
std::string TruncateUtf8Chars(const std::string& s, size_t maxChars);

// 返回 UTF-8 字符串的字符（码点）数
size_t Utf8CharCount(const std::string& s);

}  // namespace util
