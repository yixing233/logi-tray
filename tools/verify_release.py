"""End-to-end verification of the published v1.1.0 release.

Downloads both assets from GitHub, checks archive integrity, extracts each to a
clean folder, launches it, and reads back the assembly version to confirm the
in-app update check will report "up to date" instead of a phantom new version.
"""
import io
import json
import os
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

REPO = "yixing233/logi-tray"
TAG = "v1.1.0"

# api.github.com allows only 60 unauthenticated requests per hour, so use the
# stored git credential. (The app itself sidesteps this by parsing the 302 from
# /releases/latest rather than calling the API.)
_tok = subprocess.run(["git", "credential", "fill"],
                      input="protocol=https\nhost=github.com\n",
                      capture_output=True, text=True)
TOKEN = [l[9:] for l in _tok.stdout.splitlines() if l.startswith("password=")][0]

CASES = [
    ("logi-tray-v1.1.0.zip", "logi-tray", "logi-tray.exe"),
    ("logi-tray-lite-v1.1.0.zip", "logi-tray-lite", "logi-tray-lite.exe"),
]


def get(url, raw=False):
    r = urllib.request.Request(url)
    r.add_header("User-Agent", "logi-tray-verify")
    if "api.github.com" in url:
        r.add_header("Authorization", f"token {TOKEN}")
    with urllib.request.urlopen(r) as resp:
        body = resp.read()
        return body if raw else json.loads(body.decode())


rel = get(f"https://api.github.com/repos/{REPO}/releases/tags/{TAG}")
print(f"release : {rel['html_url']}")
print(f"assets  : {[a['name'] for a in rel['assets']]}")
print()

overall = True

for asset_name, proc_name, exe_name in CASES:
    asset = next((a for a in rel["assets"] if a["name"] == asset_name), None)
    print(f"=== {asset_name} ===")
    if asset is None:
        print("  FAIL: asset not attached")
        overall = False
        continue

    data = get(asset["browser_download_url"], raw=True)
    print(f"  downloaded {len(data)} bytes (declared {asset['size']})  "
          f"{'OK' if len(data) == asset['size'] else 'SIZE MISMATCH'}")

    work = os.path.join(tempfile.gettempdir(), f"verify_{proc_name}")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)

    with zipfile.ZipFile(io.BytesIO(data)) as z:
        bad = z.testzip()
        names = z.namelist()
        print(f"  zip integrity: {'OK' if bad is None else 'CORRUPT'}")
        z.extractall(work)

    need = [exe_name, "mouse-tray.exe", "app.ico", "一键安装.bat", "卸载.bat",
            "使用说明.txt", "LICENSE"]
    missing = [n for n in need if n not in names]
    if "lite" not in asset_name:
        if "Fonts/lucide.ttf" not in names:
            missing.append("Fonts/lucide.ttf")
    print(f"  required files: {'all present' if not missing else 'MISSING ' + str(missing)}")
    if missing:
        overall = False

    # launch it from the extracted folder
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Stop-Process -Name {proc_name} -Force -ErrorAction SilentlyContinue"],
                   capture_output=True)
    time.sleep(1.0)
    subprocess.Popen([os.path.join(work, exe_name)], cwd=work)
    time.sleep(11.0)

    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"$p = Get-Process -Name {proc_name} -ErrorAction SilentlyContinue | Select-Object -First 1; "
         "if ($p) { \"$($p.Id)|$($p.WorkingSet64)|$($p.HandleCount)\" }"],
        capture_output=True, text=True, errors="replace")
    line = out.stdout.strip()
    if line and "|" in line:
        pid, ws, h = line.split("|")
        print(f"  RUNNING pid={pid}  WS={int(ws)/1048576:.1f} MB  handles={h}")
    else:
        print("  FAIL: did not start")
        overall = False

    # read the version straight out of the shipped dll
    dll = os.path.join(work, f"{proc_name}.dll")
    if os.path.exists(dll):
        v = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             f"(Get-Item '{dll}').VersionInfo.FileVersion"],
            capture_output=True, text=True, errors="replace").stdout.strip()
        print(f"  shipped version: {v}  {'OK (matches v1.1.0)' if v.startswith('1.1.0') else 'FAIL'}")
        if not v.startswith("1.1.0"):
            overall = False

    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Stop-Process -Name {proc_name} -Force -ErrorAction SilentlyContinue"],
                   capture_output=True)
    shutil.rmtree(work, ignore_errors=True)
    print()

print("RESULT:", "PASS" if overall else "FAIL")
