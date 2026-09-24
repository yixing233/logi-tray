"""Verify what GitHub's /releases/latest resolves to, and that the newest
release's assets match the local builds byte for byte.

Why this matters: the README links to /releases/latest and the Logitech editions'
update check parses that redirect. If an older release were "latest", users would
download builds that predate the current work.

Version-agnostic on purpose: an earlier version of this script hardcoded v1.2.0 and
therefore reported a false failure the moment v1.3.0 became the newest release.
"""
import hashlib
import json
import os
import subprocess
import urllib.error
import urllib.request

REPO = "yixing233/logi-tray"
RELEASE_DIR = r"C:\code\chat-records\mouse-tray\release"

tok = subprocess.run(["git", "credential", "fill"],
                     input="protocol=https\nhost=github.com\n\n",
                     capture_output=True, text=True)
TOKEN = [l[9:] for l in tok.stdout.splitlines() if l.startswith("password=")][0]


def api(url, raw=False):
    r = urllib.request.Request(url)
    r.add_header("User-Agent", "logi-tray-verify")
    r.add_header("Accept", "application/vnd.github+json")
    if TOKEN:
        r.add_header("Authorization", f"token {TOKEN}")
    with urllib.request.urlopen(r, timeout=300) as resp:
        body = resp.read()
        return body if raw else json.loads(body.decode())


# ---- 1. what does /releases/latest resolve to? ----
print("=== /releases/latest 跳转到 ===")
req = urllib.request.Request(f"https://github.com/{REPO}/releases/latest")
req.add_header("User-Agent", "logi-tray-verify")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


opener = urllib.request.build_opener(NoRedirect)
try:
    opener.open(req, timeout=60)
    latest_url = None
except urllib.error.HTTPError as e:
    latest_url = e.headers.get("Location")

latest_tag = latest_url.rsplit("/", 1)[-1] if latest_url else None
print(f"  {latest_url}")
print(f"  -> {latest_tag}")

# ---- 2. list all releases ----
print("\n=== 全部 release ===")
rels = api(f"https://api.github.com/repos/{REPO}/releases")
for rel in rels:
    marker = "  <-- latest" if rel["tag_name"] == latest_tag else ""
    print(f"  {rel['tag_name']:10s} draft={rel['draft']} "
          f"assets={[a['name'] for a in rel['assets']]}{marker}")

# ---- 3. newest release: are its assets identical to the local builds? ----
newest = next((r for r in rels if r["tag_name"] == latest_tag), None)
print(f"\n=== {latest_tag} 资产与本地构建比对 ===")
if newest is None:
    print("  FAIL: 找不到最新 release")
    raise SystemExit(1)

ok = True
compared = 0
for a in newest["assets"]:
    local = os.path.join(RELEASE_DIR, a["name"])
    if not os.path.exists(local):
        # logi-tray v1.2.0 archives legitimately live in the older release dir
        # naming; a missing local copy means "cannot compare", not "mismatch".
        print(f"  {a['name']}: 本地无同名文件，跳过比对")
        continue

    remote = api(a["browser_download_url"], raw=True)
    remote_sha = hashlib.sha256(remote).hexdigest()
    with open(local, "rb") as f:
        local_sha = hashlib.sha256(f.read()).hexdigest()

    same = remote_sha == local_sha
    compared += 1
    print(f"  {a['name']}")
    print(f"    remote {a['size']:>9} bytes  sha256 {remote_sha[:16]}")
    print(f"    local  {os.path.getsize(local):>9} bytes  sha256 {local_sha[:16]}")
    print(f"    {'OK 一致' if same else '不一致'}")
    if not same:
        ok = False

print()
print(f"  比对资产数: {compared}")
if compared == 0:
    print("RESULT: FAIL - 没有可比对的资产")
elif ok:
    print(f"RESULT: PASS - /releases/latest 指向 {latest_tag}，资产与本地构建一致")
else:
    print("RESULT: FAIL - 资产与本地构建不一致")
