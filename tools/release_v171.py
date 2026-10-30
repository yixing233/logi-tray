#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""发布 v1.7.1：修掉「鼠标在用却显示离线」。

功能没有新增，只修一个用户能天天遇到的误报：设备电量读不到时会被直接
标成「已休眠/离线」，但多数情况下设备完全正常，只是那一次读取落在了
设备浅休眠的唤醒窗口之外。

两个层面的修复：
  1. 多品牌版罗技读取路径原本**完全没有重试**（ATK 键盘那条路有，注释里还
     写着「单次失败就报离线会让界面频繁闪 --」）。实测间隔 2 秒连跑 30 次
     有 7 次整轮无应答（约 23%），紧接着重试几乎必定成功。
  2. 设备名查询失败会立刻退化成「设备 #N」。这不只是显示难看：应用按
     `logitech:<设备名>` 记忆「上次电量」，名字一变历史就对不上。

为什么完整版/轻量版看起来没那么严重：它们有 StaleGrace(300s) 容忍窗口，
一次失败不会立刻标离线，只是进入 5 分钟的模糊态。多品牌版没有这层窗口，
所以问题在它身上最显眼。本版也把这层重试补进了共享的 BatteryService，
三个版本一起受益。

为什么必须升版本号：与 v1.7.0 相同的话，已装用户点「检查更新」只会看到
「已是最新」，收不到这个修复。三个版本各前进一位：1.2.5 / 1.2.4 / 1.2.5。

用法：
    python tools/release_v171.py              # 建/更新为草稿并上传资产
    python tools/release_v171.py --publish    # 同上，并转成公开
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
TAG = "v1.7.1"
TITLE = "v1.7.1 · 修掉「鼠标在用却显示离线」"
PROXY = "http://127.0.0.1:7890"

REPO_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELEASE_DIR = os.path.join(REPO_DIR, "release")
README = os.path.join(REPO_DIR, "README.md")
ASSETS = [
    "logi-tray-v1.2.5-setup.exe",
    "logi-tray-lite-v1.2.4-setup.exe",
    "multi-tray-v1.2.5-setup.exe",
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


NOTES = """## v1.7.1 · 修掉「鼠标在用却显示离线」

**只修 bug，没有新功能。** 症状是多品牌版把正在使用的鼠标显示成
「已休眠/离线」，同一时刻完整版与轻量版读数正常。

### 🔍 根因

一次**偶发**的读取失败被当成了事实。原生读取器每轮都会重新打开接收器句柄，
设备在两次轮询之间会掉进浅休眠，唤醒它的第一帧请求有时间窗（实测 650~850ms）。
落在窗口之外就整轮无应答 —— 而设备本身完全正常。

完整版/轻量版有 `StaleGrace` 300 秒的抖动容忍窗口，一次失败不会立刻标离线
（只是进入模糊态）；**多品牌版没有这层窗口**，于是一次失败就直接显示「已休眠」，
这就是为什么只有它表现得这么明显。

### 🛠️ 三处修复

**1. 罗技读取路径补上重试（多品牌版）**

同一个文件里，ATK 键盘那条路本来就有 2 次重试，注释还明确写着「真机连续 4 次
读取会有 1 次整轮无应答」「单次失败就报离线会让界面频繁闪 `--`」—— 罗技这条路
却只调用一次。等于同一个已知问题只修了一半。

实测：间隔 2 秒连跑 30 次有 **7 次读不到（约 23%）**，紧接着重试一次
**100% 成功**。

**2. 设备名查询失败不再立刻退化（原生读取器）**

电量读到了、名字那次往返却超时，原代码直接回退成「设备 #N」。后果不只是
显示难看 —— 应用按 `logi-tray:<设备名>` 记忆「上次电量」，名字一变，
这条历史就对不上了。改为重试一次（第二次给更长的 0.9s 窗口）。

**3. 枚举异常不再让设备整个消失**

原先枚举结果里没有罗技 VID 就直接返回空列表，设备会从界面上**整个消失**，
比显示离线更糟。现在区分「这台机器确实没有罗技设备」与「本次枚举失败」，
后者照常调读取器确认，读不到才如实报离线。

同一套重试也补进了共享的 `BatteryService`，完整版与轻量版一起受益。

### 📊 实测（设备已唤醒，新旧版本交替跑 40 轮）

| | 完全读不到 | 名字退化 |
| --- | --- | --- |
| 修复前 | 10 次 | 2 次 |
| 修复后 | **1 次** | **1 次** |

单独跑修复后的版本 40 轮 0 次异常。

> 仍有约 2% 的残留失败，来自更深的唤醒窗口。彻底解决需要让读取器在进程内
> 做多轮内部重试（而不是靠外层重启进程），那是更大的改动，留待后续。

### 🔢 版本号

| 版本 | 上一版 | 本版 |
| --- | --- | --- |
| 完整版 `logi-tray` | 1.2.4 | **1.2.5** |
| 轻量版 `logi-tray-lite` | 1.2.3 | **1.2.4** |
| 多品牌版 `multi-tray` | 1.2.4 | **1.2.5** |

功能没变也要升版本号：不升的话已装用户点「检查更新」只会看到「已是最新」，
永远收不到这个修复。

### 📦 下载

| 文件 | 大小 | 适合 |
| --- | --- | --- |
| `logi-tray-v1.2.5-setup.exe` | 约 2.9 MB | 完整版：亚克力毛玻璃 + 三种图标样式 |
| `logi-tray-lite-v1.2.4-setup.exe` | 约 2.6 MB | 轻量版：只要电量数字，占用尽可能低 |
| `multi-tray-v1.2.5-setup.exe` | 约 2.6 MB | 多品牌版：键鼠耳机多台不同品牌 |

双击安装包按向导安装，**全程不需要管理员权限**。直接覆盖安装即可，
配置与历史记录都保留。

### ✅ 自检

协议解析层 **259 项**（本版新增 8 项「读取器输出判定」用例）、原生电量解析
**69 项**全部通过。

新增用例针对的正是这次的修复核心：判断「这次读取到底有没有读到东西」的函数，
它决定要不要重试。判错的话修复等于没做，而且不会报错，只会安静地继续误报
离线 —— 所以必须有离线断言兜住。

---

[README](https://github.com/yixing233/logi-tray#readme) · GPL-3.0
"""


def readme_names() -> list:
    with open(README, encoding="utf-8") as f:
        text = f.read()
    names = re.findall(r"`((?:logi-tray|multi-tray)[A-Za-z0-9._-]*-setup\.exe)`", text)
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
        if hit is None:
            # 草稿的 tag_name 是 `untagged-<hash>` 占位串，按真 tag 永远匹配不到。
            # 只按 tag_name 找的话，重跑本脚本会把第一次留下的草稿当成「不存在」，
            # 于是再 POST 一个，线上就出现两个 v1.7.0（本次发布实际踩到了）。
            # 因此退回「按标题找同名草稿」：标题里带 TAG 前缀即可认定是同一版。
            hit = next(
                (r for r in (all_rel or [])
                 if r.get("draft") and str(r.get("name", "")).startswith(TAG)),
                None,
            )
            if hit is not None:
                print(f"找到同名草稿（tag_name={hit['tag_name']!r}），复用而不是新建")
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
        st, res = api("POST", upload, raw=payload, ctype="application/octet-stream")
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
    print("三个资产一致 [OK]")

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
        print(f"  python tools/release_v171.py --publish")
        print(f"  python tools/readme_vs_latest.py")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
