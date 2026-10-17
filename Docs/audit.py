"""RimForex 交付前校验：翻译键一致性 + XML 合法性 + 关键常量核对。
用法: python audit.py
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

fail = 0


def load_keys(path):
    return {c.tag: (c.text or "") for c in ET.parse(path).getroot()}


def main():
    global fail

    # ---- 1. 代码引用的翻译键 vs 两种语言的定义 ----
    # 把 RimForex_ 开头的字面量都当作翻译键，但【排除 Def 名】。
    #
    # 为什么用排除法而不是"只认后面跟 .Translate 的"：
    # 不少键是【当参数传】的（例如 DrawStat(rect, "RimForex_Equity", ...) 内部才 Translate），
    # 收紧正则会把它们全漏掉，反而更糟。
    #
    # 需要排除的是 FxMoodSystem.TierDefNames —— 那是 ThoughtDef 的 defName，
    # 名字恰好也是 RimForex_ 开头，最早被误判成"引用了但不存在的键"。
    def_names = set()
    mood_src = os.path.join(SRC, "FxMood.cs")
    if os.path.exists(mood_src):
        mtxt = open(mood_src, encoding="utf-8").read()
        block = re.search(r"TierDefNames\s*=\s*\{(.*?)\}", mtxt, re.S)
        if block:
            def_names = set(re.findall(r'"(RimForex_[A-Za-z0-9_]+)"', block.group(1)))

    used = set()
    for fn in os.listdir(SRC):
        if fn.endswith(".cs"):
            txt = open(os.path.join(SRC, fn), encoding="utf-8").read()
            used |= set(re.findall(r'"(RimForex_[A-Za-z0-9_]+)"', txt))
    used -= def_names

    en = load_keys(EN)
    zh = load_keys(ZH)

    print("翻译键: code=%d en=%d zh=%d" % (len(used), len(en), len(zh)))
    for label, got in (("缺失EN", used - set(en)), ("缺失ZH", used - set(zh)),
                       ("残留EN", set(en) - used), ("残留ZH", set(zh) - used),
                       ("中英键集差异", set(en) ^ set(zh))):
        if got:
            fail += 1
            print("  !! %s: %s" % (label, sorted(got)))
        else:
            print("  OK %s: 无" % label)

    # ---- 2. 占位符一致性 ----
    bad = []
    for k in sorted(set(en) & set(zh)):
        pe = set(re.findall(r"\{(\d+)\}", en[k]))
        pz = set(re.findall(r"\{(\d+)\}", zh[k]))
        if pe != pz:
            bad.append((k, sorted(pe), sorted(pz)))
    if bad:
        fail += 1
        for k, pe, pz in bad:
            print("  !! 占位符不一致 %s: EN=%s ZH=%s" % (k, pe, pz))
    else:
        print("  OK 占位符: 全部一致 (%d 个键)" % len(set(en) & set(zh)))

    # ---- 3. XML 合法性 ----
    xmls = []
    for dirpath, _, files in os.walk(ROOT):
        for f in files:
            if f.endswith(".xml"):
                xmls.append(os.path.join(dirpath, f))
    for p in xmls:
        try:
            ET.parse(p)
            print("  OK XML %s" % p.replace(ROOT, ""))
        except Exception as e:
            fail += 1
            print("  !! XML 解析失败 %s: %s" % (p.replace(ROOT, ""), e))

    # ---- 3b. DefInjected 字段路径必须真实存在 -------------------------------
    # 这是最容易写错又最难发现的一类错误：路径写错时 XML 完全合法、
    # 游戏也能启动，只是翻译静默不生效（界面继续显示英文）。
    # 这里把 Defs\*.xml 里出现的所有 defName 收集起来，
    # 再检查每个 DefInjected 键的第一段是不是某个真实 defName。
    def_names = set()
    def_dir = os.path.join(ROOT, "Defs")
    if os.path.isdir(def_dir):
        for f in os.listdir(def_dir):
            if not f.endswith(".xml"):
                continue
            try:
                root = ET.parse(os.path.join(def_dir, f)).getroot()
            except Exception:
                continue
            for d in root:
                for ch in d:
                    if ch.tag == "defName" and ch.text:
                        def_names.add(ch.text.strip())

    inj_dir = os.path.join(ROOT, "Languages")
    checked = 0
    if os.path.isdir(inj_dir):
        for dirpath, _, files in os.walk(inj_dir):
            if "DefInjected" not in dirpath:
                continue
            for f in files:
                if not f.endswith(".xml"):
                    continue
                full = os.path.join(dirpath, f)
                try:
                    root = ET.parse(full).getroot()
                except Exception:
                    continue          # XML 合法性上面已经报过了
                for node in root:
                    key = node.tag
                    first = key.split(".")[0]
                    checked += 1
                    if first not in def_names:
                        fail += 1
                        print("  !! DefInjected 键 %s 指向不存在的 defName '%s' (%s)"
                              % (key, first, f))
    if checked == 0:
        print("  OK DefInjected: 无条目")
    else:
        print("  OK DefInjected: %d 个键均指向真实 defName" % checked)

    # ---- 4. 关键常量核对 ----
    core = open(os.path.join(SRC, "Core.cs"), encoding="utf-8").read()
    expect = {
        "FeeRate": "0.0002f",
        "MinFee": "2f",
        "CriticalThreshold": "0.60f",
        "StopOutFraction": "0.5f",
        "SpreadCoef": "0.0008f",
        "SpreadExponent": "1.0f",
        "SpreadFloor": "0.0007f",
        "MaxSpread": "0.03f",
        "TicksPerHour": "GenDate.TicksPerHour",
        "TicksPerDay": "GenDate.TicksPerDay",
        "TicksPerRealSecond": "60",
        "GapMinMult": "3f",
        "GapMaxMult": "9f",
        "ExtremeGapMinMult": "14f",
        "ExtremeGapMaxMult": "34f",
        "StopOutVolCoverage": "0.28f",
        "MaxSpreadImpactOnMargin": "0.35f",
    }
    for name, val in expect.items():
        m = re.search(r"public const \w+ %s = ([^;]+);" % name, core)
        got = m.group(1).strip() if m else "(未找到)"
        ok = got == val
        if not ok:
            fail += 1
        print("  %s 常量 %s = %s (期望 %s)" % ("OK" if ok else "!!", name, got, val))

    # ---- 4a. 由模组设置驱动的运行时属性 ------------------------------------
    # 这几个原本是编译期常量，改成滑块后必须确认它们仍是"属性"而不是被写回常量，
    # 否则滑块会静默失效（编译能过，但改了设置没反应）。
    dynamic_props = ["TicksPerStep", "StepsPerDay", "QuotesPerCandle", "TicksPerCandle",
                     "GapChancePerDay", "GapChancePerStep",
                     "ExtremeGapChancePerDay", "ExtremeGapChancePerStep"]
    for name in dynamic_props:
        is_prop = re.search(r"public static \w+ %s\s*(=>|\{)" % name, core) is not None
        is_const = re.search(r"public const \w+ %s\s*=" % name, core) is not None
        if is_prop and not is_const:
            print("  OK 动态属性 %s（由设置驱动）" % name)
        else:
            fail += 1
            print("  !! %s 应当是动态属性（prop=%s const=%s）—— 滑块会失效"
                  % (name, is_prop, is_const))

    # ---- 4a2. 关键公式：日频率 -> 每步频率 的折算不能丢 ----
    if "System.Math.Pow(1.0 - perDay, 1.0 / StepsPerDay)" in core:
        print("  OK 跳空概率按日频率折算到每步（改报价频率不会连带改变风险）")
    else:
        fail += 1
        print("  !! 找不到跳空概率的日->步折算公式 —— 报价频率与跳空频率会互相干扰")

    # 波动缩放必须用 StepsPerDay，否则改节奏会改变日波动率。
    # 判据放宽为"出现 Mathf.Sqrt(StepsPerDay)"—— 算式可能被改写
    #（例如并入 AdvanceSubStep 后变成 DailyVol * c.volatility / Mathf.Sqrt(...)），
    # 只要仍按步数开方缩放就算通过。
    if "Mathf.Sqrt(StepsPerDay)" in core:
        print("  OK 波动按 1/sqrt(StepsPerDay) 缩放（日波动率恒定）")
    else:
        fail += 1
        print("  !! 波动缩放未使用 StepsPerDay —— 改报价频率会改变日波动率")

    # ---- 4a3. 步内子采样（T1）的结构约束 ----
    # 子步只允许改变"采样密度"，不允许改变步边界位移。
    # 关键实现特征是：位移在步边界一次算好（stepLogTarget），
    # 桥的两端必须为 0（bridgeNoise[SubSteps] = 0f），否则开盘价会被污染。
    if "stepLogTarget" in core and "stepLogStart" in core:
        print("  OK 步位移在边界一次算好（整步预算），子步只负责走完")
    else:
        fail += 1
        print("  !! 找不到整步预算字段 —— 子步可能改动了步边界分布")

    # 盘内路径（桥/振荡子）必须两端钉零。数组名会变（bridgeNoise → bn），
    # 所以用"任意下标数组 + [SubSteps] = 0f"的宽松模式匹配。
    if re.search(r"\w+\[SubSteps\]\s*=\s*0f", core):
        print("  OK 盘内路径两端钉为 0（开盘/收盘不被污染）")
    else:
        fail += 1
        print("  !! 盘内路径右端未钉为 0 —— K 线收盘价会偏离步边界价")

    if re.search(r"\w+\[0\]\s*=\s*0f", core):
        print("  OK 盘内路径左端也钉为 0（开盘价不被污染）")
    else:
        fail += 1
        print("  !! 盘内路径左端未钉为 0 —— K 线实体会被系统性放大（曾实测 +7.9%）")

    if re.search(r"AdvanceSubStep\(c\)", core) and "SamplePrices" not in core:
        print("  OK 采样已移入子步循环（否则影线不会变长）")
    else:
        fail += 1
        print("  !! 采样没有随子步进行，或残留旧的 SamplePrices —— 影线不会改善")

    # ---- 4a4. 读档保护（回归防线）------------------------------------------
    # stepLogStart / stepLogTarget 不持久化，默认值 0，而 Exp(0) = 1 = MinRate。
    # 读档后若在 TickStep 建立预算之前就推进子步，价格会被瞬间打到下限，
    # 再靠均值回归慢慢爬回锚点 —— 症状是"每次读档所有货币跌到极低价再涨回来"。
    # 必须有 stepBudgetValid 标志，且在"写"与"用"两侧都设防。
    if re.search(r"if \(!c\.stepBudgetValid\) continue;", core):
        print("  OK AdvanceSubSteps 跳过未建立预算的货币（读档保护）")
    else:
        fail += 1
        print("  !! AdvanceSubSteps 未做步预算检查 —— 读档后价格会被打到 MinRate")

    if re.search(r"if \(!c\.stepBudgetValid\) return;", core):
        print("  OK AdvanceSubStep 内部二次设防")
    else:
        fail += 1
        print("  !! AdvanceSubStep 内部未设防（单点失效即崩价）")

    if re.search(r"c\.stepBudgetValid = true;", core) and "BeginStepBudget" in core:
        print("  OK TickStep 与 BeginStepBudget 都会建立预算")
    else:
        fail += 1
        print("  !! 预算建立点缺失 —— 读档后可能永远不推进")

    # 未持久化的运行时字段不得出现在 ExposeData 里（存了反而会在读档时被旧值污染）
    bad_persist = [f for f in ("stepLogStart", "stepLogTarget", "stepBudgetValid", "subStep")
                   if re.search(r"Scribe_Values\.Look\(ref %s" % f, core)]
    if bad_persist:
        fail += 1
        print("  !! 运行时字段被持久化了：%s（不该存档）" % ", ".join(bad_persist))
    else:
        print("  OK 运行时字段未持久化（stepLogStart / stepLogTarget / stepBudgetValid / subStep）")

    # ---- 4a5. 多周期聚合（时K / 日K）-------------------------------------
    if re.search(r"public const int MaxCandles = 100;", core):
        print("  OK MaxCandles = 100（三级缓冲共用同一上限）")
    else:
        fail += 1
        print("  !! MaxCandles 不是 100 —— 图表历史长度与文档不符")

    for fld in ("hourCandles", "dayCandles", "hourStartTick", "dayStartTick"):
        if fld not in core:
            fail += 1
            print("  !! 缺少多周期字段 %s" % fld)
    print("  OK 时K / 日K 的缓冲与相位字段齐备")

    if "PeriodTicksOf" in core and re.search(r"FxGameComponent\.TicksPerHour", core) \
            and re.search(r"FxGameComponent\.TicksPerDay", core):
        print("  OK 周期长度引用原版常量（TicksPerHour / TicksPerDay）")
    else:
        fail += 1
        print("  !! 周期长度未引用原版常量 —— 可能又在自己推时间")

    # 区间判定必须是"所属 bucket 变化"，不能是"差值 >= 周期"。
    # 差值法在低帧率跨过多个周期时会漏掉边界，导致 K 线缺根。
    if re.search(r"int bucket = nowTick / periodTicks;", core):
        print("  OK 封根用绝对区间判定（跨多个周期也不漏根）")
    else:
        fail += 1
        print("  !! 封根未用区间判定 —— 低帧率下会漏掉整根 K 线")

    # ---- 4a6. 图表交互（滚轮缩放 + K 线悬停）-------------------------------
    win = open(os.path.join(SRC, "Window_FxTerminal.cs"), encoding="utf-8").read()

    # 中键拖动已于 2026-10-16 移除（一直不通）。它不该复活 ——
    # 留着会让人以为平移可用，干扰后续重构。
    if re.search(r"chartPan|chartDrag", win):
        fail += 1
        print("  !! 检测到 chartPan / chartDrag —— 中键拖动应已彻底移除")
    else:
        print("  OK 中键拖动相关状态已彻底清除（chartPan / chartDrag）")

    if re.search(r"button == 2", win):
        fail += 1
        print("  !! 仍有中键(button 2)处理 —— 该功能应当已阉割")
    else:
        print("  OK 无中键处理残留")

    # 滚轮缩放必须保留
    if "EventType.ScrollWheel" in win:
        print("  OK 滚轮缩放保留")
    else:
        fail += 1
        print("  !! 滚轮缩放不见了")

    # 悬停命中判定与绘制必须共用同一个槽宽，否则会指到相邻一根
    if re.search(r"LastSlotWidth = slot;", win) and re.search(r"LastSlotWidth <= 0\.01f", win):
        print("  OK 悬停命中与绘制共用槽宽（不会指错根）")
    else:
        fail += 1
        print("  !! 悬停未复用绘制的槽宽 —— 会指到相邻 K 线")

    if "DrawCandleHover" in win and "RimForex_HoverBody" in win:
        print("  OK K 线悬停数据显示已接入")
    else:
        fail += 1
        print("  !! 悬停数据显示缺失")

    # ---- 4a7. 慢周期行情（多尺度 OU 的最慢一级）---------------------------
    # 历史：最早锚固定在开局价（没趋势）→ 试了三种随机趋势方案全失败
    # （都会抬高步级方差）→ 退到确定性正弦锚 → 正弦太机械，最终换成
    # 多尺度 OU 堆栈里最慢的那一级（τ ≈ 9 日）。
    #
    # 现在的关注点变了：正弦锚时代要防的是"消耗 Rand 污染随机流"，
    # 而现在最慢那一级【本来就走 Rand】（它就是一个 OU），
    # 真正要守的是"滑块必须真的接上去"和"预算仍守恒"。
    if "AllocateOscBudget" in core and "TauDays" in core:
        print("  OK 存在多尺度 OU 预算分配 AllocateOscBudget")
    else:
        fail += 1
        print("  !! 缺少 AllocateOscBudget —— 汇率会退回单尺度（图上没有趋势）")

    # 两个滑块必须被 AllocateOscBudget 读取，否则就是骗人的控件
    if re.search(r"cfg\.trendAmp\s*/", core) and re.search(r"cfg\.trendPeriodDays", core):
        print("  OK 慢周期强度/长度滑块已接进预算分配（不是死控件）")
    else:
        fail += 1
        print("  !! 慢周期滑块没接进 AllocateOscBudget —— 拉了没有任何效果")

    # 权重缩放后必须重新归一化，否则总量会随滑块漂移
    if re.search(r"sumSq \+= w\[k\] \* w\[k\];", core) and \
       re.search(r"sigmaStep / System\.Math\.Sqrt\(sumSq\)", core):
        print("  OK 缩放后重新归一化（步位移方差总和恒定）")
    else:
        fail += 1
        print("  !! 缩放后未归一化 —— 滑块会改变总波动，破坏经济验收")

    # 滑块变化必须触发重建，否则玩家看不到变化
    if re.search(r"ouBuildTrendAmp == tAmp && ouBuildTrendPeriod == tPer", core):
        print("  OK 滑块变化会触发振荡子重建")
    else:
        fail += 1
        print("  !! 滑块变化不触发重建 —— 改了设置也不生效")

    # 正弦锚必须已经删干净（否则是两套趋势机制并存）
    if "TrendAnchor" in core or "TrendPhaseOf" in core:
        fail += 1
        print("  !! 仍残留正弦锚 TrendAnchor/TrendPhaseOf（已被多尺度 OU 取代）")
    else:
        print("  OK 正弦锚已完全移除（单一趋势机制）")

    # 周期字段不得持久化（相位由游戏 tick 推出，存盘只会被旧值污染）
    if re.search(r'Scribe_Values\.Look\(ref TrendPeriodDays', core):
        fail += 1
        print("  !! TrendPeriodDays 被持久化了 —— 它应由设置驱动")
    else:
        print("  OK 慢周期参数未混进存档逻辑")

    # ---- 4a8. 模组设置页必须可滚动 -----------------------------------------
    # 设置页踩过两次坑：直接 l.Begin 会让超长内容被自动分列到窗口外；
    # 改成"硬分两列"只是把溢出点往后推一格，再加设置照样溢出。
    # 正确解法是套一层 ScrollView —— 加任何数量的设置都不会被挤出页面。
    st = open(os.path.join(SRC, "RimForexSettings.cs"), encoding="utf-8").read()
    if "BeginScrollView" in st and "EndScrollView" in st:
        print("  OK 设置页有滚动视图（加设置不会被挤出页面）")
    else:
        fail += 1
        print("  !! 设置页缺少滚动视图 —— 设置一多就会被挤出页面")

    if "l.NewColumn()" in st:
        fail += 1
        print("  !! 设置页仍有硬分列 NewColumn() —— 那只推迟溢出，不解决问题")
    else:
        print("  OK 设置页为单列（无硬分列）")

    if "settingsScroll" in st:
        print("  OK 滚动位置已持久到静态字段")
    else:
        fail += 1
        print("  !! 滚动位置字段缺失 —— 每帧会跳回顶部")

    # ---- 4a9. 强平与手动平仓必须同口径（回归防线）-------------------------
    # 曾经强平路径写 `totalFee = pos.openFee` 且 `refund = eq`，
    # 等于免掉平仓手续费 —— 被强平反而比主动平仓更便宜，方向完全反了。
    # 实测 4 笔强平单的手续费恰好等于开仓费，合计漏收约 1,289 白银。
    if re.search(r"totalFee = pos\.openFee \+ closeFee", core):
        print("  OK 强平记录的手续费含平仓费（与手动平仓同口径）")
    elif re.search(r"totalFee = pos\.openFee,", core):
        fail += 1
        print("  !! 强平仍只记开仓费 —— 免掉了平仓手续费")
    else:
        fail += 1
        print("  !! 找不到强平手续费口径")

    if re.search(r"float refund = eq - closeFee;", core):
        print("  OK 强平退款已扣除平仓费")
    else:
        fail += 1
        print("  !! 强平退款未扣平仓费 —— 玩家被强平反而少付钱")

    if re.search(r"acc\.lifetimeFees \+= closeFee;", core):
        print("  OK 平仓费已计入累计手续费")
    else:
        fail += 1
        print("  !! 平仓费未计入累计手续费（报告 totalFee 会偏低）")

    # ---- 4a10. 爆仓线（图表叠加）------------------------------------------
    if "DrawStopOutLine" in win:
        print("  OK 存在爆仓线 DrawStopOutLine")
    else:
        fail += 1
        print("  !! 缺少 DrawStopOutLine")

    # 爆仓线必须用【含追加保证金】的 pos.margin —— 强平判据用的就是它。
    # 若改用 (margin - addedMargin)，补过保证金的仓位会画出一条偏近的假线。
    if re.search(r"StopOutFraction \* newest\.margin", win):
        print("  OK 爆仓线用含追加的 margin（与强平判据同口径）")
    else:
        fail += 1
        print("  !! 爆仓线未用含追加的 margin —— 补过保证金时线会画错")

    if "showStopOutLine" in win and "showStopOutLine" in st:
        print("  OK 爆仓线有开关")
    else:
        fail += 1
        print("  !! 爆仓线开关缺失")

    # ---- 4a11. csproj 不得保留未使用的程序集引用 --------------------------
    # `0Harmony` 曾是死引用：代码里零处使用，却指向一个 workshop 目录。
    # 那种目录一旦被取消订阅或更新，编译会直接失败 —— 而失败原因与代码无关，
    # 排查起来很费时间。既然不用 Harmony，就不该留这个引用。
    csproj_path = os.path.join(SRC, "RimForex.csproj")
    csproj = open(csproj_path, encoding="utf-8").read()
    if "0Harmony" in csproj or "HarmonyDir" in csproj:
        fail += 1
        print("  !! csproj 仍有 0Harmony / HarmonyDir 死引用")
    else:
        print("  OK csproj 无未使用的 Harmony 引用")

    # 复核：代码里确实没用 Harmony（防止有人加了补丁却忘了改 csproj）
    if re.search(r"\bHarmony\b|HarmonyLib|AccessTools", core + win + st):
        fail += 1
        print("  !! 代码里出现了 Harmony —— 与 csproj 不一致")
    else:
        print("  OK 代码未使用 Harmony（无补丁依赖，换引擎时风险更低）")

    # ---- 4a12. 每日结算必须用【权益差】，不能用浮动盈亏 ---------------------
    # 原实现写 `dayPnl = acc.FloatingPnl`，只统计"此刻持仓"的浮动盈亏：
    #   白天赚 5000 且已平仓 -> 落袋的钱完全不计
    #   午夜还挂着浮亏 2000  -> 结算只报"今天亏 2000"
    #   午夜恰好空仓        -> 无论白天赚亏多少，一律报 0
    # 现在改为【权益的日变化】，一次涵盖已实现 + 浮动两个口径。
    if "equityAtLastSettle" in core and "AccountEquity" in core:
        print("  OK 每日结算用权益差（equityAtLastSettle + AccountEquity）")
    else:
        fail += 1
        print("  !! 每日结算缺少权益基线 —— 已实现盈亏会漏计")

    # 必须先剥掉注释再匹配：修复说明里就写着 `dayPnl = acc.FloatingPnl`，
    # 直接搜会把注释当成代码，误报失败（踩过一次）。
    core_nc = re.sub(r"//[^\n]*", "", core)
    if re.search(r"dayPnl\s*=\s*acc\.FloatingPnl", core_nc):
        fail += 1
        print("  !! 结算又退回了 FloatingPnl —— 白天已平仓的盈亏会丢失")
    else:
        print("  OK 结算未退回 FloatingPnl 口径")

    # 空仓也可能有当日盈亏，所以不能提前 return 掉心情
    if re.search(r"if \(acc\.positions\.Count == 0\)\s*\{\s*acc\.lastDayPnl = 0f", core):
        fail += 1
        print("  !! 空仓时仍直接清零 —— 白天赚了钱、午夜前平光就报 0")
    else:
        print("  OK 空仓不再无条件清零当日盈亏")

    if "LetterProfitTextFlat" in core:
        print("  OK 结算信件有空仓专用文案（避免出现\"0 个持仓\"自相矛盾）")
    else:
        fail += 1
        print("  !! 结算信件缺少空仓文案")

    # ---- 4a13. 平仓心情系统（回归防线）------------------------------------
    # 历史：最早按"每日结算的当日盈亏"挂一个档位，阈值写死 ±400/±40，
    # 账户一大就天天"平淡"；而且每天只结算一次，体现不出单笔亏损的力度。
    # 现在改成按【单笔平仓】记录，最多同时挂 3 档，同档最多叠 3 层。
    mood_path = os.path.join(SRC, "FxMood.cs")
    if not os.path.exists(mood_path):
        fail += 1
        print("  !! 缺少 FxMood.cs")
        mood = ""
    else:
        mood = open(mood_path, encoding="utf-8").read()
        print("  OK 存在 FxMood.cs（平仓心情系统）")

        # 关键常量
        for token, desc in (
            ("MaxStack = 3", "同档最多叠 3 层"),
            ("MaxActive = 3", "最多同时挂 3 档"),
            ("TierMarginCall = 8", "爆仓是独立档"),
            ("TierBlowUp = 9", "穿仓是独立档"),
            ("tier == TierBlowUp ? 1 : MaxStack", "穿仓不叠加（只允许 1 层）"),
        ):
            if token in mood:
                print("  OK %s" % desc)
            else:
                fail += 1
                print("  !! 缺少常量：%s" % desc)

        # 时长上限必须是一天（设计约束）
        if re.search(r"if \(days > 1f\) days = 1f;", mood):
            print("  OK 心情时长封顶一天")
        else:
            fail += 1
            print("  !! 心情时长未封顶一天")

        # ⚠️ 时长【不能】靠覆写 Thought.DurationTicks —— 那只对 Thought_Memory 生效，
        # 情境想法完全忽略它。最早这么写过，结果想法永远不消失。
        # 必须自己在 worker 里比对时刻。
        if "public override int DurationTicks" in mood:
            fail += 1
            print("  !! 又出现了 DurationTicks 覆写 —— 对情境想法无效，想法会永不过期")
        else:
            print("  OK 未依赖无效的 DurationTicks 覆写")

        if "IsExpired" in mood and "ExpiredAt" in mood:
            print("  OK 过期由 IsExpired/ExpiredAt 自己判定")
        else:
            fail += 1
            print("  !! 缺少过期判定 —— 心情不会消失")

        if "sinceTick" in mood and "sinceTick" in core:
            print("  OK 条目记录发生时刻，叠层会刷新")
        else:
            fail += 1
            print("  !! 缺少 sinceTick —— 无法计时长")

        # 心情值必须现算（读设置），不能只靠 XML 的 baseMoodEffect
        if "public override float MoodOffset()" in mood:
            print("  OK 心情值由 Thought_FxCloseMood 现算（设置里的倍率才生效）")
        else:
            fail += 1
            print("  !! 未覆写 MoodOffset —— 设置里的心情倍率不会生效")

        # 与 XML 的 defName 必须逐字一致
        block = re.search(r"TierDefNames\s*=\s*\{(.*?)\};", mood, re.S)
        xml_defs = set()
        tp = os.path.join(ROOT, "Defs", "ThoughtDefs_Forex.xml")
        if os.path.exists(tp):
            xt = open(tp, encoding="utf-8").read()
            xml_defs = set(re.findall(r"<defName>(RimForex_Fx[A-Za-z]+)</defName>", xt))
        if block:
            code_defs = set(re.findall(r'"(RimForex_Fx[A-Za-z]+)"', block.group(1)))
            if code_defs == xml_defs and code_defs:
                print("  OK %d 个心情 defName 在代码与 XML 间一一对应" % len(code_defs))
            else:
                fail += 1
                print("  !! defName 不一致：代码多 %s / XML 多 %s"
                      % (sorted(code_defs - xml_defs), sorted(xml_defs - code_defs)))

            # ── stage 数必须等于该档的叠加上限 ──
            # 这里踩过一次：穿仓上限写成 1（不叠加），XML 却写了 3 条递进文案。
            # worker 只会传 ActiveAtStage(0)，后两条永远不可能显示 ——
            # 是自相矛盾的死内容，而且光看任何一边都发现不了。
            if os.path.exists(tp):
                xt2 = open(tp, encoding="utf-8").read()
                capblow = 1 if re.search(r"TierBlowUp \? 1 : MaxStack", mood) else 3
                bad_stage = []
                for mm in re.finditer(
                        r"<defName>(RimForex_Fx[A-Za-z]+)</defName>.*?<stages>(.*?)</stages>",
                        xt2, re.S):
                    nm = mm.group(1)
                    cnt = mm.group(2).count("<li>")
                    # 只有穿仓是特殊上限，其余都是 MaxStack
                    want = capblow if nm == "RimForex_FxBlowUp" else 3
                    if cnt != want:
                        bad_stage.append("%s=%d(应%d)" % (nm, cnt, want))
                if bad_stage:
                    fail += 1
                    print("  !! stage 数与叠加上限不符：%s" % ", ".join(bad_stage))
                else:
                    print("  OK 每档 stage 数等于其叠加上限（无不可达的死文案）")
        else:
            fail += 1
            print("  !! 找不到 TierDefNames")

    # 结算不再挂心情，但也不能因此彻底不吭声
    # 剥掉注释再查：字段的说明里会提到 lastMoodStage（解释为什么被取代），
    # 直接搜会把注释当代码误报（和 dayPnl 那条踩过同一个坑）。
    core_nc2 = re.sub(r"//[^\n]*", "", core)
    if "lastMoodStage" in core_nc2:
        fail += 1
        print("  !! 仍残留 lastMoodStage（应已由 activityToday 取代）")
    else:
        print("  OK 已无 lastMoodStage 残留（仅注释中作为历史说明保留）")

    if "activityToday" in core:
        print("  OK 结算用 activityToday 判断当天是否动过仓")
    else:
        fail += 1
        print("  !! 缺少 activityToday —— 平淡日会连信都不发")

    # ---- 4a14. 测试专用板块 ------------------------------------------------
    # 覆盖两条"正常游玩验不到"的路径：心情跨日行为、穿仓。
    if "ScheduleForcedGap" in core and "CheckForcedGap" in core:
        print("  OK 存在强制跳空排程（测试穿仓）")
    else:
        fail += 1
        print("  !! 缺少强制跳空排程")

    # 排程必须【延迟】而不是立即 —— 玩家要时间建仓
    if re.search(r"forceGapAtTick = GenTicks\.TicksGame \+ ForceGapDelayHours", core):
        print("  OK 强制跳空延迟触发（留出建仓时间）")
    else:
        fail += 1
        print("  !! 强制跳空没有延迟 —— 玩家来不及建仓就触发了")

    # 一次性：取走后必须清掉，否则会每步都跳空
    if re.search(r"bool forcedGap = forceExtremeGap;\s*\n\s*forceExtremeGap = false;", core):
        print("  OK 强制跳空是一次性事件")
    else:
        fail += 1
        print("  !! 强制跳空未做成一次性 —— 会连续跳空")

    # 必须向下跳空，否则"百倍做多"测不到穿仓
    # 幅度下限必须够大：最稳定的档位在 34σ 时只跌 0.91%，
    # 100x 多仓亏 91% 保证金 —— 差一点，测试会"看起来失败"。60σ 才稳。
    if re.search(r"mult = -\(ForcedGapMinMult", core):
        print("  OK 强制跳空固定向下且幅度足够（最稳档也能穿仓）")
    else:
        fail += 1
        print("  !! 强制跳空方向随机或幅度不足 —— 测试可能测不出穿仓")

    if "DebugAddMood" in core and re.search(r'RimForex_TestMood', st):
        print("  OK 存在心情测试按钮（验证叠加与跨日过期）")
    else:
        fail += 1
        print("  !! 缺少心情测试按钮")

    # ---- 4a15. 设置页高度必须自适应（防"选项滚到底也看不到"）-------------
    # 踩过一次：显式写死 `ViewH = 1500f`，靠手算内容高度保证不超。
    # 一旦手算偏低，Listing 的 NewColumnIfNeeded 就会拐到第二列
    # （画到窗口右侧之外），或者内容被 ScrollView 裁掉 ——
    # 玩家的体验就是"某个选项消失了，滚到底也没有"。
    if "maxOneColumn = true" in st:
        print("  OK 设置页禁用了自动分列")
    else:
        fail += 1
        print("  !! 设置页未禁用自动分列 —— 内容一多就会画到窗口外")

    if "MaxColumnHeightSeen" in st and "settingsViewH = used" in st:
        print("  OK 设置页高度画完后实测并自动扩容")
    else:
        fail += 1
        print("  !! 设置页高度仍是写死的 —— 内容变多就会被裁掉")

    # 写死的高度常量不该再出现（它就是上一次的病因）
    if re.search(r"const float ViewH\s*=", st):
        fail += 1
        print("  !! 又出现了写死的 ViewH 常量")
    else:
        print("  OK 无写死的 ViewH 常量")

    # 画完必须真的实测一次；只 Begin 不量等于没做
    if re.search(r"float used = l\.MaxColumnHeightSeen", st):
        print("  OK 内容高度确实取自 l.MaxColumnHeightSeen")
    else:
        fail += 1
        print("  !! 没有实测内容高度")

    # ---- 4a16. 图表静态状态必须在每次绘制前重置 ---------------------------
    # DrawCandles 会写下 ChartArea / ChartMin / ChartMax / ChartHasData，
    # 供持仓线、回本线、爆仓线、悬停提示使用 —— 它们要知道 K 线的 y 轴范围。
    #
    # 曾经只在这些字段"画成功"时置 true，**没有任何地方置 false**。
    # 于是函数从三个提前 return 之一离开时，标志停在上一帧的 true，
    # 那几个绘图函数就会拿着过期坐标画线，表现为线闪到错误位置。
    #
    # 正确写法：函数开头统一置 false，真正画完再置 true。
    # 这里检查两个赋值都存在，且 false 出现在 true 之前。
    i_false = win.find("ChartHasData = false;")
    i_true = win.find("ChartHasData = true;")
    if i_false >= 0 and i_true >= 0 and i_false < i_true:
        print("  OK 图表静态状态在绘制前重置（ChartHasData 先 false 后 true）")
    else:
        fail += 1
        print("  !! ChartHasData 缺少成对赋值 —— 提前 return 时会残留过期坐标")

    # ---- 4a17. 预警队列的两处一致性 -----------------------------------------
    # ── (1) 平仓必须把该仓位从预警队列里摘掉 ──
    # 预警窗的失效判据是 `warn.position == null`，而 `positions.Remove` 只是
    # 从列表移除、对象引用仍被 MarginWarning 持有，所以判据不成立、窗口不会自动关。
    # 那个窗口是 forcePause = true —— 游戏被暂停，玩家必须点掉一个
    # "关于已经平掉的仓位"的预警才能继续玩。
    if "DropWarningFor(pos);" in win:
        n_drop = win.count("DropWarningFor(pos);")
        if n_drop >= 2:
            print("  OK 平仓的两条路径都会摘掉预警（%d 处调用）" % n_drop)
        else:
            fail += 1
            print("  !! 只有 %d 处调用 DropWarningFor —— 有一条平仓路径没清理" % n_drop)
    else:
        fail += 1
        print("  !! 平仓不清理 pendingWarnings —— 会为已平仓位弹出必须手动关闭的暂停窗")

    # ── (2) criticalWarned 不能持久化 ──
    # 它与 pendingWarnings 是一对：标志为 true 意味着"已塞进队列"，
    # 而队列是纯运行时对象、不进存档。若只存标志，读档后队列空了但标志还是 true，
    # MaybeWarn 会直接 return —— 该仓位再也不会被提醒，玩家会被意外爆仓。
    if re.search(r"Scribe_Values\.Look\(ref criticalWarned", core):
        fail += 1
        print("  !! criticalWarned 被持久化了 —— 读档后会静默漏报预警")
    else:
        print("  OK criticalWarned 不持久化（与 pendingWarnings 同步）")

    # 那个"已经提醒过就不再弹"的判断必须还在（否则会每 tick 刷屏）
    if re.search(r"if \(pos\.criticalWarned\) return;", core):
        print("  OK MaybeWarn 仍保留防重复提醒")
    else:
        fail += 1
        print("  !! MaybeWarn 缺少防重复提醒 —— 会每 tick 弹窗")

    # ---- 4b. 已移除的常量不应复活（防止旧机制被无意恢复）----
    for gone in ("WarnThreshold", "RearmFraction"):
        if re.search(r"public const \w+ %s = " % gone, core):
            fail += 1
            print("  !! 常量 %s 仍然存在 —— 多档复燃机制应当已被单一阈值取代" % gone)
        else:
            print("  OK 常量 %s 已移除" % gone)

    # ---- 4c. 防回归：时间换算不得借用 TicksPerStep ----
    # 这是一个真实发生过的 bug：换算"距下次结算几小时"时错用了 TicksPerStep，
    # 因为它的值（180）看起来接近一小时（150）。但 TicksPerStep 是报价步长，
    # 随玩法节奏变化，一旦变动小时显示就整体错位。
    # 这里扫描所有源码里像"把 tick 换算成小时/秒"的表达式。
    bad_conv = []
    for fn in os.listdir(SRC):
        if not fn.endswith(".cs"):
            continue
        for i, line in enumerate(open(os.path.join(SRC, fn), encoding="utf-8"), 1):
            s = line.strip()
            if s.startswith("//") or s.startswith("///"):
                continue
            # 除以 TicksPerStep 且同行出现"小时/时/小时数"语义的变量名
            if "TicksPerStep" in s and re.search(r"Hour|hour|小时|RealSecond|秒", s):
                bad_conv.append("%s:%d  %s" % (fn, i, s))
    if bad_conv:
        fail += 1
        print("  !! 疑似把报价步长 TicksPerStep 当作时间单位使用：")
        for b in bad_conv:
            print("       " + b)
    else:
        print("  OK 未发现 TicksPerStep 被用作时间换算单位")

    # ---- 5. 不变量：CriticalThreshold 必须严格大于 StopOutFraction ----
    # 否则强平会在预警之前发生，预警窗口永远不会弹出。
    # 另外：CriticalThreshold 必须严格小于 1，否则浮亏还没到就报警。
    def resolve_const(name):
        """解析 float 常量，支持 `Other * 1.6f` 这种引用另一个常量的写法。"""
        m = re.search(r"public const float %s = ([^;]+);" % name, core)
        if not m:
            return None
        expr = m.group(1).strip().rstrip("f")
        mul = re.match(r"(\w+)\s*\*\s*([\d.]+)$", expr)
        if mul:
            base = resolve_const(mul.group(1))
            return None if base is None else base * float(mul.group(2))
        try:
            return float(expr)
        except ValueError:
            return None

    stop_out = resolve_const("StopOutFraction")
    crit = resolve_const("CriticalThreshold")

    if None in (stop_out, crit):
        fail += 1
        print("  !! 无法解析阈值常量，跳过不变量检查")
    else:
        pairs = [
            ("CriticalThreshold", crit, "StopOutFraction", stop_out),
        ]
        for hi_name, hi, lo_name, lo in pairs:
            if hi > lo:
                print("  OK 不变量 %s(%.2f) > %s(%.2f)" % (hi_name, hi, lo_name, lo))
            else:
                fail += 1
                print("  !! 不变量失败：%s(%.2f) 必须 > %s(%.2f)，"
                      "否则后者会先发生、前者永远不触发" % (hi_name, hi, lo_name, lo))

    print()
    print("结果:", "全部通过" if fail == 0 else "%d 项失败" % fail)
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
