// ============================================================================
//  app.h — 托盘应用主体（把各模块串起来）
//
//  这是 G8「总装」的第一部分：让程序真正能作为托盘应用运行起来。
//  逐条对应 Python 版 mouse_tray.py 的行为：
//    * 定时轮询电量（默认 30 秒）
//    * 读不到时保留最后已知读数，图标显示为"历史值"（灰 + 空心条）
//    * 记录历史并给出续航预测
//    * 低于阈值弹通知（带迟滞）
//    * 左键单击 -> 详情（浮窗尚未实现，先弹消息框占位）
//    * 右键单击 -> 原生菜单
// ============================================================================
#pragma once

#include <string>
#include <vector>

namespace app {

// 运行托盘应用的主循环。返回退出码。
// showDetailsOnStart / showSettingsOnStart: 启动后立即打开对应窗口
// （供集成测试使用）
int RunTrayApp(bool showDetailsOnStart = false,
               bool showSettingsOnStart = false);

// 不开界面，只做一轮读取并打印（等价 Python 的 --once）
int RunOnce();

}  // namespace app
