"""Locate UTF-8 corruption introduced by PowerShell round-trips.

Background: editing files with
    (Get-Content -Raw $f) -replace ... | Set-Content $f
decodes UTF-8 bytes as GBK and re-encodes them, turning Chinese comments into
mojibake. That is how both .csproj files and lite/DetailsForm.cs were damaged
earlier.

Two details matter for this scanner to be trustworthy:

1. The mojibake signatures are written as \\uXXXX escapes, not raw literals.
   With literals the scanner matched its own pattern string and reported itself
   as corrupt -- a false positive that made every run look dirty.

2. A self-test runs first, asserting the pattern still catches a known-bad sample
   and ignores a known-good one. Without it, "fixing" the false positive by
   weakening the pattern would silently disable detection.

Usage:  python tools/check_encoding.py [--quiet]
Exit code 1 when corruption is found, 0 otherwise.
"""
import os
import re
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Mojibake signatures, as escapes: these characters appear when UTF-8 Chinese is
# misread as GBK. U+FFFD is the replacement character.
_SIGNATURE_CODEPOINTS = [
    0xFFFD,  # replacement char
    0x9359, 0x6D93, 0x93C2, 0x9428, 0x934F, 0x6748, 0x7F02, 0x9429,
    0x8930, 0x701B, 0x7481, 0x9422, 0x93C4, 0x935C, 0x95AB, 0x93B5,
    0x6D63, 0x9366,
]
SUSPECT = re.compile("[" + "".join(f"\\u{c:04X}" for c in _SIGNATURE_CODEPOINTS) + "]")

# This scanner is excluded: even with escapes, a future edit could reintroduce
# literals, and the file's purpose is to describe mojibake.
SELF = os.path.join("tools", "check_encoding.py")

TEXT_EXT = (".csproj", ".cs", ".md", ".py", ".bat", ".json", ".txt", ".yml", ".yaml")


def self_test():
    """Prove the pattern still detects corruption before trusting a clean result."""
    # "鍙" and the replacement char must be caught
    bad = "\u9359 mixed with \uFFFD"
    # ordinary Chinese must not be caught
    good = "电池电量正常，环形进度，深色模式"

    assert SUSPECT.search(bad), "自检失败：未能识别乱码样本"
    assert not SUSPECT.search(good), "自检失败：把正常中文误判为乱码"


def main():
    self_test()

    files = subprocess.run(["git", "ls-files"], cwd=REPO,
                           capture_output=True, text=True).stdout.split()
    targets = [f for f in files
               if f.endswith(TEXT_EXT) and f.replace("/", os.sep) != SELF]

    print(f"自检通过 ✓  扫描 {len(targets)} 个受版本控制的文本文件\n")

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
            shown = "".join(dict.fromkeys(hits))[:8]
            corrupt.append((rel, f"{len(hits)} 处可疑字符：{shown}"))

    if not corrupt:
        print("未发现乱码 ✓")
        return 0

    print(f"发现 {len(corrupt)} 个文件含乱码：")
    for rel, why in corrupt:
        print(f"  {rel}: {why}")

    print("\n与 HEAD 对比（区分既有损坏与本次引入）：")
    for rel, _ in corrupt:
        # 必须显式按 UTF-8 解码：subprocess 的 text=True 会用系统区域编码
        # （中文 Windows 上是 GBK）解码 git 输出的 UTF-8 内容，把干净的
        # 中文注释读成乱码，从而把「本次引入」误判成「HEAD 已有问题」。
        raw = subprocess.run(["git", "show", f"HEAD:{rel}"], cwd=REPO,
                             capture_output=True).stdout
        committed = raw.decode("utf-8", errors="replace")
        if SUSPECT.search(committed):
            detail = "HEAD 已损坏（既有问题）"
        else:
            detail = "HEAD 干净 —— 本次改动引入，必须修复"
        print(f"  {rel}: {detail}")

    return 1


if __name__ == "__main__":
    sys.exit(main())
