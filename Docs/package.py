#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""打包脚本：产出一个同时兼容 1.6 / 1.7 的 zip。

用法（在仓库根目录）：
    python Docs/package.py

产物：
    dist/RimForex-<modVersion>.zip

包含：
    About/  LoadFolders.xml  1.6/  1.7/  LICENSE  README.md  CHANGELOG.md
不含：
    Source/  Docs/  .github/  dist/  以及各种点文件

打包前会做几项硬检查，任何一项不过就中止：
    · LoadFolders.xml 必须同时声明 v1.6 与 v1.7
    · 1.6 与 1.7 都必须有 Defs / Languages / Textures / Assemblies
    · 1.6 的 DLL 版本串必须是 0.3.1.*
    · 1.7 的 DLL 版本串必须与 About 里的 modVersion 一致
"""

import os
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist")

# 顶层要带进包里的条目（目录会递归）
INCLUDE = ["About", "LoadFolders.xml", "1.6", "1.7", "LICENSE", "README.md", "CHANGELOG.md"]

# 任何位置出现都跳过
SKIP_DIRS = {".git", ".github", "Source", "Docs", "dist", "bin", "obj", "__pycache__"}
SKIP_FILES = {".gitignore", ".gitattributes", "Local.props"}

REQUIRED_SUB = ["Defs", "Languages", "Textures", "Assemblies"]


def fail(msg):
    print("  !! " + msg)
    sys.exit(1)


def read_version(dll_path):
    """从 DLL 里抓 0.3.x+<hash> 版本串。"""
    with open(dll_path, "rb") as f:
        data = f.read()
    m = re.search(rb"(0\.\d+\.\d+\+[0-9a-f]{7,40})", data)
    return m.group(1).decode("ascii") if m else None


def load_about():
    path = os.path.join(ROOT, "About", "About.xml")
    if not os.path.isfile(path):
        fail("找不到 About/About.xml")
    root = ET.parse(path).getroot()
    ver = (root.findtext("modVersion") or "").strip()
    supported = [e.text.strip() for e in root.findall("supportedVersions/li") if e.text]
    return ver, supported


def check_load_folders():
    path = os.path.join(ROOT, "LoadFolders.xml")
    if not os.path.isfile(path):
        fail("找不到 LoadFolders.xml")
    root = ET.parse(path).getroot()
    tags = {child.tag for child in root}
    for need in ("v1.6", "v1.7"):
        if need not in tags:
            fail("LoadFolders.xml 缺少 <%s>" % need)
    print("  OK LoadFolders: v1.6 / v1.7")


def check_version_dir(version):
    base = os.path.join(ROOT, version)
    for sub in REQUIRED_SUB:
        if not os.path.isdir(os.path.join(base, sub)):
            fail("%s/%s 不存在" % (version, sub))
    if not os.path.isdir(os.path.join(base, "Defs")):
        fail("%s/Defs 是空的" % version)
    langs = os.path.join(base, "Languages")
    if not os.listdir(langs):
        fail("%s/Languages 是空的" % version)
    dll = os.path.join(base, "Assemblies", "RimForex.dll")
    if not os.path.isfile(dll):
        fail("%s/Assemblies/RimForex.dll 不存在" % version)
    print("  OK %s: Defs / Languages / Textures / Assemblies 齐全" % version)
    return dll


def iter_files(abs_path):
    if os.path.isfile(abs_path):
        yield abs_path
        return
    for dp, dns, fns in os.walk(abs_path):
        dns[:] = [d for d in dns if d not in SKIP_DIRS]
        for fn in fns:
            if fn in SKIP_FILES:
                continue
            yield os.path.join(dp, fn)


def main():
    print("=" * 70)
    print("打包 RimForex（单包多版本）")
    print("=" * 70)

    mod_version, supported = load_about()
    if not mod_version:
        fail("About.xml 里没有 modVersion")
    print("  About: modVersion=%s, supportedVersions=%s"
          % (mod_version, ", ".join(supported) or "（空）"))
    if "1.6" not in supported or "1.7" not in supported:
        fail("supportedVersions 必须同时包含 1.6 与 1.7")

    check_load_folders()

    dll16 = check_version_dir("1.6")
    dll17 = check_version_dir("1.7")

    ver16 = read_version(dll16)
    ver17 = read_version(dll17)
    print("  1.6 DLL 版本: %s" % ver16)
    print("  1.7 DLL 版本: %s" % ver17)

    if not ver16 or not ver16.startswith("0.3.1"):
        fail("1.6 的 DLL 不是 0.3.1 固化版（实际 %s）" % ver16)
    if not ver17 or not ver17.startswith(mod_version):
        fail("1.7 的 DLL 版本与 About 不一致（DLL %s / About %s）"
             % (ver17, mod_version))
    print("  OK 版本串：1.6 冻结在 0.3.1，1.7 与 About 一致")

    os.makedirs(DIST, exist_ok=True)
    out = os.path.join(DIST, "RimForex-%s.zip" % mod_version)
    if os.path.exists(out):
        os.remove(out)

    count = 0
    total = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        for entry in INCLUDE:
            src = os.path.join(ROOT, entry)
            if not os.path.exists(src):
                fail("缺少打包条目: %s" % entry)
            for full in iter_files(src):
                rel = os.path.relpath(full, ROOT).replace(os.sep, "/")
                zf.write(full, "RimForex/" + rel)
                count += 1
                total += os.path.getsize(full)

    print()
    print("  产物: %s" % out)
    print("  文件: %d 个，未压缩 %.1f KB，zip %.1f KB"
          % (count, total / 1024.0, os.path.getsize(out) / 1024.0))
    print("  顶层: " + " / ".join(sorted(INCLUDE)))
    print()
    print("完成。解压后把 RimForex 整个目录丢进 Mods 即可。")


if __name__ == "__main__":
    main()
