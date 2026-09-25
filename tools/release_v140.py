"""Publish the v1.4.0 release containing all three editions.

Why one release for three editions: the README points users at /releases/latest
and documents three archives, so a user following that link must find all three
downloads in one place. A separate tag for the multi-brand edition would make
"latest" point at a page missing the logi-tray packages.

Why the notes were rewritten from scratch: the previous release (v1.3.1) claimed
the ATK Z87 keyboard only echoes protocol frames and reads `--`, which stopped
being true once its real protocol was cracked and verified at 20/20 reliability.
Leaving that text up would contradict the README. Every claim below was measured
on real hardware on this machine; nothing here is projected.
"""
import json
import os
import subprocess
import urllib.error
import urllib.parse
import urllib.request

REPO = "yixing233/logi-tray"
TAG = "v1.4.0"
TITLE = "v1.4.0 · ATK Z87 键盘协议破解，多品牌版 UI 重建与休眠电量"

RELEASE_DIR = r"C:\code\chat-records\mouse-tray\release"
ASSETS = [
    "logi-tray-v1.2.1.zip",
    "logi-tray-lite-v1.2.0.zip",
    "multi-tray-v1.2.0.zip",
]

tok = subprocess.run(["git", "credential", "fill"],
                     input="protocol=https\nhost=github.com\n\n",
                     capture_output=True, text=True)
TOKEN = [l[9:] for l in tok.stdout.splitlines() if l.startswith("password=")][0]


def api(method, path, body=None, raw=None, ctype="application/json"):
    url = path if path.startswith("http") else "https://api.github.com" + path
    data = raw if raw is not None else (json.dumps(body).encode() if body else None)
    r = urllib.request.Request(url, data=data, method=method)
    r.add_header("User-Agent", "multi-tray-release")
    r.add_header("Accept", "application/vnd.github+json")
    r.add_header("Authorization", f"token {TOKEN}")
    if data:
        r.add_header("Content-Type", ctype)
    try:
        with urllib.request.urlopen(r, timeout=600) as resp:
            b = resp.read()
            return resp.status, (json.loads(b.decode()) if b else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")


NOTES = """## v1.4.0 · ATK Z87 键盘协议破解，多品牌版 UI 重建与休眠电量

三台真实设备全部读到。上一版说「ATK Z87 只回显、读不到」——**这个结论是错的**，
本版把它推翻了。

### ✅ 实机验证结果

| 设备 | 读取结果 | 稳定性 | 来源 |
| --- | --- | --- | --- |
| 罗技 PRO X Wireless 鼠标 | 稳定读到 | ✅ | HID++ 2.0 |
| **ATK Z87 键盘** | **100% · 充电中** | **20/20** | 官方驱动抓包 |
| 迈从 MCHOSE V9 PRO 耳机 | **30%** | ✅ | 迈从 `55 65` |

### 🔓 ATK Z87：为什么上一版读不到

上一版把「设备回显请求帧」当成了结论。实际是两个具体错误：

1. **帧长必须是 64**。发 32 字节会被设备直接拒绝（`Win32 错误 87` = 参数无效）。
2. **必须复现完整的 17 帧前导**：14 帧 `0x1B` LED 指令（`[4..5]` 是 16 位小端偏移，
   逐帧 `+0x18`）→ 2 帧 `0x03` → 最后才是 `0x1A` 电量指令。
   单发 `0x1A` 会被拒（`[2]` 回 `0xFF`，该字节语义已从驱动源码确认为「错误」）。

协议不是猜的，是从**官方网页驱动 `hub.atk.pro` v3.2.27 的 bundle 源码**里读出来的
（`get batteryLevel()` 取 `[baseOffset+2]`、`batteryCharge() !== 0` 即为充电），
再用 WebHID 抓包逐帧核对。

**可靠性从 3/8 提到 20/20**，根因是连发路径上后台读线程与写入抢同一句柄导致应答偶发丢失；
改成「一次句柄生命周期内逐帧写、每帧后排空读」即可稳定命中。

### 🎧 迈从耳机的充电状态：之前两个方向都是错的

旧代码把状态字节 `1/2` 当充电、`3` 当已充满——纯凭字面猜的，从未校准。
真机双向采样后定案：

- `[3] = 0x00` → **充电中**（插上充电器后连续 6 次采样）
- `[3] = 0x02` → **放电中**（放电使用时连续 15+ 次采样）

这不只是文案问题：`ShouldNotify` 里有 `if (charging) return false;`，
一个虚假的「充电中」会把低电量提醒**整个压掉**。

### 🎨 多品牌版界面重建

多品牌版此前的 UI 用错了组件（不是亚克力那一套），本版**重建在与完整版相同的
Fluent 亚克力外观层上**：卡片直接复用 `wpf/ThemeService.cs` 与共用的构建块，
两个版本的视觉表现保持一致，但 `wpf/` 自身不被改动。

同时修掉两个只有盯着渲染像素才能发现的缺陷：

- **开关胶囊里的白色细弧**：同一个 `Border` 上同时画 1.5px 同色描边和填充，
  圆角内缘两层抗锯齿覆盖率互相抵消约 76%，浅色卡片从缝里透出来。
  改为填充层 + 描边层两个兄弟节点后消失。
- **充电闪电看不见**：白色闪电画在白色卡片底色上。改为强调色闪电 + 表面色光晕，
  无论底色明暗、电量高低都能看清。

其他界面调整：刷新改为标题栏图标、新增设置图标、去掉关闭按钮（点卡片外即关）、
去掉分隔线、卡片锚定到托盘图标而非屏幕角落、新增「关于」页。

### 🔋 休眠设备显示「睡前电量」

设备休眠后大号数字**始终**是 `--%` —— 那是当前读数，读不到就是读不到。
下面多一行小字说明它睡前还剩多少：

```
PRO X Wireless                                        已休眠
设备离线                                                --%
上次 77% · 2 小时前
```

三条设计约束：**只用于显示**（历史值绝不写回 `Percent`，排序/托盘图标/低电量提醒
一律只看在线读数）、**认不出就不猜**（设备休眠后标识可能退化，只在同一品牌前缀下
只有一个候选时才回退采用）、**会过期**（超过 30 天不再显示）。

### 📦 下载

| 文件 | 大小 | 内存占用（空闲） | 适合 |
| --- | --- | --- | --- |
| `multi-tray-v1.2.0.zip` | 约 0.7 MB | 工作集 61 MB / 提交 15 MB | **多品牌版**：键鼠耳机多台不同品牌 |
| `logi-tray-v1.2.1.zip` | 约 1.4 MB | 工作集 63 MB / 提交 15 MB | 完整版：亚克力毛玻璃 + 三种图标样式 |
| `logi-tray-lite-v1.2.0.zip` | 约 0.9 MB | 工作集 51 MB / 提交 12 MB | 轻量版：只要电量数字，占用尽可能低 |

解压后运行包内的 `一键安装.bat`。需要 **.NET 8 桌面运行时**（脚本会自动检测并引导下载）。

> 内存为**实测空闲值**（启动后不打开任何窗口，等待 9 秒稳定）。
> 多品牌版自 v1.1.0 起改用与完整版相同的 WPF 亚克力层，不再有旧的「47 MB」轻量优势；
> 真正省内存的是**轻量版**（纯 WinForms，不加载 WPF 渲染栈）。

### ⚠️ 协议来源与验证范围

| 品牌 | 实现依据 | 实机验证 |
| --- | --- | --- |
| 罗技 | 本项目 native HID++ 读取器（5 条特性回退） | ✅ 53 项单测 + 实机稳定读到 |
| 迈从 MCHOSE | [同型号开源实现](https://github.com/rafagfran/mchose-v9-pro-battery-tray) | ✅ 电量实机读到；充电状态经**双向采样校准** |
| ATK Z87 键盘 | 官方网页驱动 `hub.atk.pro` v3.2.27 的 WebHID 抓包 | ✅ 实机 **100% · 充电中**，**20/20** |

协议解析层共 **217 项合成帧单测**（无需硬件）：覆盖正常值、边界 `0`/`100`/`101`/`255`、
错误帧头、长度不足、null、ATK 协议 2 的响应头校验、回显拒绝，
以及本版新增的休眠电量回填逻辑（多候选不猜、过期清除、时间文案）。

### 命令行

```bat
multi-tray.exe --list             :: 列出检测到的设备与当前电量（休眠的会带上次电量）
multi-tray.exe --diag-atk         :: 排查 ATK 设备（打印原始收发字节）
multi-tray.exe --diag-mchose      :: 排查迈从耳机的状态字节（watch 模式每秒采样一次）
multi-tray.exe --test-protocols   :: 运行协议解析层自检（无需硬件）
```

### 已知限制

- 同一型号的多台设备会合并为一条显示（去重按 VID/PID/产品名）
- ATK 各型号 Report ID 不统一，本版只对 Z87 完成验证
- 多品牌版刻意不提供续航预测与历史图表，只有电量显示与低电量预警
- 迈从的「已充满」档（`0x01`/`0x03`）尚未观测到，未做映射

---

[README](https://github.com/yixing233/logi-tray#readme) · GPL-3.0
"""

status, rel = api("GET", f"/repos/{REPO}/releases/tags/{TAG}")
if status == 200:
    print(f"release 已存在: {rel['html_url']}")
    rid = rel["id"]
    api("PATCH", f"/repos/{REPO}/releases/{rid}", {"name": TITLE, "body": NOTES})
else:
    print("创建 release…")
    status, rel = api("POST", f"/repos/{REPO}/releases",
                      {"tag_name": TAG, "name": TITLE, "body": NOTES,
                       "draft": False, "prerelease": False})
    if status not in (200, 201):
        print(f"创建失败 {status}: {rel}")
        raise SystemExit(1)
    rid = rel["id"]
    print(f"已创建: {rel['html_url']}")

# assets 端点比 tag 端点更新及时
_, fresh = api("GET", f"/repos/{REPO}/releases/{rid}/assets")
existing = {a["name"]: a["id"] for a in (fresh or [])}
print(f"现有资产: {list(existing)}")

for name in ASSETS:
    path = os.path.join(RELEASE_DIR, name)
    if not os.path.exists(path):
        print(f"  跳过（本地缺失）: {name}")
        continue

    if name in existing:
        st, _ = api("DELETE", f"/repos/{REPO}/releases/assets/{existing[name]}")
        print(f"  删除旧资产 {name} ({st})")

    with open(path, "rb") as f:
        payload = f.read()

    upload = (f"https://uploads.github.com/repos/{REPO}/releases/{rid}/assets"
              f"?name={urllib.parse.quote(name)}")
    st, res = api("POST", upload, raw=payload, ctype="application/zip")
    print(f"  {'上传成功' if st in (200, 201) else f'上传失败 {st}'} {name}"
          f"  {len(payload)/1024:.0f} KB")
    if st not in (200, 201):
        print(f"    {str(res)[:250]}")

_, final = api("GET", f"/repos/{REPO}/releases/{rid}/assets")
print()
print(f"=== {TAG} ===")
print(f"url : https://github.com/{REPO}/releases/tag/{TAG}")
for a in (final or []):
    print(f"  {a['name']:<30} {a['size']/1024/1024:.2f} MB")
