"""Update the v1.1.0 release notes so the documented filenames match the assets."""
import json
import subprocess
import urllib.error
import urllib.request

REPO = "yixing233/logi-tray"
TAG = "v1.1.0"

proc = subprocess.run(["git", "credential", "fill"],
                      input="protocol=https\nhost=github.com\n",
                      capture_output=True, text=True)
TOKEN = [l[9:] for l in proc.stdout.splitlines() if l.startswith("password=")][0]


def req(url, method="GET", data=None):
    r = urllib.request.Request(url, data=data, method=method)
    r.add_header("Authorization", f"token {TOKEN}")
    r.add_header("User-Agent", "logi-tray-release")
    if data is not None:
        r.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(r) as resp:
            body = resp.read()
            return resp.status, (json.loads(body.decode()) if body else {})
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:400]


st, rel = req(f"https://api.github.com/repos/{REPO}/releases/tags/{TAG}")
body = rel["body"]
print(f"current body length: {len(body)}")

before = body

# both editions now ship as 1.1.0
body = body.replace("`logi-tray-v1.0.1.zip`（1.6 MB）", "`logi-tray-v1.1.0.zip`（1.6 MB）")
body = body.replace("`logi-tray-lite-v1.0.0.zip`（1.0 MB）", "`logi-tray-lite-v1.1.0.zip`（1.0 MB）")

# make the version-alignment fix explicit: it is user-visible
addition = """
### 修复

- **修复版本号与发布标签不一致**：此前程序内置版本号为 1.0.0 / 1.0.1，而发布标签为 v1.1.0，
  导致装到最新版的用户打开「关于 → 检查更新」时，会被永久提示"发现新版本"。
  现已将两个版本的版本号统一为 1.1.0，与实际发布保持一致。
"""
if "修复版本号与发布标签不一致" not in body:
    marker = "### 修复\n"
    if marker in body:
        body = body.replace(marker, addition.lstrip("\n") + "\n", 1)
    else:
        body = body.rstrip() + "\n" + addition
    print("added version-alignment note")

payload = json.dumps({"body": body}, ensure_ascii=False).encode("utf-8")
st, _ = req(f"https://api.github.com/repos/{REPO}/releases/{rel['id']}", "PATCH", payload)
print(f"updated: {st}")

st, rel2 = req(f"https://api.github.com/repos/{REPO}/releases/tags/{TAG}")
b = rel2["body"]
print()
print("mentions logi-tray-v1.1.0.zip        :", "logi-tray-v1.1.0.zip" in b)
print("mentions logi-tray-lite-v1.1.0.zip   :", "logi-tray-lite-v1.1.0.zip" in b)
print("no stale 1.0.1 filename              :", "logi-tray-v1.0.1.zip" not in b)
print("no stale lite 1.0.0 filename         :", "logi-tray-lite-v1.0.0.zip" not in b)
print("explains version fix                 :", "版本号与发布标签不一致" in b)
print("still mentions GPL-3.0               :", "GPL-3.0" in b)
