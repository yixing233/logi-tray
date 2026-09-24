"""Update the v1.1.0 release to carry the two 1.1.0 packages.

The release was first published with logi-tray-v1.0.1.zip / logi-tray-lite-v1.0.0.zip.
The assembly versions were bumped to 1.1.0 so the in-app update check stops
reporting a newer version to users who are already up to date, so the assets need
to match: both are replaced with their 1.1.0 builds.
"""
import json
import os
import subprocess
import urllib.error
import urllib.parse
import urllib.request

REPO = "yixing233/logi-tray"
TAG = "v1.1.0"
RELEASE_DIR = r"C:\code\chat-records\mouse-tray\release"

# assets to end up attached
WANTED = [
    "logi-tray-v1.1.0.zip",
    "logi-tray-lite-v1.1.0.zip",
]
# outdated assets to remove
STALE = [
    "logi-tray-v1.0.1.zip",
    "logi-tray-lite-v1.0.0.zip",
]

proc = subprocess.run(["git", "credential", "fill"],
                      input="protocol=https\nhost=github.com\n",
                      capture_output=True, text=True)
TOKEN = [l[9:] for l in proc.stdout.splitlines() if l.startswith("password=")][0]


def req(url, method="GET", data=None, ctype="application/json"):
    r = urllib.request.Request(url, data=data, method=method)
    r.add_header("Authorization", f"token {TOKEN}")
    r.add_header("User-Agent", "logi-tray-release")
    if data is not None:
        r.add_header("Content-Type", ctype)
    try:
        with urllib.request.urlopen(r) as resp:
            body = resp.read()
            return resp.status, (json.loads(body.decode()) if body else {})
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:400]


st, rel = req(f"https://api.github.com/repos/{REPO}/releases/tags/{TAG}")
if not isinstance(rel, dict) or "id" not in rel:
    raise SystemExit(f"cannot resolve {TAG}: {st} {rel}")

rel_id = rel["id"]
print(f"release {TAG}: id={rel_id}")

st, assets = req(f"https://api.github.com/repos/{REPO}/releases/{rel_id}/assets")
existing = {a["name"]: a["id"] for a in assets} if isinstance(assets, list) else {}
print(f"existing: {list(existing)}")

# 1. delete stale assets
for name in STALE:
    if name in existing:
        st, _ = req(f"https://api.github.com/repos/{REPO}/releases/assets/{existing[name]}",
                    "DELETE")
        print(f"  deleted {name}: {st}")

# 2. upload the 1.1.0 assets
for name in WANTED:
    path = os.path.join(RELEASE_DIR, name)
    if not os.path.exists(path):
        print(f"  MISSING locally: {name}")
        continue

    # remove any same-named asset first so re-runs are idempotent
    if name in existing:
        req(f"https://api.github.com/repos/{REPO}/releases/assets/{existing[name]}", "DELETE")

    with open(path, "rb") as f:
        data = f.read()

    url = (f"https://uploads.github.com/repos/{REPO}/releases/{rel_id}/assets"
           f"?name={urllib.parse.quote(name)}")
    st, res = req(url, "POST", data, "application/zip")
    if st in (200, 201):
        print(f"  uploaded {name}  {len(data)/1024:.0f} KB")
    else:
        print(f"  FAILED {name}: {st} {res}")

# 3. verify
st, rel2 = req(f"https://api.github.com/repos/{REPO}/releases/tags/{TAG}")
print(f"\n=== {TAG} now has ===")
for a in rel2["assets"]:
    print(f"  {a['name']:28s} {a['size']/1024/1024:5.2f} MB  {a['state']}")

names = {a["name"] for a in rel2["assets"]}
ok = set(WANTED) <= names and not (set(STALE) & names)
print("\nRESULT:", "PASS" if ok else "FAIL")
