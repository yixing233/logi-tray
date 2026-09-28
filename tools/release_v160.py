#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""发布 v1.6.0：三个版本号各推进一位（完整版 1.2.3 / 轻量版 1.2.2 / 多品牌版 1.2.2）。

为什么明明没有功能改动还要发一版：
    v1.5.0 的发布页一度挂在 `untagged-87a162b3058a1567d626` 上 —— README 里写的
    `/releases/tag/v1.5.0` 打不开，`/releases/latest` 却指向那个哈希地址。原因见
    「为什么转公开时要带上 tag_name」。发布脚本已经修好，这一版用新流程完整走
    一遍，把修好的流程固化下来，顺便让三个包的版本号前进一位。

为什么默认先建草稿、要显式 --publish 才公开：
    上传资产是多步操作，中途失败会留下一个「公开但缺包」的发布页，而
    /releases/latest 会立刻指向它、README 承诺的文件却下载不到。先草稿、
    传完核对资产名与 README 一致、再一条命令转正，出错时公众看不到。

为什么转公开时要带上 tag_name（v1.5.0 的教训）：
    GitHub 给**草稿** release 的 tag_name 是 `untagged-<hash>` 这种占位串，即使远端
    已经有同名 tag 也一样。v1.5.0 转公开时只 PATCH 了 {draft, prerelease}，占位串就
    被原样留了下来，发布页永久挂在哈希地址上。现在转公开时一并写入 tag_name=TAG，
    从源头上不让占位串活下来；tools/attach_release_tag.py 仍作为事后的修补工具保留。

用法：
    python tools/release_v160.py              # 建/更新为草稿并上传资产
    python tools/release_v160.py --publish    # 同上，并转成公开
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
TAG = "v1.6.0"
TITLE = "v1.6.0 · 版本号推进与发布流程修复"
PROXY = "http://127.0.0.1:7890"

REPO_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELEASE_DIR = os.path.join(REPO_DIR, "release")
README = os.path.join(REPO_DIR, "README.md")
ASSETS = [
    "logi-tray-v1.2.3.zip",
    "logi-tray-lite-v1.2.2.zip",
    "multi-tray-v1.2.2.zip",
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


NOTES = """## v1.6.0 · 版本号推进与发布流程修复

**本版没有功能改动**：三个包的行为与 v1.5.0 完全一致，差别只在版本号与提交哈希。
仍然发一版，是因为要拿新修好的发布流程完整走一遍 —— 上一次的发布页一度挂在
`untagged-87a162b3058a1567d626` 这个地址上，README 里写的 `/releases/tag/v1.5.0`
反而打不开。修的是发布流程本身，这一版就是它的第一次实战。

如果你想要的是功能变动，看 [v1.5.0 的说明](https://github.com/yixing233/logi-tray/releases/tag/v1.5.0)：
托盘提示统一、完整版与轻量版补上「睡前电量」、ATK Z87 键盘读到电量、
迈从充电状态双向校准、多品牌版界面重建。

### 🔢 版本号

三个版本自 v1.0.0 起就各自独立编号，一次发布里 tag 统一而包版本号互不相同：

| 版本 | 上一版 | 本版 |
| --- | --- | --- |
| 完整版 `logi-tray` | 1.2.2 | **1.2.3** |
| 轻量版 `logi-tray-lite` | 1.2.1 | **1.2.2** |
| 多品牌版 `multi-tray` | 1.2.1 | **1.2.2** |

版本号写在各自的 `.csproj`，运行时由程序集版本读出。因此应用内的「检查更新」
比的是**你自己那一版**的最新包，不是这个 tag。

### 🛠️ 发布流程修复

- **草稿的 tag 是占位串**。GitHub 给草稿 release 的 `tag_name` 是 `untagged-<hash>`，
  即使远端已经有同名 tag 也一样。v1.5.0 转公开时只改了 `draft` 字段，占位串被原样
  留下，`/releases/latest` 于是指向哈希地址。现在转公开时一并写入 `tag_name`，
  并保留 `tools/attach_release_tag.py` 作为事后修补。
- **按 tag 查不到草稿**。`/releases/tags/{tag}` 对草稿必然 404，发布脚本改为退化到
  「列出全部 release 再按 `tag_name` 匹配」。否则重跑会把已存在的草稿当成不存在，
  POST 同名 tag 撞 `422 already_exists`。
- **先草稿、后公开**。上传资产是多步操作，中途失败会留下一个「公开但缺包」的发布页，
  而 `/releases/latest` 会立刻指向它、README 承诺的文件却下载不到。现在默认建草稿，
  核对资产名与 README 承诺一致后才用 `--publish` 转正，出错时公众看不到。
- **README 对账**。`tools/readme_vs_latest.py` 抓线上 `/releases/latest` 与资产页，
  逐个核对 README 里承诺的文件名是否真的能下载到 —— 上一次正是它先报了 FAIL。

### 📦 下载

| 文件 | 大小 | 内存占用（空闲） | 适合 |
| --- | --- | --- | --- |
| `multi-tray-v1.2.2.zip` | 约 0.7 MB | 工作集 61 MB / 提交 15 MB | **多品牌版**：键鼠耳机多台不同品牌 |
| `logi-tray-v1.2.3.zip` | 约 1.4 MB | 工作集 63 MB / 提交 15 MB | 完整版：亚克力毛玻璃 + 三种图标样式 |
| `logi-tray-lite-v1.2.2.zip` | 约 0.9 MB | 工作集 51 MB / 提交 12 MB | 轻量版：只要电量数字，占用尽可能低 |

解压后运行包内的 `一键安装.bat`。需要 **.NET 8 桌面运行时**（脚本会自动检测并引导下载）。

> **三个版本各自独立编号**，所以同一次发布里三个包的版本号互不相同
> （本次是 `1.2.3` / `1.2.2` / `1.2.2`）。应用内的「检查更新」比的是**你自己那一版**
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

    # 草稿没有真 tag（GitHub 先给 `untagged-<hash>`，转公开时才打上 TAG），
    # 所以先按 tag 查、查不到再列全部 release 按 tag_name 找。否则重跑本脚本
    # 会把已存在的草稿当成不存在 → POST 同名 tag → 422 already_exists。
    status, rel = api("GET", f"/repos/{REPO}/releases/tags/{TAG}")
    if status != 200:
        _, all_rel = api("GET", f"/repos/{REPO}/releases?per_page=100")
        hit = next((r for r in (all_rel or []) if r.get("tag_name") == TAG), None)
        status, rel = (200, hit) if hit else (status, rel)
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
        # 必须带上 tag_name：草稿的 tag_name 是 `untagged-<hash>` 占位串，
        # 只改 draft 会把占位串原样留在发布页上（v1.5.0 就是这么挂掉的）。
        st, upd = api("PATCH", f"/repos/{REPO}/releases/{rid}",
                      {"draft": False, "prerelease": False, "tag_name": TAG})
        if st != 200:
            print(f"\n转公开失败 {st}: {upd}")
            return 1
        print(f"\n已公开: {upd['html_url']}")
        print(f"  tag_name = {upd['tag_name']!r}")
        if upd["tag_name"] != TAG:
            print(f"  !! tag_name 不是 {TAG!r} —— 跑 python tools/attach_release_tag.py --fix")
            return 1
    else:
        print(f"\n当前仍是草稿。核对后执行：")
        print(f"  python tools/release_v160.py --publish")
        print(f"  python tools/readme_vs_latest.py")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
