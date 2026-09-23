// ============================================================================
//  config.cpp — 配置读写实现
//
//  范围守卫与 Python 版 _sanitise 逐条对应：
//    interval          [5, 3600]
//    low_threshold     [5, 95]
//    critical_threshold[1, low-1]
//    stale_grace       [30, 86400]
//  材质名做容错（用户可能手改成"液态玻璃"）。
// ============================================================================

#include "config.h"
#include "util.h"

#include <windows.h>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <sstream>

namespace config {

namespace {

// 从 JSON 文本里取整数值（找不到/非法则返回 fallback）
int FindInt(const std::string& t, const std::string& key, int fallback) {
    const std::string pat = "\"" + key + "\"";
    size_t p = t.find(pat);
    if (p == std::string::npos) return fallback;
    p = t.find(':', p + pat.size());
    if (p == std::string::npos) return fallback;
    ++p;
    char* end = nullptr;
    const long v = std::strtol(t.c_str() + p, &end, 10);
    if (end == t.c_str() + p) return fallback;
    return static_cast<int>(v);
}

bool FindBool(const std::string& t, const std::string& key, bool fallback) {
    const std::string pat = "\"" + key + "\"";
    size_t p = t.find(pat);
    if (p == std::string::npos) return fallback;
    p = t.find(':', p + pat.size());
    if (p == std::string::npos) return fallback;
    ++p;
    while (p < t.size() && (t[p] == ' ' || t[p] == '\t')) ++p;
    if (t.compare(p, 4, "true") == 0) return true;
    if (t.compare(p, 5, "false") == 0) return false;
    return fallback;
}

// 把 4 位十六进制转成码点
bool Hex4(const std::string& s, size_t pos, unsigned& out) {
    if (pos + 4 > s.size()) return false;
    unsigned v = 0;
    for (int i = 0; i < 4; ++i) {
        const char c = s[pos + i];
        unsigned d;
        if (c >= '0' && c <= '9') d = static_cast<unsigned>(c - '0');
        else if (c >= 'a' && c <= 'f') d = static_cast<unsigned>(c - 'a' + 10);
        else if (c >= 'A' && c <= 'F') d = static_cast<unsigned>(c - 'A' + 10);
        else return false;
        v = v * 16 + d;
    }
    out = v;
    return true;
}

void AppendUtf8(std::string& out, unsigned cp) {
    if (cp < 0x80) {
        out.push_back(static_cast<char>(cp));
    } else if (cp < 0x800) {
        out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else {
        out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
}

// 解码 JSON 字符串字面量（含 \uXXXX 与常见短转义）。
//
// 必须支持 \uXXXX：json.dump 的**默认**行为就是 ensure_ascii=True，
// 会把中文写成 \u6db2\u6001... 这种转义。早先这里直接取原始字节，
// 于是 "material": "\u6db2..." 匹配不上"液态玻璃"，材质被静默退回亚克力
// （由 test_config.py 的"材质中文名"用例发现）。
std::string DecodeJsonString(const std::string& s, size_t begin, size_t end) {
    std::string out;
    out.reserve(end - begin);
    for (size_t i = begin; i < end;) {
        const char c = s[i];
        if (c != '\\') {
            out.push_back(c);
            ++i;
            continue;
        }
        if (i + 1 >= end) break;
        const char e = s[i + 1];
        switch (e) {
            case 'n': out.push_back('\n'); i += 2; break;
            case 't': out.push_back('\t'); i += 2; break;
            case 'r': out.push_back('\r'); i += 2; break;
            case 'b': out.push_back('\b'); i += 2; break;
            case 'f': out.push_back('\f'); i += 2; break;
            case '"': out.push_back('"'); i += 2; break;
            case '\\': out.push_back('\\'); i += 2; break;
            case '/': out.push_back('/'); i += 2; break;
            case 'u': {
                unsigned cp = 0;
                if (!Hex4(s, i + 2, cp)) { i += 2; break; }
                i += 6;
                // 代理对：高位 + 低位合成一个码点
                if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < end &&
                    s[i] == '\\' && s[i + 1] == 'u') {
                    unsigned lo = 0;
                    if (Hex4(s, i + 2, lo) && lo >= 0xDC00 && lo <= 0xDFFF) {
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                        i += 6;
                    }
                }
                AppendUtf8(out, cp);
                break;
            }
            default:
                out.push_back(e);
                i += 2;
                break;
        }
    }
    return out;
}

std::string FindString(const std::string& t, const std::string& key,
                       const std::string& fallback) {
    const std::string pat = "\"" + key + "\"";
    size_t p = t.find(pat);
    if (p == std::string::npos) return fallback;
    p = t.find(':', p + pat.size());
    if (p == std::string::npos) return fallback;
    p = t.find('"', p);
    if (p == std::string::npos) return fallback;

    // 找结束引号，跳过被转义的 \" 
    size_t q = p + 1;
    while (q < t.size()) {
        if (t[q] == '\\') { q += 2; continue; }
        if (t[q] == '"') break;
        ++q;
    }
    if (q >= t.size()) return fallback;
    return DecodeJsonString(t, p + 1, q);
}

}  // namespace

std::string NormalizeMaterial(const std::string& /*name*/) {
    // 目前只提供亚克力一种材质。
    //
    // 液态玻璃与云母已从界面移除（用户要求）。渲染器里仍保留这两条实现路径
    // 且都有测试覆盖，但不再对外暴露 —— 这样旧配置里的 "liquid"/"mica"
    // 会被静默收敛为亚克力，不会因为读到未知值而出问题。
    //
    // 将来若要恢复，只需把下面对应的判断加回来即可。
    return "acrylic";
}

std::string DefaultPath() { return util::DataDirUtf8() + "\\settings.json"; }

void Settings::Sanitise() {
    interval = std::max(5, std::min(3600, interval));
    lowThreshold = std::max(5, std::min(95, lowThreshold));
    // 严重阈值必须低于低电量阈值，至少差 1
    criticalThreshold = std::max(
        1, std::min(lowThreshold - 1, criticalThreshold));
    staleGrace = std::max(30, std::min(86400, staleGrace));
    material = NormalizeMaterial(material);
    realtimeFps = std::max(1, std::min(144, realtimeFps));
}

void Settings::Load(const std::string& pathUtf8) {
    path = pathUtf8.empty() ? DefaultPath() : pathUtf8;

    std::ifstream f(util::Utf8ToWide(path).c_str(), std::ios::binary);
    if (!f) {
        Sanitise();     // 没有配置文件 -> 用默认值
        return;
    }
    std::stringstream ss;
    ss << f.rdbuf();
    const std::string text = ss.str();
    if (text.empty()) {
        Sanitise();
        return;
    }

    interval = FindInt(text, "interval", interval);
    lowThreshold = FindInt(text, "low_threshold", lowThreshold);
    criticalThreshold = FindInt(text, "critical_threshold", criticalThreshold);
    notifyEnabled = FindBool(text, "notify_enabled", notifyEnabled);
    autostart = FindBool(text, "autostart", autostart);
    staleGrace = FindInt(text, "stale_grace", staleGrace);
    material = FindString(text, "material", material);
    realtime = FindBool(text, "realtime", realtime);
    realtimeFps = FindInt(text, "realtime_fps", realtimeFps);

    Sanitise();
}

void Settings::Save() const {
    const std::wstring wp = util::Utf8ToWide(path);
    // 先写临时文件再改名，避免写到一半崩溃留下坏配置
    const std::wstring tmp = wp + L".tmp";
    {
        std::ofstream f(tmp.c_str(), std::ios::binary | std::ios::trunc);
        if (!f) return;
        f << "{\n"
          << "  \"interval\": " << interval << ",\n"
          << "  \"low_threshold\": " << lowThreshold << ",\n"
          << "  \"critical_threshold\": " << criticalThreshold << ",\n"
          << "  \"notify_enabled\": "
          << (notifyEnabled ? "true" : "false") << ",\n"
          << "  \"autostart\": " << (autostart ? "true" : "false") << ",\n"
          << "  \"stale_grace\": " << staleGrace << ",\n"
          << "  \"material\": \"" << material << "\",\n"
          << "  \"material_params\": {},\n"
          << "  \"realtime\": " << (realtime ? "true" : "false") << ",\n"
          << "  \"realtime_fps\": " << realtimeFps << "\n"
          << "}\n";
    }
    MoveFileExW(tmp.c_str(), wp.c_str(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}

}  // namespace config
