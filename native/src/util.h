// ============================================================================
//  util.h — UTF-8 console output for the native HID++ reader
// ============================================================================
#pragma once

namespace util {

// Set the Windows console code pages to UTF-8.
void InitConsole();

// Print formatted UTF-8 text to stdout.
void Print(const char* fmt, ...);

}  // namespace util
