"""Verify the published release state end to end.

Critical checks:
  1. /releases/latest must resolve to v1.2.0. The README links there and the app's
     update check parses that redirect, so if an older release were "latest" users
     would download a build whose bundled reader predates the protocol work.
  2. The uploaded assets must match the current local builds byte-for-byte.
  3. All releases, so nothing stale is left misleading users.
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
TOKEN = [l[9:] for l in tok.stdout.splitlines() if l.startswith("password=")]
TOKEN = TOKEN[0] if TOKEN else ""


def api(url, raw=False):
    r = urllib.request.Request(url)
    r.add_header("User-Agent", "logi-tray-verify")
    if TOKEN:
        r.add_header("Authorization", f"token {TOKEN}")
    with urllib.request.urlopen(r, timeout=120) as resp:
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
print(f"  {latest_url}")

latest_tag = latest_url.rsplit("/", 1)[-1] if latest_url else None
print(f"  -> {latest_tag}")

# ---- 2. list all releases ----
print("\n=== 全部 release ===")
rels = api(f"https://api.github.com/repos/{REPO}/releases")
for rel in rels:
    marker = "  <-- latest" if rel["tag_name"] == latest_tag else ""
    print(f"  {rel['tag_name']:10s} draft={rel['draft']} prerelease={rel['prerelease']}"
          f"  assets={[a['name'] for a in rel['assets']]}{marker}")

# ---- 3. compare uploaded v1.2.0 assets against local builds ----
print("\n=== v1.2.0 资产与本地构建比对 ===")
rel = next((r for r in rels if r["tag_name"] == "v1.2.0"), None)
if rel is None:
    print("  FAIL: v1.2.0 release not found")
else:
    for a in rel["assets"]:
        local = os.path.join(RELEASE_DIR, a["name"])
        remote = api(a["browser_download_url"], raw=True)

        remote_sha = hashlib.sha256(remote).hexdigest()
        if os.path.exists(local):
            with open(local, "rb") as f:
                local_sha = hashlib.sha256(f.read()).hexdigest()
        else:
            local_sha = "(missing locally)"

        same = remote_sha == local_sha
        print(f"  {a['name']}")
        print(f"    remote {a['size']:>9} bytes  sha256 {remote_sha[:16]}")
        print(f"    local  {os.path.getsize(local) if os.path.exists(local) else 0:>9} bytes  "
              f"sha256 {local_sha[:16]}")
        print(f"    {'OK - identical' if same else 'DIFFERS'}")

# ---- 4. verdict on the latest pointer ----
print()
ok = (latest_tag == "v1.2.0")
print("RESULT:", "PASS - /releases/latest is v1.2.0" if ok
      else f"FAIL - /releases/latest points at {latest_tag}, expected v1.2.0")
