"""Confirm the final layout constants leave room for every string the card shows.

Reads the real constants out of DetailsForm (via the compiled assembly where
possible) and checks the widest string of each column fits, with the numbers
measured rather than assumed.
"""
import os
import re
import subprocess

REPO = r"C:\code\chat-records\mouse-tray"
SRC = os.path.join(REPO, "lite", "DetailsForm.cs")

with open(SRC, encoding="utf-8") as f:
    src = f.read()


_consts = {}


def const(name):
    """求值常量，允许像 PercentLabelRight = 12 + PercentLabelWidth 这样的引用。"""
    m = re.search(rf"private const int {name} = ([^;]+);", src)
    if not m:
        raise SystemExit(f"找不到常量 {name}")
    value = eval(m.group(1), {}, _consts)
    _consts[name] = value
    return value


card = const("CardWidth")
pct_w = const("PercentLabelWidth")
pct_w_offline = const("PercentLabelWidthOffline")
pct_right = const("PercentLabelRight")
right_pad = const("RightColumnRightPadding")

big = re.search(r'_bigFont = new\("Microsoft YaHei UI", ([\d.]+)f', src).group(1)

# 在线与离线是两套列宽：离线时大号数字只是「--」，左列收到 40px 把宽度让给
# 「上次 100% · 23 小时前」。两套都要验，只验一套会漏掉溢出。
online_right = card - pct_right - right_pad
offline_right = card - (12 + pct_w_offline) - right_pad

print(f"CardWidth                 = {card}")
print(f"PercentLabelWidth         = {pct_w}")
print(f"PercentLabelWidthOffline  = {pct_w_offline}")
print(f"PercentLabelRight         = {pct_right}")
print(f"RightColumnRightPad       = {right_pad}")
print(f"大号字号                  = {big}pt")
print()
print(f"[在线] 电量列: x=12, 宽={pct_w}, 右边界={pct_right}")
print(f"[在线] 右侧列: x={pct_right}, 宽={online_right}")
print(f"[在线] 两列重叠: {'是' if 12 + pct_w > pct_right else '否'}")
print(f"[离线] 电量列: x=12, 宽={pct_w_offline}, 右边界={12 + pct_w_offline}")
print(f"[离线] 右侧列: x={12 + pct_w_offline}, 宽={offline_right}")
# 离线时右列的起点**就是**左列宽度算出来的，两者按构造不可能重叠，
# 所以这里不写恒为「否」的重叠判断，改验右列是否真的顶到了卡片右边距。
print(f"[离线] 右列右边界: {12 + pct_w_offline + offline_right}（应为 {card - right_pad}）")

WORK = os.path.join(REPO, "_measure3")
os.makedirs(WORK, exist_ok=True)
with open(os.path.join(WORK, "m.csproj"), "w", encoding="utf-8") as f:
    f.write("""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>disable</Nullable><UseWPF>false</UseWPF><UseWindowsForms>true</UseWindowsForms>
    <AssemblyName>m</AssemblyName><StartupObject>M</StartupObject>
    <EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>
  </PropertyGroup>
</Project>
""")

with open(os.path.join(WORK, "M.cs"), "w", encoding="utf-8") as f:
    f.write(r"""
using System;
using System.Drawing;
using System.Windows.Forms;

internal static class M
{
    // 与 Label 的实际渲染一致：Label 不额外加内边距，用默认 flags 量出来的
    // 宽度会多出十几像素（"100%" 默认 102px vs 实际 85px），
    // 于是「放得下」会被误判成「超出」，凭空造出假缺陷。
    private const TextFormatFlags F = TextFormatFlags.NoPadding;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        float big = __BIG__f;
        int pctW = __PCTW__;
        int pctWOffline = __PCTWOFFLINE__;
        int card = __CARD__;
        int pctRight = __PCTRIGHT__;
        int rightPad = __RIGHTPAD__;
        int onlineRightW = card - pctRight - rightPad;
        int offlineRightW = card - (12 + pctWOffline) - rightPad;

        var bigFont = new Font("Microsoft YaHei UI", big, FontStyle.Bold);
        var value = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        var label = new Font("Microsoft YaHei UI", 8.5f);

        bool ok = true;
        Console.WriteLine();
        Console.WriteLine("=== [在线] 电量列（可用 " + pctW + "px）===");
        foreach (string t in new[] { "100%", "99%", "82%", "9%", "--" })
        {
            int w = TextRenderer.MeasureText(t, bigFont, new Size(int.MaxValue, int.MaxValue), F).Width;
            bool fit = w <= pctW;
            ok &= fit;
            Console.WriteLine($"  \"{t}\" = {w,3}px  {(fit ? "OK" : "超出 " + (w - pctW) + "px")}");
        }

        Console.WriteLine();
        Console.WriteLine("=== [在线] 右侧列（可用 " + onlineRightW + "px）===");
        foreach (var (t, f) in new (string, Font)[] {
            ("放电中", value), ("充电中", value), ("已休眠", value),
            ("充电中（慢充）", value), ("已充满", value), ("充电异常", value),
            ("3 天 4 小时", label), ("设备离线", label), ("正在估算...", label),
            ("放电数据积累中", label), ("24 小时", label) })
        {
            int w = TextRenderer.MeasureText(t, f, new Size(int.MaxValue, int.MaxValue), F).Width;
            bool fit = w <= onlineRightW;
            ok &= fit;
            Console.WriteLine($"  \"{t}\" = {w,3}px  {(fit ? "OK" : "超出 " + (w - onlineRightW) + "px")}");
        }

        // 离线时左列只有 40px，大号数字必须仍然放得下「--」。
        Console.WriteLine();
        Console.WriteLine("=== [离线] 电量列（可用 " + pctWOffline + "px）===");
        foreach (string t in new[] { "--" })
        {
            int w = TextRenderer.MeasureText(t, bigFont, new Size(int.MaxValue, int.MaxValue), F).Width;
            bool fit = w <= pctWOffline;
            ok &= fit;
            Console.WriteLine($"  \"{t}\" = {w,3}px  {(fit ? "OK" : "超出 " + (w - pctWOffline) + "px")}");
        }

        // 离线时右侧列要装得下历史小字，这是本列宽存在的理由。
        Console.WriteLine();
        Console.WriteLine("=== [离线] 右侧列（可用 " + offlineRightW + "px）===");
        foreach (var (t, f) in new (string, Font)[] {
            ("上次 77% · 2 小时前", label),
            ("上次 100% · 23 小时前", label),
            ("上次 100% · 59 分钟前", label),
            ("上次 100% · 刚刚", label),
            ("上次 100% · 30 天前", label),
            ("已休眠", value), ("设备离线", label) })
        {
            int w = TextRenderer.MeasureText(t, f, new Size(int.MaxValue, int.MaxValue), F).Width;
            bool fit = w <= offlineRightW;
            ok &= fit;
            Console.WriteLine($"  \"{t}\" = {w,3}px  {(fit ? "OK" : "超出 " + (w - offlineRightW) + "px")}");
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "RESULT: PASS - 所有文字都能完整显示"
                            : "RESULT: FAIL - 仍有文字放不下");
    }
}
""".replace("__BIG__", big)
   .replace("__PCTW__", str(pct_w))
   .replace("__PCTWOFFLINE__", str(pct_w_offline))
   .replace("__CARD__", str(card))
   .replace("__PCTRIGHT__", str(pct_right))
   .replace("__RIGHTPAD__", str(right_pad)))

r = subprocess.run(["dotnet", "run", "-c", "Release", "-v", "quiet"],
                   cwd=WORK, capture_output=True, text=True, errors="replace", timeout=300)
print(r.stdout)
if r.returncode != 0:
    print((r.stdout or "")[-500:] + (r.stderr or "")[-500:])
