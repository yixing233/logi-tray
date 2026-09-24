"""Publish the v1.3.0 release containing all three editions.

Decision: rather than a separate release for multi-tray, this creates one v1.3.0
release holding all three archives. Reason: the README links users to
/releases/latest and now documents three editions, so a user following that link
must find all three downloads in one place. A separate tag would make "latest"
point at a page missing the logi-tray packages, or leave multi-tray buried in an
older release.

Release notes state plainly which protocol paths are verified and which are not:
reconnaissance showed this machine's ATK keyboard and MCHOSE headset never answer
their vendor frames, so claiming those work would be dishonest.
"""
import json
import os
import subprocess
import urllib.error
import urllib.parse
import urllib.request

REPO = "yixing233/logi-tray"
TAG = "v1.3.1"
TITLE = "v1.3.1 · multi-tray 实机验证：迈从协议可用，ATK 只回显"

RELEASE_DIR = r"C:\code\chat-records\mouse-tray\release"
ASSETS = [
    "logi-tray-v1.2.0.zip",
    "logi-tray-lite-v1.2.0.zip",
    "multi-tray-v1.0.0.zip",
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


NOTES = """## v1.3.1 · multi-tray 实机验证结果

在真实设备上完成验证。**迈从协议确认可用**，ATK Z87 查明只回显不应答。

### ✅ 实机验证结果（6 轮连续读数）

| 设备 | 读取结果 | 稳定性 | 来源 |
| --- | --- | --- | --- |
| 罗技 PRO X Wireless 鼠标 | **81%** | 6/6 成功 | HID++ |
| **迈从 MCHOSE V9 PRO 耳机** | **40%** | **6/6 成功** | 迈从 55 65 |
| ATK Z87 键盘 | `--` | 0/6 | ATK |

**迈从 MCHOSE 的协议已在真机上验证可用**——设备唤醒后稳定读到电量，
这同时确认了「从开源实现获取协议」这条路线是有效的。

### ❌ ATK Z87 为什么读不到

已查明：该键盘对协议帧**原样回显**请求，而不是给出应答。

```
发送: 04 7D 72 02 00 01 07 01 00 00 ...
收到: 04 7D 72 02 00 01 07 01 00 00 ...   ← 完全一致
```

说明这个型号走的是另一套指令，与公开实现覆盖的型号不同。
已在 README 中如实记录为**未验证**。

> **回显防护是必要的**：回显里的 `[7]=0x01` 若不排除，会被误报成「**1%**」——
> 一个完全错误的读数。程序有两道防线（响应头校验 + 回显比对），
> 并有 9 项专门单测覆盖。

### 本版改动

- 新增 `--probe-atk`：打印 ATK 每个接口的原始收发字节，便于进一步定位
- 新增**回显拒绝**逻辑及 9 项单测（协议自检从 88 项增至 **97 项**）
- README 与发布说明更新为实机验证后的结论

### 下载

| 文件 | 大小 | 适合 |
| --- | --- | --- |
| `multi-tray-v1.0.0.zip` | 约 0.5 MB | **多品牌版**：键鼠耳机多台不同品牌设备 |
| `logi-tray-v1.2.0.zip` | 约 1.4 MB | 完整版：亚克力毛玻璃 + 三种图标样式 + 主题切换 |
| `logi-tray-lite-v1.2.0.zip` | 约 0.9 MB | 轻量版：只要电量数字，占用尽可能低 |

解压后运行包内的 `一键安装.bat`。需要 **.NET 8 桌面运行时**（脚本会自动检测并引导下载）。

后两者**只支持罗技设备**，本次未改动（仍为 v1.2.0）。

### ⚠️ 协议来源与验证范围

| 品牌 | 实现依据 | 实机验证 |
| --- | --- | --- |
| 罗技 | 本项目 native HID++ 读取器（5 条特性回退） | ✅ 53 项合成帧单测 + 实机 6/6 读到 |
| 迈从 MCHOSE | [同型号开源实现](https://github.com/rafagfran/mchose-v9-pro-battery-tray) | ✅ **实机 6/6 读到** |
| ATK / VXE / VGN | [开源 ATK 实现](https://github.com/Fan4Metal/ATK_tray) 的协议 1/2 | ❌ 本机 Z87 **回显而非应答**，未读到 |

**协议解析层共 97 项合成帧单测**：覆盖正常值、边界 `0`/`100`/`101`/`255`、
错误帧头、长度不足、null、ATK 协议 2 的响应头校验，以及回显拒绝。

### 命令行

```bat
multi-tray.exe --list             :: 列出检测到的设备与当前电量
multi-tray.exe --probe-atk        :: 排查 ATK 设备（打印原始收发字节）
multi-tray.exe --test-protocols   :: 运行协议解析层自检（无需硬件）
```

### 修复（本版与上一版累计）

- **同一设备重复条目**：原先按接口路径去重，导致一把键盘出现 3 条记录
- **罗技设备整块消失**：判据误用用途页，读不到时应显示为离线而非不显示
- **托盘图标数字过小**：在超采样画布上直接用了 16px 的字号
- **CLI 中文乱码**：`WinExe` 下设置 `Console.OutputEncoding` 会**吞掉全部输出**，
  已改为 `SetOut` 接管 stdout（用 5 种变体实测确认）
- **HICON 泄漏**：每次刷新释放旧图标句柄
- **回显被当成电量**：可能被误读为「1%」，已加两道防线

### 已知限制

- **ATK Z87 读不到电量**：该型号对已知协议帧只回显不应答
- ATK 各型号 Report ID 不统一（公开实现用 `0x08`，实测本机接口是 `0x5A`/`0xED`/`0xCC`）
- 同一型号的多台设备会合并为一条显示
- 多品牌版不含「关于」页与检查更新

---

[README](https://github.com/yixing233/logi-tray#readme) · GPL-3.0
"""

### 命令行

```bat
multi-tray.exe --list             :: 列出检测到的设备与当前电量
multi-tray.exe --probe-atk        :: 排查 ATK 设备（打印原始收发字节）
multi-tray.exe --test-protocols   :: 运行协议解析层自检（无需硬件）
```

### 修复

多品牌版开发过程中发现并修复了若干真实缺陷：

- **同一设备重复条目**：原先按接口路径去重，导致一把键盘出现 3 条记录
- **罗技设备整块消失**：判据误用用途页，读不到时应显示为离线而非不显示
- **托盘图标数字过小**：在超采样画布上直接用了 16px 的字号
- **CLI 中文乱码**：`WinExe` 下设置 `Console.OutputEncoding` 会**吞掉全部输出**，
  已改为 `SetOut` 接管 stdout（用 5 种变体实测确认）
- **HICON 泄漏**：每次刷新释放旧图标句柄
- **回显被当成电量**：ATK 的请求回显可能被误读为「1%」，已加两道防线

### 已知限制

- **ATK Z87 读不到电量**：该型号对已知协议帧只回显不应答
- ATK 各型号 Report ID 不统一（公开实现用 `0x08`，实测本机接口是 `0x5A`/`0xED`/`0xCC`）
- 同一型号的多台设备会合并为一条显示
- 多品牌版不含「关于」页与检查更新

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
