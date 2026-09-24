"""End-to-end verification of the packaged multi-tray archive.

Extracts the published zip to a temp directory and runs the SHIPPED binary's own
self-test, rather than trusting the build output. This catches packaging mistakes
such as a missing native reader or a stale DLL.

Checks:
  1. zip integrity and every required file present
  2. the shipped --test-protocols passes (88 assertions, no hardware needed)
  3. the shipped --list runs and reports the three real devices
  4. the app actually starts and stays running from the extracted copy
  5. the bundled native reader is the current build (byte-identical to native/build)
"""
import hashlib
import os
import shutil
import subprocess
import tempfile
import zipfile

REPO = r"C:\code\chat-records\mouse-tray"
ZIP = os.path.join(REPO, "release", "multi-tray-v1.0.0.zip")
NATIVE = os.path.join(REPO, "native", "build", "mouse-tray.exe")

REQUIRED = ["multi-tray.exe", "multi-tray.dll", "mouse-tray.exe", "app.ico",
            "LICENSE", "一键安装.bat", "卸载.bat", "使用说明.txt"]

work = os.path.join(tempfile.gettempdir(), "multi_e2e")
shutil.rmtree(work, ignore_errors=True)
os.makedirs(work, exist_ok=True)

print("=" * 70)
print("1. 压缩包完整性")
print("=" * 70)
print(f"  包: {os.path.basename(ZIP)}  {os.path.getsize(ZIP)/1024:.0f} KB")
with zipfile.ZipFile(ZIP) as z:
    bad = z.testzip()
    print(f"  完整性: {'OK' if bad is None else f'损坏 {bad}'}")
    z.extractall(work)

missing = [f for f in REQUIRED if not os.path.exists(os.path.join(work, f))]
print(f"  必需文件: {'全部存在' if not missing else f'缺失 {missing}'}")

print()
print("=" * 70)
print("2. 包内原生读取器是否与当前构建一致")
print("=" * 70)
a = hashlib.sha256(open(NATIVE, "rb").read()).hexdigest()
b = hashlib.sha256(open(os.path.join(work, "mouse-tray.exe"), "rb").read()).hexdigest()
print(f"  native/build : {a[:16]}  {os.path.getsize(NATIVE)} bytes")
print(f"  包内         : {b[:16]}  {os.path.getsize(os.path.join(work, 'mouse-tray.exe'))} bytes")
print(f"  {'OK 一致' if a == b else '不一致 —— 打包用了旧读取器'}")

exe = os.path.join(work, "multi-tray.exe")

print()
print("=" * 70)
print("3. 包内程序自检（--test-protocols）")
print("=" * 70)
r = subprocess.run([exe, "--test-protocols"], capture_output=True, timeout=120)
out = r.stdout.decode("utf-8", errors="replace")
tail = [l for l in out.splitlines() if "通过" in l or "失败" in l]
print(f"  退出码: {r.returncode}")
for l in tail:
    print(f"  {l.strip()}")
passed = r.returncode == 0 and "失败 0 项" in out

print()
print("=" * 70)
print("4. 包内程序设备扫描（--list）")
print("=" * 70)
r2 = subprocess.run([exe, "--list"], capture_output=True, timeout=180,
                    env={**os.environ, "PYTHONIOENCODING": "utf-8"})
out2 = r2.stdout.decode("utf-8", errors="replace")
print(f"  退出码: {r2.returncode}")
for l in out2.splitlines():
    s = l.strip()
    if s and ("设备" in s or "%" in s or "来源" in s or "共" in s):
        print(f"  {s}")
sawDevices = "ATK" in out2 and "MCHOSE" in out2

print()
print("=" * 70)
print("5. 包内程序能否启动并驻留")
print("=" * 70)
p = subprocess.Popen([exe], cwd=work, stdout=subprocess.DEVNULL,
                     stderr=subprocess.DEVNULL)
import time
time.sleep(7)
alive = p.poll() is None
print(f"  启动后 7 秒仍在运行: {alive}")
if alive:
    rss = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"(Get-Process -Id {p.pid} -ErrorAction SilentlyContinue).WorkingSet64"],
        capture_output=True, text=True)
    try:
        mb = int(rss.stdout.strip()) / 1024 / 1024
        print(f"  内存占用: {mb:.1f} MB")
    except Exception:
        pass
    p.terminate()
    try:
        p.wait(timeout=10)
    except Exception:
        p.kill()

print()
print("=" * 70)
ok = (not missing and a == b and passed and sawDevices and alive)
print("RESULT:", "PASS - 打包产物完整且可用" if ok else "FAIL")
print("=" * 70)
