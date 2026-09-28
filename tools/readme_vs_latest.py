#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 README 承诺的下载文件名与 /releases/latest 实际提供的文件名对账（只读）。

为什么需要：README（公开的 origin/main 版本）明确列出三个文件名让用户去
/releases/latest 下载。草稿化 v1.4.0 之后 latest 回落到 v1.3.1，如果那里的
资产名与 README 写的不一致，用户就会在「最新发布页面」上找不到 README 说的包。
这是草稿操作的**副作用**，必须显式对账，不能凭印象说「没问题」。

用法: python tools/readme_vs_latest.py
"""
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "yixing233/logi-tray"
REPO_DIR = r"C:\code\chat-records\mouse-tray"
README = os.path.join(REPO_DIR, "README.md")
PROXY = "http://127.0.0.1:7890"
UA = "logi-tray-readme-check"


def _open(req, follow=True):
    handlers = []
    if not follow:
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *a, **k):
                return None
        handlers.append(NoRedirect)
    last = None
    for proxies in ({}, {"https": PROXY, "http": PROXY}):
        opener = urllib.request.build_opener(
            urllib.request.ProxyHandler(proxies), *handlers)
        try:
            return opener.open(req, timeout=30)
        except urllib.error.HTTPError:
            raise
        except Exception as e:  # noqa: BLE001
            last = e
    raise last


def readme_names(path, label):
    with open(path, encoding="utf-8") as f:
        text = f.read()
    return label, real_names(text)


def real_names(text):
    """抽出真实资产文件名。

    必须排除文档里的占位写法 `logi-tray-vX.Y.Z.zip`（README 用它泛指「版本号随发布变」），
    否则对账会报出一堆假缺失，把真正的缺失淹掉。
    """
    names = re.findall(r"`((?:logi-tray|multi-tray)[A-Za-z0-9._-]*\.zip)`", text)
    return sorted({n for n in names
                   if re.search(r"-v\d+\.\d+", n)})


def main() -> int:
    # README 本地（未推送的修复版）与公开版（origin/main）都要看
    pairs = [readme_names(README, "本地 README (HEAD)")]
    try:
        public = subprocess.run(["git", "show", "origin/main:README.md"],
                                cwd=REPO_DIR, capture_output=True, text=True,
                                encoding="utf-8")
        if public.returncode == 0:
            pairs.append(("公开 README (origin/main)", real_names(public.stdout)))
    except Exception as e:  # noqa: BLE001
        print("读取 origin/main 失败:", e)

    # latest 实际指向
    req = urllib.request.Request(f"https://github.com/{REPO}/releases/latest",
                                 headers={"User-Agent": UA})
    try:
        r = _open(req, follow=False)
        loc = r.headers.get("Location")
    except urllib.error.HTTPError as e:
        loc = e.headers.get("Location")
    tag = loc.rsplit("/", 1)[-1] if loc else None
    print(f"=== /releases/latest 指向 {tag} ===")

    # 该 tag 的实际资产
    url = f"https://github.com/{REPO}/releases/expanded_assets/{tag}"
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    html = _open(req, follow=True).read().decode("utf-8", errors="replace")
    actual = sorted(set(re.findall(r">((?:logi-tray|multi-tray)[A-Za-z0-9._-]*\.zip)<",
                                   html)))
    print("实际资产:")
    for n in actual:
        print(f"   {n}")

    print()
    failed = False
    for label, names in pairs:
        print(f"=== {label} 承诺的下载文件 ===")
        for n in names:
            ok = n in actual
            print(f"   [{'有' if ok else '缺失'}] {n}")
            if not ok:
                failed = True
        print()

    if failed:
        print("RESULT: FAIL - README 承诺的部分文件在 /releases/latest 页面上下载不到")
        return 1
    print("RESULT: PASS - README 承诺的文件都能在 /releases/latest 页面找到")
    return 0


if __name__ == "__main__":
    sys.exit(main())
