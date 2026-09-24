"""Locate any UTF-8 corruption introduced by PowerShell round-trips.

Earlier in this session I edited .csproj files with
(Get-Content -Raw) ... | Set-Content, which decodes UTF-8 as GBK and re-encodes it,
turning Chinese comments into mojibake. This scans the tracked text files for the
tell-tale patterns and compares each against its committed version.
"""
import os
import re
import subprocess

REPO = r"C:\code\chat-records\mouse-tray"

# Mojibake signatures: common Chinese words that appear when UTF-8 bytes are read
# as GBK. 鍙/涓/鏂/鐨 are the usual giveaways.
SUSPECT = re.compile(r"[\uFFFD]|鍙|涓|鏂|鐨|鍏|杈|缂|鐩|褰|瀛|璁|鐢|鏄|鍜|閫|鎵|浣|鍦")

files = subprocess.run(["git", "ls-files"], cwd=REPO, capture_output=True,
                       text=True).stdout.split()
targets = [f for f in files if f.endswith((".csproj", ".cs", ".md", ".py", ".bat", ".json", ".txt"))]

print(f"扫描 {len(targets)} 个受版本控制的文本文件\n")

corrupt = []
for rel in targets:
    path = os.path.join(REPO, rel.replace("/", os.sep))
    if not os.path.exists(path):
        continue
    try:
        with open(path, encoding="utf-8") as f:
            text = f.read()
    except UnicodeDecodeError:
        corrupt.append((rel, "无法按 UTF-8 解码"))
        continue

    hits = SUSPECT.findall(text)
    if hits:
        corrupt.append((rel, f"{len(hits)} 处可疑字符，例如 {''.join(dict.fromkeys(hits))[:8]}"))

if not corrupt:
    print("未发现乱码")
else:
    print(f"发现 {len(corrupt)} 个文件含疑似乱码：")
    for rel, why in corrupt:
        print(f"  {rel}: {why}")

    # For each, confirm whether the committed version is clean
    print("\n与 HEAD 版本对比（判断是本次引入还是既有）：")
    for rel, _ in corrupt:
        committed = subprocess.run(["git", "show", f"HEAD:{rel}"], cwd=REPO,
                                   capture_output=True, text=True,
                                   errors="replace").stdout
        head_bad = bool(SUSPECT.search(committed))
        work_bad = True
        print(f"  {rel}")
        print(f"    HEAD 含乱码: {head_bad}")
        print(f"    工作区含乱码: {work_bad}")
        print(f"    -> {'既有问题（HEAD 就已损坏）' if head_bad else '本次新引入，需修复'}")
