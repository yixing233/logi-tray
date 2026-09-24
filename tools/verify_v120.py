"""End-to-end verification of the published v1.2.0 release.

Downloads both assets, checks integrity and required files, then runs the packaged
native reader's own self-test and launches each app from a clean extraction. This
confirms the multi-feature battery support actually reached the release.
"""
import io
import json
import os
import shutil
import subprocess
import tempfile
import time
import urllib.request
import zipfile

REPO = "yixing233/logi-tray"
TAG = "v1.2.0"

CASES = [
    ("logi-tray-v1.2.0.zip", "logi-tray", "logi-tray.exe"),
    ("logi-tray-lite-v1.2.0.zip", "logi-tray-lite", "logi-tray-lite.exe"),
]

tok = subprocess.run(["git", "credential", "fill"],
                     input="protocol=https\nhost=github.com\n\n",
                     capture_output=True, text=True)
TOKEN = [l[9:] for l in tok.stdout.splitlines() if l.startswith("password=")]
TOKEN = TOKEN[0] if TOKEN else ""


def get(url, raw=False):
    r = urllib.request.Request(url)
    r.add_header("User-Agent", "logi-tray-verify")
    if "api.github.com" in url and TOKEN:
        r.add_header("Authorization", f"token {TOKEN}")
    with urllib.request.urlopen(r, timeout=120) as resp:
        body = resp.read()
        return body if raw else json.loads(body.decode())


rel = get(f"https://api.github.com/repos/{REPO}/releases/tags/{TAG}")
print(f"release : {rel['html_url']}")
print(f"assets  : {[a['name'] for a in rel['assets']]}\n")

overall = True

for asset_name, proc_name, exe_name in CASES:
    asset = next((a for a in rel["assets"] if a["name"] == asset_name), None)
    print(f"=== {asset_name} ===")
    if asset is None:
        print("  FAIL: not attached")
        overall = False
        continue

    data = get(asset["browser_download_url"], raw=True)
    size_ok = len(data) == asset["size"]
    print(f"  downloaded {len(data)} / {asset['size']} bytes  {'OK' if size_ok else 'MISMATCH'}")
    if not size_ok:
        overall = False

    work = os.path.join(tempfile.gettempdir(), f"e2e_{proc_name}")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)

    with zipfile.ZipFile(io.BytesIO(data)) as z:
        corrupt = z.testzip()
        names = z.namelist()
        z.extractall(work)
    print(f"  zip integrity: {'OK' if corrupt is None else 'CORRUPT'}")

    need = [exe_name, "mouse-tray.exe", "app.ico", "一键安装.bat", "卸载.bat",
            "使用说明.txt", "LICENSE"]
    if "lite" not in asset_name:
        need.append("Fonts/lucide.ttf")
    missing = [n for n in need if n not in names]
    print(f"  required files: {'all present' if not missing else 'MISSING ' + str(missing)}")
    if missing:
        overall = False

    # the shipped native reader must carry (and pass) the new parse tests
    native = os.path.join(work, "mouse-tray.exe")
    r = subprocess.run([native, "--test-battery"], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=120)
    out = r.stdout or ""
    ok = r.returncode == 0 and "失败 0 项" in out
    summary = [l.strip() for l in out.splitlines() if "通过" in l and "项" in l]
    print(f"  native --test-battery: {'PASS' if ok else 'FAIL'}  "
          f"{summary[0] if summary else ''}")
    if not ok:
        overall = False

    r = subprocess.run([native, "--help"], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=60)
    probe_ok = "--probe" in (r.stdout or "")
    print(f"  native --probe available: {probe_ok}")
    if not probe_ok:
        overall = False

    # launch the app itself
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Stop-Process -Name {proc_name} -Force -ErrorAction SilentlyContinue"],
                   capture_output=True)
    time.sleep(1.0)
    subprocess.Popen([os.path.join(work, exe_name)], cwd=work)
    time.sleep(11.0)

    o = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"$p = Get-Process -Name {proc_name} -ErrorAction SilentlyContinue | Select-Object -First 1; "
         "if ($p) { \"$($p.Id)|$($p.WorkingSet64)\" }"],
        capture_output=True, text=True, errors="replace")
    line = o.stdout.strip()
    if line and "|" in line:
        pid, ws = line.split("|")
        print(f"  RUNNING pid={pid}  WS={int(ws)/1048576:.1f} MB")
    else:
        print("  FAIL: app did not start")
        overall = False

    dll = os.path.join(work, f"{proc_name}.dll")
    if os.path.exists(dll):
        v = subprocess.run(["powershell", "-NoProfile", "-Command",
                            f"(Get-Item '{dll}').VersionInfo.FileVersion"],
                           capture_output=True, text=True, errors="replace").stdout.strip()
        vok = v.startswith("1.2.0")
        print(f"  shipped version: {v}  {'OK' if vok else 'FAIL'}")
        if not vok:
            overall = False

    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Stop-Process -Name {proc_name} -Force -ErrorAction SilentlyContinue"],
                   capture_output=True)
    shutil.rmtree(work, ignore_errors=True)
    print()

print("RESULT:", "PASS" if overall else "FAIL")
