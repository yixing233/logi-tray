// ============================================================================
//  config.h — 用户配置的读写
//
//  从已验证的 Python 版 (settings.py) 移植。
//  配置存到 %LOCALAPPDATA%\MouseBatteryTray\settings.json。
//  任何读写失败都退回默认值，**绝不因为配置问题让程序起不来**。
// ============================================================================
#pragma once

#include <string>

namespace config {

// 材质名（与 glass_effects.MATERIALS 对应）
std::string NormalizeMaterial(const std::string& name);

struct Settings {
    // 默认值（必须与 Python 版 DEFAULTS 一致）
    int interval = 30;             // 轮询间隔（秒）
    int lowThreshold = 20;         // 低电量提醒阈值
    int criticalThreshold = 10;    // 严重低电量阈值
    bool notifyEnabled = true;     // 是否启用通知
    bool autostart = false;        // 开机自启（权威值在注册表，这里只作缓存）
    int staleGrace = 300;          // 离线后仍显示最后读数的宽限（秒）
    std::string material = "acrylic";   // liquid / acrylic / mica
    bool realtime = true;          // 实时材质
    int realtimeFps = 30;          // 实时渲染帧率上限

    std::string path;

    // 读取（失败时保留默认值）
    void Load(const std::string& pathUtf8);
    // 写入（失败静默，不影响运行）
    void Save() const;

    // 类型与范围守卫：配置被手改坏了也不至于崩
    void Sanitise();
};

// 默认配置文件路径：%LOCALAPPDATA%\MouseBatteryTray\settings.json
std::string DefaultPath();

}  // namespace config
