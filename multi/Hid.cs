using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MultiTray;

/// <summary>
/// Windows HID 互操作层：枚举接口、读取报告描述符、收发报告。
///
/// 之所以自己写而不是引第三方库：项目交付的是纯应用包（不额外带原生依赖），
/// 而这些 Win32 调用本身很薄，自己写反而更好控制超时与错误处理。
///
/// 标记为 public 是因为 <see cref="IBatteryProvider"/> 的实现需要它作为
/// 参数类型；内部方法不对外暴露。
/// </summary>
public static class Hid
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const int HIDP_STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0110007);

    private static readonly Guid GUID_HID =
        new("4D1E55B2-F16F-11CF-88CB-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(
        SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(
        SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(
        IntPtr pp, ref HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(
        SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_SetFeature(
        SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_SetOutputReport(
        SafeFileHandle h, byte[] buf, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid g, IntPtr e, IntPtr h, int f);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr h, IntPtr di, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr h, ref SP_DEVICE_INTERFACE_DATA d, IntPtr det, int sz,
        ref int req, IntPtr di);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string n, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle h, byte[] b, int n, IntPtr r, ref OVERLAPPED o);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle h, byte[] b, int n, IntPtr w, ref OVERLAPPED o);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle h, ref OVERLAPPED o, out int t, bool w);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr a, bool m, bool i, IntPtr n);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIo(SafeFileHandle h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>一个 HID 接口及其能力。</summary>
    public sealed class HidInterface
    {
        public string Path = "";
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputLength;
        public ushort OutputLength;
        public ushort FeatureLength;
        public string? ProductName;

        public string Id => $"{VendorId:X4}:{ProductId:X4}";
        public override string ToString() =>
            $"{Id} UP=0x{UsagePage:X4} IN={InputLength} OUT={OutputLength} FEAT={FeatureLength}";
    }

    /// <summary>枚举当前所有 HID 接口。任何单个接口失败都不影响整体。</summary>
    public static List<HidInterface> Enumerate()
    {
        var result = new List<HidInterface>();
        Guid guid;
        HidD_GetHidGuid(out guid);

        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
                                         DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            for (int i = 0; ; i++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA();
                did.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did))
                    break;

                int required = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0,
                                                ref required, IntPtr.Zero);
                if (required <= 0) continue;

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    // 64 位上 cbSize 为 8（含对齐），32 位为 6；路径字符串从偏移 4 开始
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, detail, required,
                                                         ref required, IntPtr.Zero))
                        continue;

                    string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4))
                                  ?? "";
                    var info = Inspect(path);
                    if (info != null) result.Add(info);
                }
                catch
                {
                    // 单个接口异常不能中断枚举
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static HidInterface? Inspect(string path)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                           OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            // 有些接口只允许只读打开；只读也够读描述符与特性报告
            h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                           OPEN_EXISTING, 0, IntPtr.Zero);
        }
        if (h.IsInvalid) return null;

        using (h)
        {
            var attr = new HIDD_ATTRIBUTES();
            attr.Size = Marshal.SizeOf<HIDD_ATTRIBUTES>();
            if (!HidD_GetAttributes(h, ref attr)) return null;

            IntPtr pp;
            if (!HidD_GetPreparsedData(h, out pp)) return null;
            try
            {
                var caps = new HIDP_CAPS();
                caps.Reserved = new ushort[17];
                if (HidP_GetCaps(pp, ref caps) != HIDP_STATUS_SUCCESS) return null;

                return new HidInterface
                {
                    Path = path,
                    VendorId = attr.VendorID,
                    ProductId = attr.ProductID,
                    UsagePage = caps.UsagePage,
                    Usage = caps.Usage,
                    InputLength = caps.InputReportByteLength,
                    OutputLength = caps.OutputReportByteLength,
                    FeatureLength = caps.FeatureReportByteLength,
                    ProductName = TryGetProductName(h),
                };
            }
            finally
            {
                HidD_FreePreparsedData(pp);
            }
        }
    }

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(
        SafeFileHandle h, byte[] buf, int len);

    private static string? TryGetProductName(SafeFileHandle h)
    {
        try
        {
            var buf = new byte[256];
            if (!HidD_GetProductString(h, buf, buf.Length)) return null;
            string s = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    private static SafeFileHandle Open(string path, uint access, bool overlapped)
        => CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                      OPEN_EXISTING, overlapped ? FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);

    /// <summary>读取特性报告。<paramref name="reportId"/> 会被写入缓冲区首字节。</summary>
    public static byte[]? GetFeature(string path, byte reportId, int length)
    {
        if (length <= 1) return null;
        var h = Open(path, GENERIC_READ | GENERIC_WRITE, false);
        if (h.IsInvalid)
        {
            // 有些接口只允许只读打开；GET_FEATURE 在只读句柄上也有效
            h = Open(path, 0, false);
        }
        if (h.IsInvalid) return null;

        using (h)
        {
            var buf = new byte[length];
            buf[0] = reportId;
            return HidD_GetFeature(h, buf, buf.Length) ? buf : null;
        }
    }

    /// <summary>发送特性报告。</summary>
    public static bool SetFeature(string path, byte[] frame)
    {
        var h = Open(path, GENERIC_READ | GENERIC_WRITE, false);
        if (h.IsInvalid) return false;
        using (h) { return HidD_SetFeature(h, frame, frame.Length); }
    }

    /// <summary>发送输出报告（走控制端点，WriteFile 失败时用这个）。</summary>
    public static bool SetOutput(string path, byte[] frame)
    {
        var h = Open(path, GENERIC_READ | GENERIC_WRITE, false);
        if (h.IsInvalid) return false;
        using (h) { return HidD_SetOutputReport(h, frame, frame.Length); }
    }

    /// <summary>
    /// 写一帧并等待一帧输入报告，返回读到的字节（无响应返回 null）。
    ///
    /// 顺序很关键：**先挂起读、再写**。厂商协议的回答通常紧跟请求，
    /// 若先写后读，响应可能在 ReadFile 提交之前到达而被丢弃，
    /// 表现为「设备无响应」—— 这正是排查过程中踩过的坑。
    /// </summary>
    public static byte[]? WriteRead(string path, byte[] frame, int readLength,
                                    int timeoutMs)
    {
        if (readLength <= 0) return null;

        var h = Open(path, GENERIC_READ | GENERIC_WRITE, true);
        if (h.IsInvalid) return null;

        using (h)
        {
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero) return null;

            try
            {
                var buf = new byte[readLength];
                var ovr = new OVERLAPPED { hEvent = ev };
                bool readPending = ReadFile(h, buf, buf.Length, IntPtr.Zero, ref ovr);
                int readErr = Marshal.GetLastWin32Error();
                bool readQueued = readPending || readErr == ERROR_IO_PENDING;

                var ovw = new OVERLAPPED { hEvent = ev };
                bool wrote = WriteFile(h, frame, frame.Length, IntPtr.Zero, ref ovw);
                int writeErr = Marshal.GetLastWin32Error();

                if (!wrote)
                {
                    if (writeErr != ERROR_IO_PENDING)
                    {
                        if (readQueued) CancelIo(h);
                        return null;
                    }
                    if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                    {
                        CancelIo(h);
                        return null;
                    }
                    if (!GetOverlappedResult(h, ref ovw, out _, false))
                    {
                        CancelIo(h);
                        return null;
                    }
                }
                ResetEvent(ev);

                if (!readQueued)
                {
                    var ovr2 = new OVERLAPPED { hEvent = ev };
                    if (!ReadFile(h, buf, buf.Length, IntPtr.Zero, ref ovr2))
                    {
                        if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;
                        if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                        {
                            CancelIo(h);
                            return null;
                        }
                    }
                    if (!GetOverlappedResult(h, ref ovr2, out int got2, false)) return null;
                    if (got2 <= 0) return null;
                    if (got2 < buf.Length) Array.Resize(ref buf, got2);
                    return buf;
                }

                if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                {
                    CancelIo(h);
                    return null;
                }
                if (!GetOverlappedResult(h, ref ovr, out int got, false)) return null;
                if (got <= 0) return null;
                if (got < buf.Length) Array.Resize(ref buf, got);
                return buf;
            }
            finally
            {
                CloseHandle(ev);
            }
        }
    }

    /// <summary>在指定接口上被动监听一帧输入报告（不发送任何请求）。</summary>
    public static byte[]? Listen(string path, int readLength, int timeoutMs)
    {
        if (readLength <= 0) return null;
        var h = Open(path, GENERIC_READ | GENERIC_WRITE, true);
        if (h.IsInvalid) return null;

        using (h)
        {
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero) return null;
            try
            {
                var buf = new byte[readLength];
                var ov = new OVERLAPPED { hEvent = ev };
                bool pending = ReadFile(h, buf, buf.Length, IntPtr.Zero, ref ov);
                if (!pending && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;

                if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                {
                    CancelIo(h);
                    return null;
                }
                if (!GetOverlappedResult(h, ref ov, out int got, false)) return null;
                if (got <= 0) return null;
                if (got < buf.Length) Array.Resize(ref buf, got);
                return buf;
            }
            finally
            {
                CloseHandle(ev);
            }
        }
    }

    /// <summary>
    /// 把同一物理设备的多个 HID 接口归并到同一个键。
    ///
    /// 接口路径形如：
    ///   \\?\hid#vid_373b&amp;pid_1012&amp;mi_01&amp;col01#8&amp;1314833e&amp;0&amp;0001#{guid}
    /// 硬件 ID 段里的 mi_XX / colXX 会因接口/集合不同而变化，实例 ID 段
    /// 也会因接口不同而变化，因此**不能**直接用路径去重 —— 早先用
    /// 「取前两个 # 段」的做法让同一把键盘出现 3 条记录。
    ///
    /// 这里改用「VID:PID + 产品名」作为归并键：对本工具的语义
    /// （一台设备一个电量）而言这是正确的。副作用是同一型号的两台设备会
    /// 合并为一条 —— 这种情况很少见，且合并显示比重复显示更不容易误导。
    /// </summary>
    public static string PhysicalKey(HidInterface d)
    {
        string name = string.IsNullOrWhiteSpace(d.ProductName) ? "" : d.ProductName!;
        return $"{d.VendorId:X4}:{d.ProductId:X4}:{name}";
    }
}
