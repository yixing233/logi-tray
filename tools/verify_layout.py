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
pct_right = const("PercentLabelRight")
right_pad = const("RightColumnRightPadding")

big = re.search(r'_bigFont = new\("Microsoft YaHei UI", ([\d.]+)f', src).group(1)

print(f"CardWidth            = {card}")
print(f"PercentLabelWidth    = {pct_w}")
print(f"PercentLabelRight    = {pct_right}")
print(f"RightColumnRightPad  = {right_pad}")
print(f"大号字号             = {big}pt")
print()
print(f"电量列: x=12, 宽={pct_w}, 右边界={pct_right}")
print(f"右侧列: x={pct_right}, 宽={card - pct_right - right_pad}")
print(f"两列重叠: {'是' if 12 + pct_w > pct_right else '否'}")

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
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        float big = __BIG__f;
        int pctW = __PCTW__;
        int card = __CARD__;
        int pctRight = __PCTRIGHT__;
        int rightPad = __RIGHTPAD__;
        int rightW = card - pctRight - rightPad;

        var bigFont = new Font("Microsoft YaHei UI", big, FontStyle.Bold);
        var value = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        var label = new Font("Microsoft YaHei UI", 8.5f);

        bool ok = true;
        Console.WriteLine();
        Console.WriteLine("=== 电量列（可用 " + pctW + "px）===");
        foreach (string t in new[] { "100%", "99%", "82%", "9%", "--" })
        {
            int w = TextRenderer.MeasureText(t, bigFont).Width;
            bool fit = w <= pctW;
            ok &= fit;
            Console.WriteLine($"  \"{t}\" = {w,3}px  {(fit ? "OK" : "超出 " + (w - pctW) + "px")}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 右侧列（可用 " + rightW + "px）===");
        foreach (var (t, f) in new (string, Font)[] {
            ("放电中", value), ("充电中", value), ("已休眠", value),
            ("充电中（慢充）", value), ("已充满", value), ("充电异常", value),
            ("3 天 4 小时", label), ("设备离线", label), ("正在估算...", label),
            ("放电数据积累中", label), ("24 小时", label) })
        {
            int w = TextRenderer.MeasureText(t, f).Width;
            bool fit = w <= rightW;
            ok &= fit;
            Console.WriteLine($"  \"{t}\" = {w,3}px  {(fit ? "OK" : "超出 " + (w - rightW) + "px")}");
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "RESULT: PASS - 所有文字都能完整显示"
                            : "RESULT: FAIL - 仍有文字放不下");
    }
}
""".replace("__BIG__", big)
   .replace("__PCTW__", str(pct_w))
   .replace("__CARD__", str(card))
   .replace("__PCTRIGHT__", str(pct_right))
   .replace("__RIGHTPAD__", str(right_pad)))

r = subprocess.run(["dotnet", "run", "-c", "Release", "-v", "quiet"],
                   cwd=WORK, capture_output=True, text=True, errors="replace", timeout=300)
print(r.stdout)
if r.returncode != 0:
    print((r.stdout or "")[-500:] + (r.stderr or "")[-500:])
