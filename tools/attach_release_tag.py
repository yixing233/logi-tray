#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把某个 release 重新挂到它本该用的 tag 上（修草稿泄漏出来的占位 tag）。

为什么需要：GitHub 给**草稿** release 的 tag_name 是 `untagged-<hash>` 这种占位串，
即使远端已经存在同名 tag 也一样 —— 它把这个占位串当成真正的 tag 存了下来。
结果是 /releases/latest 指向一个 tag_name 为哈希的发布页，而
`/releases/tag/v1.5.0`（README 里给人看的那种地址）却 404。

修法：PATCH release，把 tag_name 改回真实 tag。GitHub 允许在 release 级别改
tag_name，且不会动资产。

用法：
    python tools/attach_release_tag.py                # 只报告当前挂的 tag
    python tools/attach_release_tag.py --fix          # 改挂到 EXPECTED 上
"""
import json
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "yixing233/logi-tray"
PROXY = "http://127.0.0.1:7890"
EXPECTED = "v1.5.0"
# 待修的 release：草稿占位 tag 在发布后才看得出，用 release id 定位最稳妥。
RELEASE_ID = 0


def token() -> str:
    p = subprocess.run(["git", "credential", "fill"],
                       input="protocol=https\nhost=github.com\n\n",
                       capture_output=True, text=True,
                       cwd=r"C:\code\chat-records\mouse-tray")
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
            return opener.open(req, timeout=120)
        except urllib.error.HTTPError:
            raise
        except Exception as e:  # noqa: BLE001
            last = e
    raise last


def api(method, path, body=None):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request("https://api.github.com" + path,
                                 data=data, method=method)
    req.add_header("Authorization", "token " + TOKEN)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "logi-tray-attach-tag")
    if data:
        req.add_header("Content-Type", "application/json")
    try:
        with _open(req) as r:
            b = r.read()
            return r.status, (json.loads(b.decode()) if b else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")


def main(argv) -> int:
    fix = "--fix" in argv

    code, rels = api("GET", f"/repos/{REPO}/releases?per_page=100")
    if code != 200:
        print("查询失败:", code, rels)
        return 1

    targets = [r for r in rels
               if r["id"] == RELEASE_ID or r.get("tag_name", "").startswith("untagged-")]
    if not targets:
        print("没有挂着占位 tag 的 release。")
        return 0

    for r in targets:
        print(f"release id={r['id']}  name={r['name']!r}")
        print(f"  当前 tag_name = {r['tag_name']!r}")
        print(f"  draft={r['draft']}  资产 {[a['name'] for a in r.get('assets', [])]}")
        if not fix:
            continue
        if r["tag_name"] == EXPECTED:
            print("  已经是目标 tag，跳过。")
            continue
        st, upd = api("PATCH", f"/repos/{REPO}/releases/{r['id']}",
                      {"tag_name": EXPECTED, "draft": False, "prerelease": False})
        if st != 200:
            print(f"  改挂失败 {st}: {str(upd)[:300]}")
            return 1
        print(f"  已改挂 -> tag_name={upd['tag_name']!r}  url={upd['html_url']}")
        print(f"  资产 {[a['name'] for a in upd.get('assets', [])]}")

    if not fix:
        print("\n加 --fix 应用修改。")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
