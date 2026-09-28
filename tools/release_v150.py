#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""发布 v1.5.0：三版托盘提示统一 + 完整版/轻量版补上休眠前电量。

为什么是新 tag 而不是复用 v1.4.0：
    v1.4.0 那次只创建了**草稿**，从未公开，而且它的 tag 打在 fcf61d6 上，
    资产是「检查更新修复」之前那一版（三个包里还是 1.2.1/1.2.0/1.2.0）。
    既然现在要真正放开给用户，tag 必须落在本次发布的提交上，资产也必须是
    刚打出来的三个包 —— 所以起 v1.5.0，并把旧的 v1.4.0 草稿清掉（见
    tools/README 的发布流程）。

为什么默认先建草稿、要显式 --publish 才公开：
    上传资产是多步操作，中途失败会留下一个「公开但缺包」的发布页，而
    /releases/latest 会立刻指向它、README 承诺的文件却下载不到。先草稿、
    传完核对资产名与 README 一致、再一条命令转正，出错时公众看不到。

用法：
    python tools/release_v150.py              # 建/更新为草稿并上传资产
    python tools/release_v150.py --publish    # 同上，并转成公开
"""
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

REPO = "yixing233/logi-tray"
TAG = "v1.5.0"
TITLE = "v1.5.0 · 三版托盘提示统一，完整版与轻量版补上「睡前电量」"
PROXY = "http://127.0.0.1:7890"

REPO_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELEASE_DIR = os.path.join(REPO_DIR, "release")
README = os.path.join(REPO_DIR, "README.md")
ASSETS = [
    "logi-tray-v1.2.2.zip",
    "logi-tray-lite-v1.2.1.zip",
    "multi-tray-v1.2.1.zip",
]


def token() -> str:
    p = subprocess.run(["git", "credential", "fill"],
                       input="protocol=https\nhost=github.com\n\n",
                       capture_output=True, text=True, cwd=REPO_DIR)
    for line in p.stdout.splitlines():
        if line.startswith("password="):
            return line[len("password="):]
    raise SystemExit("no token: " + p.stdout + p.stderr)


TOKEN = token()


def _open(req):
    """先直连再走代理：这台机器上 7890 有时是唯一出口，但代理本身也可能不通，
    两条路都试并把各自的错误都报出来，否则只会看到「连不上」，分不出是墙、
    是代理挂了、还是 token 失效。"""
    last = None
    for proxies in ({}, {"https": PROXY, "http": PROXY}):
        opener = urllib.request.build_opener(urllib.request.ProxyHandler(proxies))
        try:
            return opener.open(req, timeout=600)
        except urllib.error.HTTPError:
            raise
        except Exception as e:  # noqa: BLE001
            last = e
    raise last


def api(method, path, body=None, raw=None, ctype="application/json"):
    url = path if path.startswith("http") else "https://api.github.com" + path
    data = raw if raw is not None else (json.dumps(body).encode() if body else None)
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("User-Agent", "multi-tray-release")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("Authorization", "token " + TOKEN)
    if data:
        req.add_header("Content-Type", ctype)
    try:
        with _open(req) as resp:
            b = resp.read()
            return resp.status, (json.loads(b.decode()) if b else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")


NOTES = """## v1.5.0 · 三版托盘提示统一，完整版与轻量版补上「睡前电量」

三个版本的托盘悬停提示此前**各说各的**，本版把它们收成同一套说法；
完整版与轻量版另外补上了多品牌版早就有的「休眠前最后一次电量」。

### 🔤 托盘提示统一：一行里混着两种量纲

把鼠标移到托盘图标上，第三个位置显示的东西**取决于你用哪个版本、哪台设备**：

| 版本 / 设备 | 提示的第三段 | 实际含义 |
| --- | --- | --- |
| 多品牌版 · 罗技鼠标 | `放电中` | 充放电状态 |
| 多品牌版 · ATK 键盘 | `良好` | **电量档位** |
| 完整版 / 轻量版 | `预计剩余 8 小时` | 续航预测 |

同一个位置一会儿是状态、一会儿是档位、一会儿是续航，几台设备并排显示时
根本没法横向比较：

```
ATK Z87 Dongle   72%  良好      ← 档位
PRO X Wireless   73%  放电中     ← 状态
MCHOSE V9 PRO   100%  放电中     ← 状态
```

更糟的是完整版与轻量版**根本拿不到状态词**：`BatteryService` 早就把原生读到的
状态解析出来了，却只是传给 `BuildSnapshotFromHistory` 的 `statusText` 形参 ——
**那个形参从来没有被用过**，`BatterySnapshot` 也没有对应的属性，于是提示只能拿
`RemainingTimeText`（续航预测）凑数。多品牌版里 ATK 那一路同样是拿
`LevelText(pct)` 填的状态位。

**现在三个版本共用同一个来源** `shared/BatteryStatusText.cs`，只有四个词：

- `充电中`
- `放电中`
- `充电状态未知` —— 设备自己也没给出状态时如实说不知道，不替它猜
- `已休眠` —— 离线优先，`--%` 的读数不再配一句「放电中」

规则上有两处刻意为之：

- **电量档位一律不进提示**。档位属于详情卡片第二行（`电量等级 · 良好`），
  提示只回答「在充电还是在放电」。判定状态词用**前缀**而非「含『充』」——
  档位里的「充**足**」也含「充」，用子串判断会把它当成状态词原样放出去，
  正是这次要消灭的混用。
- **设备原话优先**。「已充满」「充电中（慢充）」「充电异常」比笼统的「充电中」
  信息更多，原样保留；原生状态位读不出来时打的是 `未知 (0x12)` 这种十六进制
  调试串，对用户是天书，统一收成「充电状态未知」。

顺带修掉一个会**替设备撒谎**的分支：原生状态位无法识别时，`未知 (0x12)`
落不进任何判定，状态词就停在初值「放电中」—— 设备明说「我不知道」，界面却
宣称它在放电。现在这条分支收口成「充电状态未知」。

判定顺序也抽成了可测的纯函数（`BatteryStatusText.FromNativeTail`）：
**「已充满」必须排在「充电」之前** ——「已充满」里并不含「充电」这个连续子串，
顺序写反了不会报错，只会安静地把插着线的鼠标说成放电中。

### 🔋 完整版与轻量版：休眠设备显示「睡前电量」

多品牌版早就有，这次补齐。设备休眠后大号数字**始终**是 `--%` —— 那是当前读数，
读不到就是读不到；下面多一行小字说明它睡前还剩多少：

```
PRO X Wireless                                        已休眠
设备离线                                                --%
上次 77% · 2 小时前
```

三条设计约束：**只用于显示**（历史值单独存在 `LastKnownPercent`，绝不写回
`Percent`，否则大号数字会用旧值冒充当前值；排序、托盘图标与低电量提醒也一律
只看在线读数）、**认不出就不猜**（设备休眠后标识可能退化，只在同一品牌前缀下
只有一个候选时才回退采用）、**会过期**（超过 30 天不再显示）。

### 🔁 检查更新的一个真实缺陷

三个包各自独立编号，但「检查更新」比的是 release 的 **tag**（如 `v1.4.0`），
而 tag 远大于各版自身版本号。结果：从 `v1.4.0` 下载到 `1.2.0` 的用户一按检查
更新就被告知「发现新版本」，点「是」又下载回同一个包 —— **无限提示循环**。

修法：每个版本去发布页找**属于自己的那个包**，比它自己的版本号。匹配用**整名**
正则而非 `StartsWith`：完整版的 `logi-tray` 会前缀命中轻量版的
`logi-tray-lite-v1.2.1.zip`，把轻量版的版本号当成自己的。另外，资产列表**没取到**
（网络抖动）与**该发布确实没有本版的包**是两回事，前者不能报「已是最新」。

### 🔓 ATK Z87 键盘：上一版说「读不到」，这个结论是错的

上一版（v1.3.1）写着「ATK Z87 只回显、读不到」——**那是错的**，本版把它推翻。
实际是两个具体错误：

1. **帧长必须是 64**。发 32 字节会被设备直接拒绝（`Win32 错误 87` = 参数无效）。
2. **必须复现完整的 17 帧前导**：14 帧 `0x1B` LED 指令（`[4..5]` 是 16 位小端
   偏移，逐帧 `+0x18`）→ 2 帧 `0x03` → 最后才是 `0x1A` 电量指令。单发 `0x1A`
   会被拒（`[2]` 回 `0xFF`，该字节语义已从驱动源码确认为「错误」）。

协议不是猜的：从**官方网页驱动 `hub.atk.pro` v3.2.27 的 bundle 源码**里读出来的
（`get batteryLevel()` 取 `[baseOffset+2]`、`batteryCharge() !== 0` 即为充电），
再用 WebHID 抓包逐帧核对。可靠性从 3/8 提到 **20/20**，根因是连发路径上后台读
线程与写入抢同一句柄导致应答偶发丢失；改成「一次句柄生命周期内逐帧写、每帧后
排空读」即可稳定命中。

### 🎧 迈从耳机的充电状态：之前两个方向都是错的

旧代码把状态字节 `1/2` 当充电、`3` 当已充满 —— 纯凭字面猜的，从未校准。
真机双向采样后定案：

- `[3] = 0x00` → **充电中**（插上充电器后连续 6 次采样）
- `[3] = 0x02` → **放电中**（放电使用时连续 15+ 次采样）

这不只是文案问题：`ShouldNotify` 里有 `if (charging) return false;`，
一个虚假的「充电中」会把低电量提醒**整个压掉**。未观测到的取值
（`1,3,4,9,0xFF`）现在一律不表态，显示「充电状态未知」。

### 🎨 多品牌版界面重建

多品牌版此前的 UI 用错了组件（不是亚克力那一套），重建在与完整版相同的
Fluent 亚克力外观层上：卡片直接复用 `wpf/ThemeService.cs` 与共用的构建块，
两个版本的视觉表现保持一致，但 `wpf/` 自身不被改动。

同时修掉两个只有盯着渲染像素才能发现的缺陷：

- **开关胶囊里的白色细弧**：同一个 `Border` 上同时画 1.5px 同色描边和填充，
  圆角内缘两层抗锯齿覆盖率互相抵消约 76%，浅色卡片从缝里透出来。
  改为填充层 + 描边层两个兄弟节点后消失。
- **充电闪电看不见**：白色闪电画在白色卡片底色上。改为强调色闪电 + 表面色光晕。

其他界面调整：刷新改为标题栏图标、新增设置图标、去掉关闭按钮（点卡片外即关）、
去掉分隔线、卡片锚定到托盘图标而非屏幕角落、新增「关于」页。

### 📦 下载

| 文件 | 大小 | 内存占用（空闲） | 适合 |
| --- | --- | --- | --- |
| `multi-tray-v1.2.1.zip` | 约 0.7 MB | 工作集 61 MB / 提交 15 MB | **多品牌版**：键鼠耳机多台不同品牌 |
| `logi-tray-v1.2.2.zip` | 约 1.4 MB | 工作集 63 MB / 提交 15 MB | 完整版：亚克力毛玻璃 + 三种图标样式 |
| `logi-tray-lite-v1.2.1.zip` | 约 0.9 MB | 工作集 51 MB / 提交 12 MB | 轻量版：只要电量数字，占用尽可能低 |

解压后运行包内的 `一键安装.bat`。需要 **.NET 8 桌面运行时**（脚本会自动检测并引导下载）。

> **三个版本各自独立编号**，所以同一次发布里三个包的版本号互不相同
> （本次是 `1.2.2` / `1.2.1` / `1.2.1`）。应用内的「检查更新」比的是**你自己那一版**
> 的最新包，不是这个 tag。
>
> 内存为**实测空闲值**（启动后不打开任何窗口，等待 9 秒稳定）。
> 多品牌版自 v1.1.0 起改用与完整版相同的 WPF 亚克力层，不再有旧的「47 MB」轻量优势；
> 真正省内存的是**轻量版**（纯 WinForms，不加载 WPF 渲染栈）。

### ⚠️ 协议来源与验证范围

| 品牌 | 实现依据 | 实机验证 |
| --- | --- | --- |
| 罗技 | 本项目 native HID++ 读取器（5 条特性回退） | ✅ 实机稳定读到 |
| 迈从 MCHOSE | [同型号开源实现](https://github.com/rafagfran/mchose-v9-pro-battery-tray) | ✅ 电量实机读到；充电状态经**双向采样校准** |
| ATK Z87 键盘 | 官方网页驱动 `hub.atk.pro` v3.2.27 的 WebHID 抓包 | ✅ 实机读到电量与充电位，**20/20** |

协议解析层共 **248 项合成帧单测**（无需硬件）：覆盖正常值、边界 `0`/`100`/`101`/`255`、
错误帧头、长度不足、null、ATK 协议 2 的响应头校验、回显拒绝、休眠电量回填逻辑
（多候选不猜、过期清除、时间文案）、充放电文案与提示行的合成断言（含「档位词
不得混进提示行」的反向用例），以及「检查更新」的资产版本号解析与两步 HTTP 流程
（用进程内桩服务器跑通 302 → 资产页，覆盖只测解析函数碰不到的重定向处理）。

### 命令行

```bat
multi-tray.exe --list             :: 列出检测到的设备与当前电量（休眠的会带上次电量）
multi-tray.exe --check-update     :: 检查更新，打印本版资产版本号与比较结果
multi-tray.exe --diag-atk         :: 排查 ATK 设备（打印原始收发字节）
multi-tray.exe --diag-mchose      :: 排查迈从耳机的状态字节（watch 模式每秒采样一次）
multi-tray.exe --test-protocols   :: 运行协议解析层自检（无需硬件）
```

### 已知限制

- 同一型号的多台设备会合并为一条显示（去重按 VID/PID/产品名）
- ATK 各型号 Report ID 不统一，本版只对 Z87 完成验证
- 多品牌版刻意不提供续航预测与历史图表，只有电量显示与低电量预警
- 迈从的「已充满」档（`0x01`/`0x03`）尚未观测到，未做映射
- ATK 协议 1 与协议 2 的应答里没有充电位，这两路的充放电状态显示为「充电状态未知」

---

[README](https://github.com/yixing233/logi-tray#readme) · GPL-3.0
"""


def readme_names() -> list:
    with open(README, encoding="utf-8") as f:
        text = f.read()
    names = re.findall(r"`((?:logi-tray|multi-tray)[A-Za-z0-9._-]*\.zip)`", text)
    return sorted({n for n in names if re.search(r"-v\d+\.\d+", n)})


def main(argv) -> int:
    publish = "--publish" in argv

    # 资产先在本地说清楚：README 承诺的三个包与接下来要上传的三个包必须同名，
    # 否则「发布成功」只是把不一致固化到线上。
    promised = readme_names()
    print("=== README 承诺的下载文件 ===")
    for n in promised:
        print(f"   {'[本地有]' if os.path.exists(os.path.join(RELEASE_DIR, n)) else '[本地缺]'} {n}")
    if promised != sorted(ASSETS):
        print(f"\nREADME 承诺 {promised}")
        print(f"本次上传   {sorted(ASSETS)}")
        print("两者不一致 —— 先统一再发布。")
        return 1

    print(f"\n=== 本地包 === ({RELEASE_DIR})")
    for name in ASSETS:
        path = os.path.join(RELEASE_DIR, name)
        if not os.path.exists(path):
            print(f"   [缺失] {name} —— 先跑 python tools/package_release.py")
            return 1
        print(f"   {name:<28} {os.path.getsize(path)/1024:>7.0f} KB")

    status, rel = api("GET", f"/repos/{REPO}/releases/tags/{TAG}")
    if status == 200:
        print(f"\nrelease 已存在: {rel['html_url']}")
        print(f"  当前状态: draft={rel['draft']} prerelease={rel['prerelease']}")
        rid = rel["id"]
        st, _ = api("PATCH", f"/repos/{REPO}/releases/{rid}", {"name": TITLE, "body": NOTES})
        print(f"  说明已刷新 ({st})")
    else:
        print(f"\n创建 release {TAG}（草稿）…")
        status, rel = api("POST", f"/repos/{REPO}/releases",
                          {"tag_name": TAG, "name": TITLE, "body": NOTES,
                           "draft": True, "prerelease": False})
        if status not in (200, 201):
            print(f"创建失败 {status}: {rel}")
            return 1
        rid = rel["id"]
        print(f"  已创建: {rel['html_url']}")

    _, fresh = api("GET", f"/repos/{REPO}/releases/{rid}/assets")
    existing = {a["name"]: a["id"] for a in (fresh or [])}
    print(f"\n现有资产: {list(existing)}")

    for name in ASSETS:
        path = os.path.join(RELEASE_DIR, name)
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
    got = sorted(a["name"] for a in (final or []))
    print(f"\n=== {TAG} 资产核对 ===")
    for a in (final or []):
        print(f"  {a['name']:<30} {a['size']/1024/1024:.2f} MB")
    if got != sorted(ASSETS):
        print(f"\n资产与预期不符：实际 {got} 期望 {sorted(ASSETS)} —— 不自动公开。")
        return 1
    print("三个资产一致 ✓")

    if publish:
        st, upd = api("PATCH", f"/repos/{REPO}/releases/{rid}",
                      {"draft": False, "prerelease": False})
        if st != 200:
            print(f"\n转公开失败 {st}: {upd}")
            return 1
        print(f"\n已公开: https://github.com/{REPO}/releases/tag/{TAG}")
    else:
        print(f"\n当前仍是草稿。核对后执行：")
        print(f"  python tools/release_v150.py --publish")
        print(f"  python tools/readme_vs_latest.py")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
