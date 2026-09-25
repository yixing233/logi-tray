using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>
/// 休眠设备的「上次已知电量」记忆层。
///
/// 为什么需要它：无线设备一旦休眠就完全不应答 —— 罗技的 native 读取器
/// 连一台都解析不出来、迈从/ATK 的 HID 接口还在但读写全超时。
/// 此时卡片只能显示「--%」，用户看不出它睡前还剩多少（用户要求补上）。
///
/// 做法：每轮读到**在线且可信**的电量就按 <see cref="DeviceReading.Key"/>
/// 落盘记一笔；下一轮该设备读不到时，把那笔历史回填到
/// <see cref="DeviceReading.LastKnownPercent"/>，卡片用小字显示。
///
/// 刻意不改 <see cref="DeviceReading.Percent"/>：大号数字必须保持「--%」，
/// 因为那是**当前**读数，用历史值顶替会造成「现在还有 42%」的误读。
/// 排序与低电量通知同理，都只看在线读数。
///
/// 落盘位置：%LOCALAPPDATA%\multi-tray\battery-history.json
/// （与 settings.json 同目录，UTF-8 无 BOM —— 带 BOM 会让反序列化失败，
/// 这个坑 settings.json 已经踩过一次）。
/// </summary>
public static class BatteryHistory
{
    /// <summary>超过这个天数的记录不再参与回填，并在下次写入时清掉。</summary>
    public const int MaxAgeDays = 30;

    /// <summary>最多记住多少台设备；超了按时间淘汰最旧的。</summary>
    private const int MaxEntries = 64;

    /// <summary>同一台设备两次落盘的间隔下限，避免每轮刷新都写盘。</summary>
    private static readonly TimeSpan MinSaveInterval = TimeSpan.FromMinutes(10);

    /// <summary>一台设备的最后一条记录。</summary>
    public sealed class Entry
    {
        [JsonPropertyName("percent")]
        public int Percent { get; set; } = -1;

        /// <summary>采集时间（本地时间）。</summary>
        [JsonPropertyName("at")]
        public DateTime At { get; set; }
    }

    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _cache;

    public static string FilePath =>
        Path.Combine(AppIdentity.DataDirectory, "battery-history.json");

    /// <summary>
    /// 记住在线设备的电量，并给休眠设备回填历史值。
    /// 直接改传入的 <paramref name="readings"/>（由调用方随即交给界面）。
    /// </summary>
    public static void Apply(List<DeviceReading> readings)
    {
        if (readings == null || readings.Count == 0) return;

        DateTime now = DateTime.Now;
        lock (Gate)
        {
            var map = EnsureLoaded();
            if (ApplyTo(map, readings, now)) SaveLocked(map);
        }
    }

    /// <summary>
    /// 纯逻辑主体（不碰文件），便于用合成数据做穷尽自检。
    /// 返回是否需要落盘。
    /// </summary>
    internal static bool ApplyTo(Dictionary<string, Entry> map,
                                 List<DeviceReading> readings,
                                 DateTime now)
    {
        bool dirty = Prune(map, now);

        // 1) 在线且读到电量的设备：记一笔
        foreach (var r in readings)
        {
            if (!r.IsOnline || r.Percent < 0 || string.IsNullOrEmpty(r.Key)) continue;

            if (map.TryGetValue(r.Key, out var e) && e.Percent == r.Percent &&
                now - e.At < MinSaveInterval)
            {
                continue;   // 电量没变且刚记过，不重复写盘
            }

            map[r.Key] = new Entry { Percent = r.Percent, At = now };
            dirty = true;
        }

        // 2) 休眠设备：回填历史
        foreach (var r in readings)
        {
            if (r.IsOnline || r.Percent >= 0) continue;

            var e = Lookup(map, r.Key, now);
            if (e == null) continue;

            r.LastKnownPercent = e.Percent;
            r.LastKnownAt = e.At;
        }

        return dirty;
    }

    /// <summary>取某台设备最近一次记录（含唯一候选回退），过期返回 null。</summary>
    private static Entry? Lookup(Dictionary<string, Entry> map, string key, DateTime now)
    {
        if (string.IsNullOrEmpty(key)) return null;

        if (map.TryGetValue(key, out var exact) && Fresh(exact, now)) return exact;

        // 回退：罗技鼠标一休眠，native 读取器就一台都解析不出来，
        // Key 从 "logitech:PRO X Wireless" 退化成 "logitech:unknown"，
        // 与在线时记下的键对不上。迈从/ATK 的键基于 HID 接口路径，
        // 换一个 USB 口也会变。这里按「同一来源」再找一次 ——
        // 但**只在只有一个候选时**才用：多个设备就无从判断是哪一个，
        // 宁可不显示也不能张冠李戴。
        string prefix = ProviderPrefix(key);
        if (prefix.Length == 0) return null;

        Entry? only = null;
        foreach (var kv in map)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!Fresh(kv.Value, now)) continue;
            if (only != null) return null;   // 出现第二个候选，放弃
            only = kv.Value;
        }
        return only;
    }

    /// <summary>"logitech:xxx" → "logitech:"；没有冒号则返回空串（不参与回退）。</summary>
    private static string ProviderPrefix(string key)
    {
        int i = key.IndexOf(':');
        return i <= 0 ? "" : key.Substring(0, i + 1);
    }

    private static bool Fresh(Entry e, DateTime now) =>
        e.Percent >= 0 && now - e.At <= TimeSpan.FromDays(MaxAgeDays);

    /// <summary>清掉过期记录与超量条目。返回是否有条目被移除。</summary>
    private static bool Prune(Dictionary<string, Entry> map, DateTime now)
    {
        bool removed = false;

        foreach (string key in map.Keys.ToList())
        {
            if (!Fresh(map[key], now))
            {
                map.Remove(key);
                removed = true;
            }
        }

        if (map.Count > MaxEntries)
        {
            foreach (var kv in map.OrderBy(x => x.Value.At).ToList())
            {
                if (map.Count <= MaxEntries) break;
                map.Remove(kv.Key);
                removed = true;
            }
        }

        return removed;
    }

    /// <summary>把「上次电量」渲染成卡片上的小字，例如「上次 77% · 2 小时前」。</summary>
    public static string Describe(int percent, DateTime? at, DateTime now)
        => at.HasValue
            ? $"上次 {percent}% · {FormatAge(at.Value, now)}"
            : $"上次 {percent}%";

    /// <summary>相对时长，例如「刚刚」「5 分钟前」「3 小时前」「2 天前」。</summary>
    public static string FormatAge(DateTime at, DateTime now)
    {
        TimeSpan d = now - at;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} 小时前";
        return $"{(int)d.TotalDays} 天前";
    }

    // ───────────── 落盘 ─────────────

    private static Dictionary<string, Entry> EnsureLoaded()
    {
        if (_cache != null) return _cache;

        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            string path = FilePath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                    File.ReadAllText(path));
                if (loaded != null)
                {
                    foreach (var kv in loaded)
                    {
                        if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                            map[kv.Key] = kv.Value;
                    }
                }
            }
        }
        catch
        {
            // 历史文件损坏不该影响读数：丢掉记忆重新开始
            map.Clear();
        }

        _cache = map;
        return _cache;
    }

    private static void SaveLocked(Dictionary<string, Entry> map)
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(map,
                new JsonSerializerOptions { WriteIndented = true });
            // 不写 BOM：带 BOM 会让下次反序列化失败（settings.json 的同类坑）
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch
        {
            // 写不进去（磁盘只读、目录被占）只损失记忆，不影响读数
        }
    }
}
