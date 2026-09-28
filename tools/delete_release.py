#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""删除一个已经作废的 release（含其全部资产），可选连带删除它的 git tag。

用法：
    python tools/delete_release.py v1.4.0              # 只预览，不动手
    python tools/delete_release.py v1.4.0 --yes        # 真删 release 及其资产
    python tools/delete_release.py v1.4.0 --yes --tag  # 连带删掉本地与远端 tag

为什么需要它：发布流程里 `release_v*.py` 只会**建** release，删资产的代码
（`DELETE .../releases/assets/{id}`）也只在「重传前清同名资产」时用到；
要整体撤掉一次发布，此前没有工具。于是 v1.4.0 这种「建了草稿、随后被两代
版本超越、永远不会发布」的草稿只能一直挂在 releases 列表里。

为什么默认只预览：删除是不可逆的公众操作。`--yes` 之前先打印 release id、
name、tag_name、draft 与资产清单，核对无误再动手 —— 尤其是别把当前正在用的
版本删了。参数里不带 --yes 时退出码为 0（预览成功，不是失败）。

为什么删 release 要连资产一起删：GitHub 的 `DELETE /releases/{id}` 会顺带
让资产不可访问，但先显式删资产能让我们在日志里看到每个文件的字节数，
事后对得上「删掉的确实是那三个包」。

为什么 tag 要单独开关：release 与 tag 是两件事。删掉 release 后 tag 仍指向
那个历史提交，本身无害（很多人就是靠 tag 回溯）；只有当一个版本确定不会再发布
（例如 v1.4.0 的 1.2.1/1.2.0/1.2.0 已被 v1.5.0、v1.6.0 两次超越），留着孤立的
tag 才只是噪音。删本地 tag 用 `git tag -d`，远端用 `git push origin :refs/tags/<tag>`
（`--delete` 形式在老版本 git 上不通用）。整个操作可逆：提交还在历史里，
`git tag <tag> <sha>` + push 就能重新挂上。
"""
import json
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "yixing233/logi-tray"
PROXY = "http://127.0.0.1:7890"
HERE = r"C:\code\chat-records\mouse-tray"

USAGE = ("用法: python tools/delete_release.py <tag> [--yes] [--tag]\n"
         "      不带 --yes 只预览；--tag 连带删除本地与远端 tag。")


def token() -> str:
    p = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True, cwd=HERE,
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
            "User-Agent": "logi-tray-delete-release",
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


def find_release(tag: str):
    """按 tag 找 release，草稿也能找到。

    为什么不能只用 `/releases/tags/{tag}`：草稿的 tag_name 是
    `untagged-<hash>` 占位串（见 release_v160.py 的说明），按真 tag 查它必然 404。
    而 v1.4.0 恰好是「占位串被 PATCH 修回去过」的草稿，两种查找都得能兜住。
    """
    code, rel = api("GET", f"/repos/{REPO}/releases/tags/{tag}")
    if code == 200:
        return rel, None
    code, rels = api("GET", f"/repos/{REPO}/releases?per_page=100")
    if code != 200:
        return None, f"查询失败: {code} {rels}"
    for r in rels:
        if r.get("tag_name") == tag:
            return r, None
    tagged = [r.get("tag_name") for r in rels]
    return None, f"找不到 {tag}。现有 release 的 tag: {tagged}"


def git(*args) -> int:
    print("  $ git " + " ".join(args))
    return subprocess.run(["git"] + list(args), cwd=HERE).returncode


def main(argv) -> int:
    if len(argv) < 2 or argv[1].startswith("-"):
        print(USAGE)
        return 1
    tag = argv[1]
    confirm = "--yes" in argv
    also_tag = "--tag" in argv

    rel, err = find_release(tag)
    if err:
        print(err)
        return 1

    assets = rel.get("assets", [])
    print(f"release id : {rel['id']}")
    print(f"name       : {rel['name']}")
    print(f"tag_name   : {rel['tag_name']}")
    print(f"draft      : {rel['draft']}  prerelease={rel['prerelease']}")
    print(f"published  : {rel.get('published_at')}")
    print(f"资产 ({len(assets)}):")
    for a in assets:
        print(f"   {a['name']}  {a['size']} 下载 {a['download_count']}")

    if not confirm:
        print("\n以上为预览。确认要删除请加 --yes"
              + ("（当前还会连带删除本地与远端 tag）" if also_tag else ""))
        return 0

    for a in assets:
        code, _ = api("DELETE", f"/repos/{REPO}/releases/assets/{a['id']}")
        ok = "✓" if code in (204, 200) else f"!! {code}"
        print(f"删资产 {a['name']}: {ok}")

    code, body = api("DELETE", f"/repos/{REPO}/releases/{rel['id']}")
    if code not in (204, 200):
        print(f"删除 release 失败: {code} {body}")
        return 1
    print(f"已删除 release: {tag}")

    if also_tag:
        print("删除 git tag:")
        git("tag", "-d", tag)
        git("push", "origin", f":refs/tags/{tag}")
    else:
        print(f"\n注意：git tag {tag} 仍存在。要一并删除请重跑并加 --tag。")

    code, after = api("GET", f"/repos/{REPO}/releases?per_page=100")
    if code == 200:
        left = [r["tag_name"] for r in after]
        print(f"\n剩余 release ({len(left)}): {left}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
