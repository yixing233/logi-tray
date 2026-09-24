"""Publish v1.2.0 with the multi-feature native reader.

v1.1.0's packages carried a stale native reader (329728 bytes, predating this
work), so the multi-feature support had to go out under a new tag rather than
replacing the existing assets.
"""
import json
import os
import subprocess
import urllib.error
import urllib.parse
import urllib.request

REPO = "yixing233/logi-tray"
TAG = "v1.2.0"
RELEASE_DIR = r"C:\code\chat-records\mouse-tray\release"

ASSETS = [
    ("logi-tray-v1.2.0.zip", "application/zip"),
    ("logi-tray-lite-v1.2.0.zip", "application/zip"),
]

tok = subprocess.run(["git", "credential", "fill"],
                     input="protocol=https\nhost=github.com\n\n",
                     capture_output=True, text=True)
TOKEN = [l[9:] for l in tok.stdout.splitlines() if l.startswith("password=")][0]


def req(url, method="GET", data=None, ctype="application/json"):
    r = urllib.request.Request(url, data=data, method=method)
    r.add_header("Authorization", f"token {TOKEN}")
    r.add_header("User-Agent", "logi-tray-release")
    if data is not None:
        r.add_header("Content-Type", ctype)
    try:
        with urllib.request.urlopen(r, timeout=120) as resp:
            body = resp.read()
            return resp.status, (json.loads(body.decode()) if body else {})
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:500]


BODY = """## logi-tray v1.2.0

本版重点：**大幅扩展罗技鼠标的电量读取适配范围**，并修复三处会导致读数错误的缺陷。

### 适配更多型号：覆盖全部 HID++ 电量接口

此前只尝试 `0x1004`（UnifiedBattery），失败后回退 `0x1000`（BatteryStatus）。
只实现其它电量接口的型号因此会一直显示离线——其中 `0x1001`（BatteryVoltage）
在代码里声明了却从未被使用。

现在按「信息量优先」的顺序依次尝试，任意一条成功即采纳：

| 特性 | 说明 |
| --- | --- |
| `0x1004` UNIFIED_BATTERY | 首选，需先读 capabilities 才能正确解释 |
| `0x1000` BATTERY_STATUS | 较老设备（G304 等） |
| `0x0104` CENTURION_BATTERY_SOC | 较新的 Centurion 系列 |
| `0x1001` BATTERY_VOLTAGE | 只有电压，按放电曲线换算百分比 |
| `0x1F20` ADC_MEASUREMENT | 另一路电压测量 |

**关键修正**：`0x1004` 的 capabilities 字节中，BIT 1 表示设备是否直报百分比。
此前从不读 capabilities，遇到「只报档位」的设备会把无意义的字节当成百分比。
现在会正确区分两种模式，档位还会与能力掩码相与后再判断。

新的 `--probe` 参数可列出每台设备实际实现了哪些电量特性，便于排查特定型号：

```
mouse-tray.exe --probe
```

### 修复会导致误读的三处缺陷

- **设备名含百分号时电量读错**：解析时在整行里找百分比，于是名为
  `G502 100% Edition` 的设备会被读成 100%。现在只解析分隔符之后的电量字段。
- **满电被显示成放电中**：`已充满` 并不包含 `充电` 这个连续子串，
  状态判断顺序有问题，插着线的满电鼠标会被当成放电。现已明确判定顺序。
- **完整版打包时用的是陈旧读取器**：WPF 项目从未引用原生读取器，
  输出目录里的那份是手工拷贝留下的旧文件（329728 字节，而当前构建为 74240），
  导致重新编译原生读取器后，完整版的发布包里仍是旧版本，协议修复根本不生效。
  现已在构建中显式引用，两个版本一致。

### 可验证性

解析逻辑已抽成不依赖 IO 的纯函数层，`--test-battery` 用合成帧跑 **53 项断言**，
覆盖五种接口、`0x1004` 的两种上报模式、档位掩码、充电阶段、电压插值边界、
截断与钳制，全部通过。这两个版本随包附带的读取器都已实测运行该自检。

HID++ 帧构造保持不变：`--dump-frames` 的 10 个帧仍逐字节一致，既有对拍测试通过。

### 下载

| 版本 | 文件 | 说明 |
| --- | --- | --- |
| **完整版** | `logi-tray-v1.2.0.zip` | 亚克力毛玻璃、三种托盘图标样式、主题切换 |
| **轻量版** | `logi-tray-lite-v1.2.0.zip` | 纯 WinForms，占用显著更低，数字图标 |

两版可同时安装运行，配置与历史互不干扰。

### 前置依赖

需要微软官方**免费**的 **.NET 8 桌面运行时**（多数 Windows 11 已预装）：

📥 https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0
打开页面后选择 **.NET Desktop Runtime 8.0.x → Windows → x64**

### 开源协议

本项目基于 **GNU General Public License v3.0 (GPL-3.0)** 开源。
任何基于本项目的衍生作品也须以 GPL-3.0 开源，并保留原始版权声明。
"""


st, rel = req(f"https://api.github.com/repos/{REPO}/releases/tags/{TAG}")
if st == 404:
    payload = json.dumps({
        "tag_name": TAG, "name": "logi-tray v1.2.0", "body": BODY,
        "draft": False, "prerelease": False,
    }, ensure_ascii=False).encode("utf-8")
    st, rel = req(f"https://api.github.com/repos/{REPO}/releases", "POST", payload)
    print(f"created release: {st} {rel.get('html_url') if isinstance(rel, dict) else rel}")
elif isinstance(rel, dict) and "id" in rel:
    payload = json.dumps({"name": "logi-tray v1.2.0", "body": BODY},
                         ensure_ascii=False).encode("utf-8")
    st, _ = req(f"https://api.github.com/repos/{REPO}/releases/{rel['id']}", "PATCH", payload)
    print(f"updated existing release: {st}")
else:
    raise SystemExit(f"cannot resolve {TAG}: {st} {rel}")

rel_id = rel["id"]

st, assets = req(f"https://api.github.com/repos/{REPO}/releases/{rel_id}/assets")
existing = {a["name"]: a["id"] for a in assets} if isinstance(assets, list) else {}
print(f"existing assets: {list(existing)}")

for name, ctype in ASSETS:
    path = os.path.join(RELEASE_DIR, name)
    if not os.path.exists(path):
        print(f"  MISSING locally: {name}")
        continue

    if name in existing:
        req(f"https://api.github.com/repos/{REPO}/releases/assets/{existing[name]}", "DELETE")

    with open(path, "rb") as f:
        data = f.read()

    url = (f"https://uploads.github.com/repos/{REPO}/releases/{rel_id}/assets"
           f"?name={urllib.parse.quote(name)}")
    st, res = req(url, "POST", data, ctype)
    print(f"  {'uploaded' if st in (200, 201) else 'FAILED'} {name}  {len(data)/1024:.0f} KB  ({st})")

st, rel2 = req(f"https://api.github.com/repos/{REPO}/releases/tags/{TAG}")
print(f"\n=== {TAG} ===")
print(f"url  : {rel2['html_url']}")
for a in rel2["assets"]:
    print(f"  {a['name']:30s} {a['size']/1024/1024:5.2f} MB  {a['state']}")
