#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""查询线上发布状态：列出全部 release（含草稿）与 /releases/latest 的回落结果。

只读，不做任何修改。存在的理由：三个版本各自独立编号，而「哪个是公开的、
latest 指向哪里、页面上挂的包是什么版本」三件事必须能一眼看清 ——
用户问「现在线上最新版本都是啥情况」时，答案只能来自这里。
"""
import json
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "yixing233/logi-tray"
PROXY = "http://127.0.0.1:7890"


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
            return opener.open(req, timeout=45)
        except Exception as e:  # noqa: BLE001
            last = e
    raise last


def api(path: str, follow: bool = True):
    """follow=False 时不自动跟随重定向，用来观察 /releases/latest 的 302 指向。"""
    req = urllib.request.Request(
        "https://api.github.com" + path,
        headers={
            "Authorization": "token " + TOKEN,
            "Accept": "application/vnd.github+json",
            "User-Agent": "logi-tray-release-state",
        },
    )
    if follow:
        try:
            with _open(req) as r:
                b = r.read()
                return r.status, (json.loads(b.decode("utf-8")) if b else None)
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode(errors="replace")
    # 手动观察重定向
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *a, **k):
            return None

    opener = urllib.request.build_opener(NoRedirect)
    try:
        with opener.open(req, timeout=45) as r:
            return r.status, r.headers.get("Location")
    except urllib.error.HTTPError as e:
        return e.code, e.headers.get("Location")


def main() -> int:
    # 1) latest 指向哪个 tag（用户实际点下载页看到的就是这个）
    #    注意：API 的 /releases/latest 直接返回 release JSON（HTTP 200），
    #    并不像网页版那样发 302；所以要读 body 里的 tag_name，不能读 Location。
    code, latest = api("/repos/%s/releases/latest" % REPO)
    tag = latest.get("tag_name") if isinstance(latest, dict) else None
    print("=== /releases/latest 指向 ===")
    if tag:
        print(f"  {tag}   (HTTP {code})  draft={latest['draft']}")
    else:
        print(f"  (取不到, HTTP {code}) {str(latest)[:120]}")

    # 2) 全部 release（含草稿）
    code, rels = api("/repos/%s/releases?per_page=100" % REPO)
    if code != 200:
        print("查询 release 列表失败:", code, rels)
        return 1

    print()
    print("=== 全部 release ===")
    for r in rels:
        state = "草稿" if r["draft"] else ("预发布" if r["prerelease"] else "公开")
        print(f"\n[{state}] {r['tag_name']}   ({r['name']})")
        print(f"    published_at={r.get('published_at')}  created={r['created_at']}")

        # 资产版本号按文件名解析，因为三版各自独立编号
        for a in r.get("assets", []):
            print(f"      {a['name']:<32} {a['size']/1024:>7.0f} KB  "
                  f"下载数 {a['download_count']}")

    # 3) 汇总公开 release 里的包版本
    print()
    print("=== 当前公开的包版本（按资产名解析）===")
    import re
    for r in rels:
        if r["draft"]:
            continue
        print(f"  {r['tag_name']}:")
        for a in r.get("assets", []):
            m = re.match(r"^([A-Za-z0-9][A-Za-z0-9._-]*?)-v(\d+\.\d+(?:\.\d+)?)\.zip$",
                         a["name"])
            if m:
                print(f"     {m.group(1):<16} -> {m.group(2)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
