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

ROOT = r"C:\Users\12435\Desktop\test\RimForex"
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
    files = []
    for dirpath, _, fnames in os.walk(ROOT):
        for f in fnames:
            files.append(os.path.join(dirpath, f))
    # 检查每个文件是否在别处被提及（按文件名与相对路径）
    for p in files:
        rel = os.path.relpath(p, ROOT)
        name = os.path.basename(p)
        if rel.startswith("Source" + os.sep) or rel.startswith("Assemblies"):
            continue
        if name.lower() in ("about.xml", "modicon.png"):
            continue
        # 在源码与 README 中查找提及
        hay = all_raw + open(os.path.join(ROOT, "README.md"), encoding="utf-8").read()
        base = os.path.splitext(name)[0]
        mentioned = (name in hay) or (base in hay)
        if not mentioned:
            note("DEAD?", "file", f"未被任何源码/文档提及: {rel}")
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
