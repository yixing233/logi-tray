#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""查看 / 切换某个 release 的公开状态（草稿 ⇄ 公开）。

用法：
    python tools/set_release_state.py                      # 只看当前发布的当前状态
    python tools/set_release_state.py draft                # 转为草稿（未发布）
    python tools/set_release_state.py publish              # 转为公开
    python tools/set_release_state.py draft   v1.4.0       # 指定 tag

为什么需要它：三个版本的版本号各自独立，发布是个手动动作，而
「代码改完了但还没在自己机器上试过」是常态。草稿状态正好对应这个阶段 ——
对公众不可见、也不会被 /releases/latest 命中，但 release notes 与资产都留着，
本地测完一条命令就能转正，不必重新打包上传。

为什么不用删除代替草稿：删掉就把 notes 和资产一起丢了，还得重新上传三个包；
而草稿随时可以一键公开，代价只有一条命令。

为什么直连失败要再试代理：这台机器上 7890 端口的代理有时是唯一出口，
但代理本身也可能不通，所以两条路都试，失败时把两个错误都报出来 ——
否则只会看到「连不上」，分不出是墙、是代理挂了、还是 token 失效。
"""
import json
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "yixing233/logi-tray"
# 默认操作的那次发布；发新版时改这里，或在命令行用第二个参数指定 tag。
TAG = "v1.5.0"
PROXY = "http://127.0.0.1:7890"

# 退出码：0 = 成功（含「本来就是目标状态」），1 = 出错
USAGE = "用法: python tools/set_release_state.py [draft|publish] [tag]"


def token() -> str:
    p = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True,
        cwd=r"C:\code\chat-records\mouse-tray",
    )
    for line in p.stdout.splitlines():
        if line.startswith("password="):
            return line[len("password="):]
    raise SystemExit("no token: " + p.stdout + p.stderr)


TOKEN = token()


def _open(req):
    last = None
    for proxies in ({}, {"https": PROXY, "http": PROXY}):
        opener = urllib.request.build_opener(urllib.request.ProxyHandler(proxies))
        try:
            return opener.open(req, timeout=60)
        except Exception as e:  # noqa: BLE001
            last = e
    raise last


def api(method: str, path: str, body=None):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        "https://api.github.com" + path,
        data=data,
        method=method,
        headers={
            "Authorization": "token " + TOKEN,
            "Accept": "application/vnd.github+json",
            "User-Agent": "logi-tray-release-state",
        },
    )
    if data:
        req.add_header("Content-Type", "application/json")
    try:
        with _open(req) as r:
            b = r.read()
            return r.status, (json.loads(b.decode("utf-8")) if b else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")


def show(label: str, rel: dict) -> None:
    print(f"{label}: draft={rel['draft']} prerelease={rel['prerelease']} "
          f"published_at={rel.get('published_at')}")
    print("资产:")
    for a in rel.get("assets", []):
        print(f"   {a['name']}  {a['size']}")


def main(argv) -> int:
    want = argv[1].lower() if len(argv) > 1 else None
    if want not in (None, "draft", "publish"):
        print(USAGE)
        return 1
    tag = argv[2] if len(argv) > 2 else TAG

    code, rel = api("GET", f"/repos/{REPO}/releases/tags/{tag}")
    if code != 200:
        print("查询失败:", code, rel)
        return 1

    show(f"当前状态 ({tag})", rel)

    if want is None:
        return 0

    target_draft = want == "draft"
    if rel["draft"] == target_draft:
        print(f"\n已经是{'草稿' if target_draft else '公开'}，无需改动。")
        return 0

    code, updated = api("PATCH", f"/repos/{REPO}/releases/{rel['id']}",
                        {"draft": target_draft, "prerelease": False})
    if code != 200:
        print(f"\n切换失败（目标 {'草稿' if target_draft else '公开'}）:", code, updated)
        return 1

    print()
    show("已切换为", updated)
    if target_draft:
        print("\n草稿状态：公开页面上看不到，/releases/latest 会回落到上一个公开版本。")
        print(f"本地测完后执行  python tools/set_release_state.py publish {tag}  即可转正。")
    else:
        print(f"\n已公开: https://github.com/{REPO}/releases/tag/{tag}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
