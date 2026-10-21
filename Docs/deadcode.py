"""RimForex 死代码扫描。

三层检查：
  A. 翻译键：定义了但没有任何代码引用（含字符串常量间接引用）
  B. 未使用文件：目录里存在但从未被引用
  C. 成员扫描：私有/内部字段与方法声明后从未被引用
  D. Scribe 字段考古：ExposeData 里读写的字段是否还有意义

只报告，不修改。C 层的判定需要人工复核，因为反射/序列化会被误报。
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

# 模组根目录 = 本脚本所在目录（Docs/）的上一级。
#
# 不能写死绝对路径：这些脚本要在两台完全不同的机器上跑 ——
# 本地开发机，以及 GitHub Actions 的构建机（那里根本不存在本地那个路径）。
# 用 __file__ 推导，两边都成立。
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Source")
EN = os.path.join(ROOT, r"Languages\English\Keyed\RimForex.xml")
ZH = os.path.join(ROOT, r"Languages\ChineseSimplified (简体中文)\Keyed\RimForex.xml")

findings = []


def note(sev, area, msg):
    findings.append((sev, area, msg))


def read_sources():
    out = {}
    for fn in sorted(os.listdir(SRC)):
        if fn.endswith(".cs"):
            out[fn] = open(os.path.join(SRC, fn), encoding="utf-8").read()
    return out


def strip_comments(code):
    code = re.sub(r"/\*.*?\*/", "", code, flags=re.S)
    code = re.sub(r"//[^\n]*", "", code)
    return code


def main():
    srcs = read_sources()
    all_raw = "\n".join(srcs.values())
    all_code = strip_comments(all_raw)

    print("=" * 74)
    print("A. 翻译键 — 定义了但没有代码引用")
    print("=" * 74)
    used_keys = set(re.findall(r'"(RimForex_[A-Za-z0-9_]+)"', all_raw))
    en = {c.tag for c in ET.parse(EN).getroot()}
    zh = {c.tag for c in ET.parse(ZH).getroot()}
    for label, keys in (("EN", en), ("ZH", zh)):
        stale = sorted(keys - used_keys)
        if stale:
            for k in stale:
                note("DEAD", "i18n", f"{label} 定义了但代码未引用: {k}")
        else:
            print(f"  {label}: 无死键")
    if not (en - used_keys) and not (zh - used_keys):
        print("  无死键")

    print()
    print("=" * 74)
    print("B. 未被引用的文件")
    print("=" * 74)

    # ── 这一节只查【孤儿贴图】（2026-10-21 收紧）──
    #
    # 原来的判据是"文件名有没有在源码或 README 里出现过"，
    # 于是把一大批**根本不需要被名字引用**的文件全报了出来：
    #   · Defs/ 与 Languages/：游戏按目录约定加载，不需要任何地方提到文件名
    #   · About/：同上，About.xml 是固定入口
    #   · Docs/ 与 .github/：开发文档与工作流，不是模组资产
    #   · .gitignore / .gitattributes / *.md / *.txt / *.yml：配置与文档
    #
    # README 从"列全量目录树"改成玩家向的简介之后，这些误报从 13 条涨到 24 条 ——
    # 说明这个判据本来就不成立，不是文档的错。
    #
    # 真正值得查的"孤儿文件"只有一类：**贴图没被任何 Def 引用**。
    # 所以现在只扫 Textures/，其余一律跳过。
    TEXTURE_EXT = (".png", ".jpg", ".jpeg", ".dds", ".psd")

    files = []
    for dirpath, _, fnames in os.walk(ROOT):
        for f in fnames:
            files.append(os.path.join(dirpath, f))

    hay = all_raw + open(os.path.join(ROOT, "README.md"), encoding="utf-8").read()
    tex_dir = os.path.join("Textures", "")
    for p in files:
        rel = os.path.relpath(p, ROOT)
        if not rel.startswith(tex_dir):
            continue
        if not rel.lower().endswith(TEXTURE_EXT):
            continue
        name = os.path.basename(p)
        base = os.path.splitext(name)[0]
        # 贴图在 Def 里通常写成路径片段（不带扩展名），所以两种都查
        if (name in hay) or (base in hay):
            continue
        note("DEAD?", "file", f"贴图未被任何源码/Def 引用: {rel}")
    print("  （见下方汇总）")

    print()
    print("=" * 74)
    print("C. 成员扫描 — 声明后从未被引用")
    print("=" * 74)
    # 收集所有声明
    decls = []   # (file, kind, name, line)
    decl_re = re.compile(
        r"^\s*(?:public|private|protected|internal|static|readonly|const|virtual|override|sealed|abstract|\s)*"
        r"(?:[\w<>\[\],\.\?]+\s+)+?"          # 返回类型
        r"(\w+)\s*(?:\(|=|;|\{)"
    )
    for fn, code in srcs.items():
        for i, ln in enumerate(code.split("\n"), 1):
            s = strip_comments(ln).strip()
            if not s or s.startswith("//"):
                continue
            if "(" in s and ")" in s:
                m = re.match(r"^\s*(?:public|private|protected|internal|static|virtual|override|sealed|abstract|async|\s)*"
                             r"(?:[\w<>\[\],\.\?]+\s+)+(\w+)\s*\(", s)
                if m and m.group(1) not in ("if", "for", "while", "switch", "return", "new", "using", "catch", "lock"):
                    decls.append((fn, "method", m.group(1), i))
            else:
                m = re.match(r"^\s*(?:public|private|protected|internal|static|readonly|const|\s)*"
                             r"(?:[\w<>\[\],\.\?]+\s+)+(\w+)\s*(?:=[^=]|;)", s)
                if m:
                    decls.append((fn, "field", m.group(1), i))

    unref = []
    for fn, kind, name, line in decls:
        if name in ("Main", "ExposeData", "DoWindowContents"):
            continue
        # 引用次数（排除声明那一行本身）
        occurrences = len(re.findall(r"\b%s\b" % re.escape(name), all_code))
        # 声明行本身算 1 次
        if occurrences <= 1:
            unref.append((fn, kind, name, line, occurrences))
    if unref:
        for fn, kind, name, line, occ in sorted(unref):
            note("REVIEW", "member", f"{fn}:{line} {kind} `{name}` 仅出现 {occ} 次")
    else:
        print("  未发现明显未被引用的成员")
    print("  （见下方汇总）")

    print()
    print("=" * 74)
    print("D. Scribe 字段考古 — ExposeData 里的历史兼容字段")
    print("=" * 74)
    for fn, code in srcs.items():
        for i, ln in enumerate(code.split("\n"), 1):
            if "Scribe_" in ln and "legacy" in ln.lower():
                print(f"  {fn}:{i}  {ln.strip()}")
    legacy_hits = re.findall(r"(legacy\w+)", all_code)
    if legacy_hits:
        print("  历史兼容字段:", sorted(set(legacy_hits)))
    else:
        print("  无")

    print()
    print("=" * 74)
    print("汇总")
    print("=" * 74)
    if not findings:
        print("  未发现问题")
    for sev, area, msg in findings:
        print(f"  [{sev}] {area}: {msg}")
    print()
    print(f"共 {len(findings)} 条待处理")
    return 0


if __name__ == "__main__":
    sys.exit(main())
