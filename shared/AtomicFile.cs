using System;
using System.IO;
using System.Text;

namespace MouseBatteryTray;

/// <summary>
/// 覆盖写文本文件，并保证「磁盘上要么是旧内容、要么是完整的新内容」。
///
/// 为什么需要它：设置与历史原先都是 <c>File.WriteAllText</c> 直接往目标文件写。
/// 写到一半崩溃 / 断电 / 被强制结束，文件里就只剩前半截 JSON；而三处读取路径
/// 都是 <c>catch { }</c> 静默回落到默认值 —— 用户不会看到任何提示，只会发现
/// 开机自启、阈值、图标样式全部回到出厂，历史记录里的「睡前电量」也一起消失。
///
/// 做法是经典的临时文件 + 原子替换：先写同目录下的 <c>.tmp</c>（同卷才可能
/// 原子替换），再 <c>File.Replace</c> 覆盖目标。写失败时目标文件一个字节都没动。
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// 原子地覆盖 <paramref name="path"/>。
    /// <paramref name="encoding"/> 省略时用**无 BOM** 的 UTF-8
    /// （带 BOM 会让后续 JSON 解析失败，这个坑 settings.json 已经踩过一次）。
    /// </summary>
    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(false);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, contents, encoding);

        try
        {
            if (File.Exists(path))
            {
                // 同卷替换：NTFS 上是原子的，读者要么看到旧文件、要么看到新文件
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (IOException)
        {
            // 极少数文件系统不支持 Replace（或目标在我们判断后被删掉）：
            // 退回「覆盖式 Move」，仍比直接 WriteAllText 安全 —— 此时新内容
            // 已经完整落在临时文件里了。
            File.Move(temp, path, overwrite: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temp, path, overwrite: true);
        }
    }
}
