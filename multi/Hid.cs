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

    /// <summary>
    /// IOCTL_HID_GET_REPORT_DESCRIPTOR = HID_CTL_CODE(0)
    ///                                = CTL_CODE(FILE_DEVICE_KEYBOARD, 0, METHOD_NEITHER, FILE_ANY_ACCESS)
    ///                                = (0x0B &lt;&lt; 16) | (0 &lt;&lt; 14) | (0 &lt;&lt; 2) | 3
    ///                                = 0x000B0003。
    ///
    /// 注意低两位是 METHOD_NEITHER(3)，不是 0 —— 早先误写成 0x000B0000，
    /// 导致 DeviceIoControl 一直返回失败、报告描述符永远读不到。
    ///
    /// HidP_GetCaps 只给出「最长的那个报告」的长度（各报告号取最大值），
    /// 因此当设备同时存在 31 字节与 63 字节两种载荷时，它会报 64，
    /// 让人误以为所有报告都该按 64 字节发。要拿到**按报告号区分**的真实
    /// 长度，只能读原始报告描述符自己解析。
    /// </summary>
    private const uint IOCTL_HID_GET_REPORT_DESCRIPTOR = 0x000B0003;

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
    [DllImport("hid.dll")] private static extern bool HidD_GetInputReport(
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
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle h, uint code, IntPtr inBuf, int inSize,
        byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

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

    /// <summary>
    /// 报告描述符里解析出的「按报告号区分」的报告长度。
    ///
    /// 为什么需要：HidP_GetCaps 返回的 InputReportByteLength 是**所有报告号
    /// 里的最大值**（含报告号字节）。若设备同时定义 reportId=3 → 2 字节 与
    /// reportId=4 → 64 字节，GET_CAPS 只说 64，无法判断某个报告号实际多长。
    /// </summary>
    public sealed class ReportLengths
    {
        /// <summary>报告号 → 输入报告总长（含报告号字节）。</summary>
        public readonly Dictionary<byte, int> Input = new();
        /// <summary>报告号 → 输出报告总长（含报告号字节）。</summary>
        public readonly Dictionary<byte, int> Output = new();

        /// <summary>描述符是否显式声明了报告号（带 Report ID 的设备）。</summary>
        public bool HasReportIds;
    }

    /// <summary>
    /// 读取并解析 HID 报告描述符，得到每个报告号的真实长度。
    ///
    /// 只实现到「够用」的程度：跟踪 Report ID / Report Size / Report Count
    /// 三个全局项，遇到 Input / Output 主项时把位宽累加到当前报告号上。
    /// 变量的解析（Usage 等）一律跳过。解析失败返回 null。
    /// </summary>
    public static ReportLengths? GetReportLengths(string path)
    {
        var desc = GetReportDescriptor(path);
        if (desc == null || desc.Length == 0) return null;

        var r = new ReportLengths();
        int reportSize = 0, reportCount = 0;
        int globalReportId = 0;
        byte curId = 0;

        // 当前报告号是否已出现过（用于判断是否真的是带 ID 的设备）
        int i = 0;
        while (i < desc.Length)
        {
            byte b = desc[i++];
            if (b == 0xFE)
            {
                // 长项：0xFE 后跟 1 字节长度、1 字节 tag
                if (i + 1 >= desc.Length) break;
                int len = desc[i++];
                i++; // tag
                i += len;
                continue;
            }

            int size = b & 0x03;
            if (size == 3) size = 4;
            int type = (b >> 2) & 0x03;
            int tag = (b >> 4) & 0x0F;

            int val = 0;
            for (int k = 0; k < size && i < desc.Length; k++) val |= desc[i + k] << (8 * k);
            i += size;

            switch (type)
            {
                case 1: // Global
                    if (tag == 0) { globalReportId = val; curId = (byte)val; r.HasReportIds = true; }
                    else if (tag == 7) reportSize = val;
                    else if (tag == 9) reportCount = val;
                    break;
                case 0: // Main
                    if (tag == 8 || tag == 9) // Input / Output
                    {
                        int bytes = (reportSize * reportCount + 7) / 8;
                        // reportId 为 0 时不占字节；非 0 时报告号本身占 1 字节
                        int total = bytes + (globalReportId == 0 ? 0 : 1);
                        var map = tag == 8 ? r.Input : r.Output;
                        map.TryGetValue(curId, out int prev);
                        if (total > prev) map[curId] = total;
                        _ = curId;
                    }
                    break;
            }
        }
        return r;
    }

    /// <summary>用 IOCTL 取原始报告描述符字节；失败返回 null。</summary>
    public static byte[]? GetReportDescriptor(string path)
    {
        var h = Open(path, 0, false); // 描述符只读即可
        if (h.IsInvalid) h = Open(path, GENERIC_READ, false);
        if (h.IsInvalid) return null;

        using (h)
        {
            var buf = new byte[4096];
            if (!DeviceIoControl(h, IOCTL_HID_GET_REPORT_DESCRIPTOR,
                                 IntPtr.Zero, 0, buf, buf.Length, out int got,
                                 IntPtr.Zero))
                return null;
            if (got <= 0) return null;
            if (got < buf.Length) Array.Resize(ref buf, got);
            return buf;
        }
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
    /// 用控制端点做一次「SET_OUTPUT → 等待 → GET_INPUT」往返。
    ///
    /// 为什么需要它：本机 0xFF1C / 0xFFEF 这两个厂商接口用 WriteFile 直接报
    /// error 87（ERROR_INVALID_PARAMETER），因为它们的输出报告不是通过中断 OUT
    /// 端点走的，必须走控制端点的 Set_Report。此前的探测全部用 WriteFile，
    /// 因此这两个接口实际上从未被真正写入过。
    ///
    /// 读取同样走控制端点（HidD_GetInputReport），这也是带编号报告设备的常规做法。
    /// </summary>
    public static byte[]? ControlRoundTrip(string path, byte[] frame, int readLength,
                                           int settleMs)
    {
        if (readLength <= 0) return null;

        var h = Open(path, GENERIC_READ | GENERIC_WRITE, false);
        if (h.IsInvalid) return null;

        using (h)
        {
            if (!HidD_SetOutputReport(h, frame, frame.Length)) return null;

            // 给设备时间把应答准备好
            if (settleMs > 0) Thread.Sleep(settleMs);

            var buf = new byte[readLength];
            // GET_INPUT_REPORT 要求首字节是 Report ID
            buf[0] = frame.Length > 0 ? frame[0] : (byte)0;
            return HidD_GetInputReport(h, buf, buf.Length) ? buf : null;
        }
    }

    /// <summary>只取控制端点的输入报告（不先写）。</summary>
    public static byte[]? GetInputReport(string path, byte reportId, int length)    {
        if (length <= 1) return null;
        var h = Open(path, GENERIC_READ | GENERIC_WRITE, false);
        if (h.IsInvalid) return null;
        using (h)
        {
            var buf = new byte[length];
            buf[0] = reportId;
            return HidD_GetInputReport(h, buf, buf.Length) ? buf : null;
        }
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

    /// <summary>
    /// 先写一帧、**等待**、再读一帧输入报告。
    ///
    /// 与 <see cref="WriteRead"/> 的区别在于「等待」这一步，这是照着上游
    /// ATK 实现（ATK_tray）的做法加的：它 write 之后 sleep(0.1) 才 read。
    /// 部分设备在收到请求后需要一点时间才把应答放进输入队列，写后立即读
    /// 会得到空结果，看起来像「设备不应答」。
    /// </summary>
    public static byte[]? WriteThenRead(string path, byte[] frame, int readLength,
                                        int settleMs, int timeoutMs)
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
                // 先写（同步等待写完）
                var ovw = new OVERLAPPED { hEvent = ev };
                if (!WriteFile(h, frame, frame.Length, IntPtr.Zero, ref ovw))
                {
                    if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;
                    if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                    {
                        CancelIo(h);
                        return null;
                    }
                    if (!GetOverlappedResult(h, ref ovw, out _, false)) return null;
                }
                ResetEvent(ev);

                // 关键：给设备留出准备应答的时间
                if (settleMs > 0) Thread.Sleep(settleMs);

                // 再挂读并等待
                var buf = new byte[readLength];
                var ovr = new OVERLAPPED { hEvent = ev };
                if (!ReadFile(h, buf, buf.Length, IntPtr.Zero, ref ovr))
                {
                    if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;
                    if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                    {
                        CancelIo(h);
                        return null;
                    }
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

    /// <summary>
    /// 用**控制端点写**输出报告，再从**中断 IN 端点读**应答。
    ///
    /// 这是此前唯一没试过的组合，也是 WebHID 的行为：WebHID 的 sendReport()
    /// 对带编号报告走 Set_Report 控制传输，而 inputreport 事件则是主机在
    /// 中断 IN 端点上排一个 ReadFile。此前要么「中断写 + 中断读」，要么
    /// 「控制写 + 控制读」，二者都不匹配真正的驱动行为。
    ///
    /// <paramref name="writeViaControl"/> 为 false 时改用 WriteFile 写
    /// （有些接口只接受其中一种）。
    /// </summary>
    public static byte[]? WriteControlReadInterrupt(string path, byte[] frame,
                                                    int readLength, int settleMs,
                                                    int timeoutMs,
                                                    bool writeViaControl = true)
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
                // 先把读排上，避免应答比读请求先到
                var buf = new byte[readLength];
                var ovr = new OVERLAPPED { hEvent = ev };
                bool readPending = ReadFile(h, buf, buf.Length, IntPtr.Zero, ref ovr);
                int readErr = Marshal.GetLastWin32Error();
                bool readQueued = readPending || readErr == ERROR_IO_PENDING;

                bool wrote;
                if (writeViaControl)
                {
                    wrote = HidD_SetOutputReport(h, frame, frame.Length);
                }
                else
                {
                    var ovw = new OVERLAPPED { hEvent = ev };
                    wrote = WriteFile(h, frame, frame.Length, IntPtr.Zero, ref ovw);
                    if (!wrote && Marshal.GetLastWin32Error() == ERROR_IO_PENDING)
                    {
                        wrote = WaitForSingleObject(ev, (uint)timeoutMs) == 0 &&
                                GetOverlappedResult(h, ref ovw, out _, false);
                        ResetEvent(ev);
                    }
                }

                if (!wrote)
                {
                    if (readQueued) CancelIo(h);
                    return null;
                }

                if (settleMs > 0) Thread.Sleep(settleMs);

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

    /// <summary>
    /// 一次传输尝试的详细结果，供诊断工具区分「写失败」与「写成功但无应答」。
    ///
    /// 之所以需要：原来的诊断把两者都打印成「(无响应)」，
    /// 结果无法判断问题出在管道还是命令 —— 这正是排查 ATK 时的盲点。
    /// </summary>
    public struct WriteResult
    {
        public bool WriteOk;
        public int WriteError;
        public byte[]? Response;
        public string Describe()
        {
            if (!WriteOk) return $"写失败 (Win32 错误 {WriteError})";
            return Response == null ? "写成功，读超时（设备未应答）"
                                    : $"写成功，收到 {Response.Length} 字节";
        }
    }

    /// <summary>
    /// 写一帧（中断 OUT）**之后**再挂读，并报告写是否成功。
    ///
    /// 与 <see cref="ProbeInterruptCore"/> 的顺序相反：那边先挂读再写，
    /// 这边先写后挂读（等于 <see cref="WriteThenRead"/> 加错误码）。
    /// 两种顺序都保留，因为无法先验判断设备在哪一侧应答。
    /// </summary>
    public static WriteResult ProbeInterrupt(string path, byte[] frame, int readLength,
                                             int settleMs, int timeoutMs)
    {
        var r = new WriteResult();
        if (readLength <= 0) { r.WriteError = -1; return r; }

        var h = Open(path, GENERIC_READ | GENERIC_WRITE, true);
        if (h.IsInvalid) { r.WriteError = Marshal.GetLastWin32Error(); return r; }

        using (h)
        {
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero) { r.WriteError = Marshal.GetLastWin32Error(); return r; }
            try
            {
                // 先写
                var ovw = new OVERLAPPED { hEvent = ev };
                bool wrote = WriteFile(h, frame, frame.Length, IntPtr.Zero, ref ovw);
                int werr = Marshal.GetLastWin32Error();
                if (!wrote && werr == ERROR_IO_PENDING)
                {
                    wrote = WaitForSingleObject(ev, (uint)timeoutMs) == 0 &&
                            GetOverlappedResult(h, ref ovw, out _, false);
                }
                r.WriteOk = wrote;
                r.WriteError = wrote ? 0 : werr;
                ResetEvent(ev);
                if (!wrote) return r;
                if (settleMs > 0) Thread.Sleep(settleMs);

                // 再挂读
                var buf = new byte[readLength];
                var ovr = new OVERLAPPED { hEvent = ev };
                if (!ReadFile(h, buf, buf.Length, IntPtr.Zero, ref ovr) &&
                    Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
                    return r;
                if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                {
                    CancelIo(h);
                    return r;
                }
                if (!GetOverlappedResult(h, ref ovr, out int got, false) || got <= 0)
                    return r;
                if (got < buf.Length) Array.Resize(ref buf, got);
                r.Response = buf;
                return r;
            }
            finally { CloseHandle(ev); }
        }
    }

    /// <summary>
    /// **先挂读再写**，然后等应答；<paramref name="writeViaControl"/> 决定写走
    /// 控制端点（HidD_SetOutputReport）还是中断 OUT（WriteFile）。
    ///
    /// 读请求在写之前提交，因为应答可能在写返回与读提交之间到达而被丢弃 ——
    /// 这是排查 ATK 时的一个重要盲点（此前只用过 <see cref="ProbeInterrupt"/>
    /// 的「写后挂读」）。
    /// </summary>
    public static WriteResult ProbeInterruptCore(string path, byte[] frame,
                                                 int readLength, int settleMs,
                                                 int timeoutMs, bool writeViaControl)
    {
        var r = new WriteResult();
        if (readLength <= 0) { r.WriteError = -1; return r; }

        var h = Open(path, GENERIC_READ | GENERIC_WRITE, true);
        if (h.IsInvalid) { r.WriteError = Marshal.GetLastWin32Error(); return r; }

        using (h)
        {
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero) { r.WriteError = Marshal.GetLastWin32Error(); return r; }
            try
            {
                // 先把读排上，避免应答比读请求先到而被丢弃
                var buf = new byte[readLength];
                var ovr = new OVERLAPPED { hEvent = ev };
                bool readPending = ReadFile(h, buf, buf.Length, IntPtr.Zero, ref ovr);
                int readErr = Marshal.GetLastWin32Error();
                bool readQueued = readPending || readErr == ERROR_IO_PENDING;

                bool wrote;
                int werr = 0;
                if (writeViaControl)
                {
                    wrote = HidD_SetOutputReport(h, frame, frame.Length);
                    if (!wrote) werr = Marshal.GetLastWin32Error();
                }
                else
                {
                    var ovw = new OVERLAPPED { hEvent = ev };
                    wrote = WriteFile(h, frame, frame.Length, IntPtr.Zero, ref ovw);
                    werr = Marshal.GetLastWin32Error();
                    if (!wrote && werr == ERROR_IO_PENDING)
                    {
                        wrote = WaitForSingleObject(ev, (uint)timeoutMs) == 0 &&
                                GetOverlappedResult(h, ref ovw, out _, false);
                    }
                }
                r.WriteOk = wrote;
                r.WriteError = wrote ? 0 : werr;
                ResetEvent(ev);

                if (!wrote)
                {
                    if (readQueued) CancelIo(h);
                    return r;
                }
                if (settleMs > 0) Thread.Sleep(settleMs);

                if (!readQueued)
                {
                    var ovr2 = new OVERLAPPED { hEvent = ev };
                    if (!ReadFile(h, buf, buf.Length, IntPtr.Zero, ref ovr2) &&
                        Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
                        return r;
                    if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                    {
                        CancelIo(h);
                        return r;
                    }
                    if (!GetOverlappedResult(h, ref ovr2, out int g2, false) || g2 <= 0)
                        return r;
                    if (g2 < buf.Length) Array.Resize(ref buf, g2);
                    r.Response = buf;
                    return r;
                }

                if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                {
                    CancelIo(h);
                    return r;
                }
                if (!GetOverlappedResult(h, ref ovr, out int got, false) || got <= 0)
                    return r;
                if (got < buf.Length) Array.Resize(ref buf, got);
                r.Response = buf;
                return r;
            }
            finally { CloseHandle(ev); }
        }
    }

    /// <summary>控制端点往返，同时报告写是否成功。</summary>
    public static WriteResult ProbeControlRoundTrip(string path, byte[] frame,
                                                    int readLength, int settleMs)
    {
        var r = new WriteResult();
        var h = Open(path, GENERIC_READ | GENERIC_WRITE, false);
        if (h.IsInvalid) { r.WriteError = Marshal.GetLastWin32Error(); return r; }
        using (h)
        {
            if (!HidD_SetOutputReport(h, frame, frame.Length))
            {
                r.WriteError = Marshal.GetLastWin32Error();
                return r;
            }
            r.WriteOk = true;
            if (settleMs > 0) Thread.Sleep(settleMs);

            var buf = new byte[readLength];
            buf[0] = frame.Length > 0 ? frame[0] : (byte)0;
            r.Response = HidD_GetInputReport(h, buf, buf.Length) ? buf : null;
        }
        return r;
    }

    /// <summary>
    /// 一次交换的结果：发出的帧、按顺序收到的所有报告、以及首个写错误。
    /// </summary>
    public sealed class ExchangeResult
    {
        public bool WriteOk = true;
        public int WriteError;
        public List<byte[]> Reports = new();
        public string Describe()
        {
            if (!WriteOk) return $"写失败 (Win32 错误 {WriteError})";
            return Reports.Count == 0
                ? "写成功，未收到任何报告"
                : $"写成功，收到 {Reports.Count} 个报告";
        }
    }

    /// <summary>
    /// **连续读取 + 批量发送**的一次完整交换。
    ///
    /// 这是 WebHID 的真实行为模型：驱动打开设备后持续接收 inputreport 事件，
    /// 期间穿插多次 sendReport。此前每个探针都是「开句柄 → 写一帧 → 读一次 → 关句柄」，
    /// 于是上一帧的应答会被下一帧的读请求收走（实测：帧长 32 的四路探针全部超时，
    /// 紧接着帧长 64 的探针却收到了属于前一帧的 `04 05 00 FF ...`），
    /// 既丢应答又误判归属。
    ///
    /// 本方法在一次句柄生命周期内：逐帧写 <paramref name="frames"/>，每写完一帧
    /// 就把到达的报告排空，全部发完后再等 <paramref name="drainMs"/> 毫秒接住迟到的报告。
    /// 任何写失败都记在 <see cref="ExchangeResult.WriteError"/>，不再与「读超时」混为一谈。
    /// </summary>
    public static ExchangeResult ProbeDrain(string path, IReadOnlyList<byte[]> frames,
                                            int readLength, int settleMs, int drainMs,
                                            bool writeViaControl = false)
    {
        var result = new ExchangeResult();
        if (readLength <= 0) { result.WriteOk = false; result.WriteError = -1; return result; }

        var h = Open(path, GENERIC_READ | GENERIC_WRITE, true);
        if (h.IsInvalid)
        {
            result.WriteOk = false;
            result.WriteError = Marshal.GetLastWin32Error();
            return result;
        }

        using (h)
        {
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero)
            {
                result.WriteOk = false;
                result.WriteError = Marshal.GetLastWin32Error();
                return result;
            }
            try
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];

                    if (writeViaControl)
                    {
                        if (!HidD_SetOutputReport(h, frame, frame.Length))
                        {
                            result.WriteOk = false;
                            result.WriteError = Marshal.GetLastWin32Error();
                            return result;
                        }
                    }
                    else
                    {
                        var ovw = new OVERLAPPED { hEvent = ev };
                        bool wrote = WriteFile(h, frame, frame.Length, IntPtr.Zero, ref ovw);
                        int werr = Marshal.GetLastWin32Error();
                        if (!wrote && werr == ERROR_IO_PENDING)
                            wrote = WaitForSingleObject(ev, 500) == 0 &&
                                    GetOverlappedResult(h, ref ovw, out _, false);
                        ResetEvent(ev);
                        if (!wrote)
                        {
                            result.WriteOk = false;
                            result.WriteError = werr;
                            return result;
                        }
                    }

                    if (settleMs > 0) Thread.Sleep(settleMs);

                    // 写完就把它之后到达的报告收干净，避免串到下一帧
                    Drain(h, ev, readLength, result.Reports, drainMs);
                }

                // 帧发完后继续等一会儿，接住迟到的报告
                Drain(h, ev, readLength, result.Reports, drainMs);
                return result;
            }
            finally { CloseHandle(ev); }
        }
    }

    /// <summary>
    /// **连续后台读取 + 连发**的一次完整交换，用于复现浏览器的真实时序。
    ///
    /// 抓包记录显示驱动的 34 帧全部落在同一秒内（时间戳只有秒级精度，
    /// 34 帧同秒 = 背靠背连发）。而 <see cref="ProbeDrain"/> 每帧要
    /// <c>settleMs + drainMs</c>，17 帧就要 5.4 秒 —— 设备很可能因为
    /// 会话超时而拒绝后续命令（载荷 [2]=0xFF）。
    ///
    /// 本方法先用一个后台线程挂上连续的异步读，再以
    /// <paramref name="gapMs"/> 的间隔把帧连发出去，最后再等
    /// <paramref name="afterMs"/> 毫秒收尾。读线程全程不停，因此不会
    /// 漏掉任一帧的应答。
    /// </summary>
    public static ExchangeResult ProbeBurst(string path, IReadOnlyList<byte[]> frames,
                                            int readLength, int gapMs, int afterMs,
                                            bool writeViaControl = false)
    {
        var result = new ExchangeResult();
        if (readLength <= 0) { result.WriteOk = false; result.WriteError = -1; return result; }

        var h = Open(path, GENERIC_READ | GENERIC_WRITE, true);
        if (h.IsInvalid)
        {
            result.WriteOk = false;
            result.WriteError = Marshal.GetLastWin32Error();
            return result;
        }

        using (h)
        {
            var stop = false;
            IntPtr readEv = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            var reader = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                    Drain(h, readEv, readLength, result.Reports, 30);
            })
            { IsBackground = true };
            reader.Start();

            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            try
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];

                    if (writeViaControl)
                    {
                        if (!HidD_SetOutputReport(h, frame, frame.Length))
                        {
                            result.WriteOk = false;
                            result.WriteError = Marshal.GetLastWin32Error();
                            break;
                        }
                    }
                    else
                    {
                        var ovw = new OVERLAPPED { hEvent = ev };
                        bool wrote = WriteFile(h, frame, frame.Length, IntPtr.Zero, ref ovw);
                        int werr = Marshal.GetLastWin32Error();
                        if (!wrote && werr == ERROR_IO_PENDING)
                            wrote = WaitForSingleObject(ev, 500) == 0 &&
                                    GetOverlappedResult(h, ref ovw, out _, false);
                        ResetEvent(ev);
                        if (!wrote)
                        {
                            result.WriteOk = false;
                            result.WriteError = werr;
                            break;
                        }
                    }

                    if (gapMs > 0) Thread.Sleep(gapMs);
                }

                if (afterMs > 0) Thread.Sleep(afterMs);
                return result;
            }
            finally
            {
                Volatile.Write(ref stop, true);
                CloseHandle(ev);
                reader.Join(500);
                CloseHandle(readEv);
            }
        }
    }

    /// <summary>在 <paramref name="totalMs"/> 内把所有到达的报告追加到 <paramref name="into"/>。</summary>
    private static void Drain(SafeFileHandle h, IntPtr ev, int readLength,
                              List<byte[]> into, int totalMs)
    {
        var deadline = Environment.TickCount64 + totalMs;
        while (Environment.TickCount64 < deadline)
        {
            int left = (int)(deadline - Environment.TickCount64);
            if (left <= 0) break;

            var buf = new byte[readLength];
            var ov = new OVERLAPPED { hEvent = ev };
            bool pending = ReadFile(h, buf, buf.Length, IntPtr.Zero, ref ov);
            if (!pending && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return;

            if (WaitForSingleObject(ev, (uint)left) != 0)
            {
                CancelIo(h);

                // 必须**等被取消的 I/O 真正落定**再返回。
                // 否则这个 OVERLAPPED（及其事件）会在下一次 ReadFile 里被复用，
                // 而 ResetEvent 也可能在 I/O 尚未完成时就清掉事件 ——
                // 两者都会让后续读到的报告丢失或串台，表现为「偶尔整轮无应答」。
                // 这是 ProbeBurst 在真机上 3/8 次读不到电量的根因。
                GetOverlappedResult(h, ref ov, out _, true);
                ResetEvent(ev);
                return;
            }
            if (!GetOverlappedResult(h, ref ov, out int got, false) || got <= 0)
            {
                ResetEvent(ev);
                return;
            }
            ResetEvent(ev);
            if (got < buf.Length) Array.Resize(ref buf, got);
            into.Add(buf);
        }
    }

    /// <summary>
    /// 在指定接口上被动监听一帧输入报告（不发送任何请求）。
    /// </summary>
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
