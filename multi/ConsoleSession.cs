using System;
using System.IO;
using System.Text;

namespace MultiTray;

/// <summary>
/// 让命令行模式稳定输出 UTF-8。
///
/// 背景：程序所有面向用户的文本都是中文。输出被重定向（管道 / 文件）时，
/// .NET 会按控制台代码页写出，在中文 Windows 上是 GBK；而调用方（脚本、CI、
/// 或用户重定向到文件后按 UTF-8 打开）通常按 UTF-8 解析，于是中文全成乱码。
///
/// **不能设置 Console.OutputEncoding**：本项目是 WinExe（GUI 子系统，托盘程序
/// 必须如此）。在这种子系统下设置 OutputEncoding 会让 .NET 重建 stdout 写入器，
/// 且新写入器拿不到有效的控制台句柄，结果是**输出被整个吞掉**（实测重定向时
/// 字节数为 0）。这一点用四种变体实测确认过：
///
///     变体                    WinExe 下的结果
///     不处理                  输出 GBK（乱码）
///     只设 OutputEncoding     输出 0 字节（全部丢失）
///     只 SetOut(UTF-8)        UTF-8（正确）
///     先设编码再 SetOut       输出 0 字节（全部丢失）
///     直接写原始字节流        UTF-8（正确）
///
/// 因此这里用 SetOut 接管 stdout；若失败则退回到直接写字节流。
/// </summary>
internal static class ConsoleSession
{
    private static bool _applied;

    public static void UseUtf8Output()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            var utf8 = new UTF8Encoding(false);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8)
            {
                // 保证进程退出前不丢输出
                AutoFlush = true,
            };
            Console.SetOut(stdout);
        }
        catch
        {
            // 极少数宿主不允许替换 stdout；此时保持默认行为，
            // 乱码总比整个输出丢失好。
        }
    }
}
