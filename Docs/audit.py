"""RimForex 交付前校验：翻译键一致性 + XML 合法性 + 关键常量核对。
用法: python audit.py
"""
import os
import re
import sys
import struct
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
        "CriticalThreshold": "0.45f",
        "StopOutFraction": "0.3f",
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

    # 2026-10-23：账户上的"累计手续费"已删除（作者认为这个统计没有意义）。
    # 但报告里的 totalFee 必须仍然准确 —— 它来自**逐笔记录**，不依赖那个累计字段。
    # 这条检查守的是"报告口径"，不是"有没有累计器"。
    hist_src = ""
    hp = os.path.join(SRC, "Window_TradeHistory.cs")
    if os.path.exists(hp):
        hist_src = open(hp, encoding="utf-8").read()
    if re.search(r"totalFee \+= t\.totalFee;", hist_src):
        print("  OK 报告 totalFee 来自逐笔记录（不依赖账户累计字段）")
    else:
        fail += 1
        print("  !! 报告的 totalFee 不再逐笔累加 —— 手续费合计会失真")
    if "lifetimeFees" not in core:
        print("  OK 账户累计手续费字段已彻底移除")
    else:
        fail += 1
        print("  !! lifetimeFees 又回来了 —— 玩家认为这个统计没有意义，已要求删除")

    # ---- 4a10. 爆仓线（图表叠加）------------------------------------------
    if "DrawStopOutLine" in win:
        print("  OK 存在爆仓线 DrawStopOutLine")
    else:
        fail += 1
        print("  !! 缺少 DrawStopOutLine")

    # 爆仓线必须与【强平判据】同口径。
    #
    # 2026-10-21：判据从 pos.margin 改成 pos.BaseMargin（初始保证金）。
    # 原因：追加保证金会抬高 margin，若拿它当强平基准，
    # 玩家会在界面显示"健康度 60%、还没到预警线"时被静默强平。
    # 所以画线与判据都必须用初始保证金。
    if "newest.BaseMargin" in win and "newest.margin - newest.addedMargin" not in win:
        print("  OK 爆仓线用初始保证金（与强平判据同口径）")
    else:
        fail += 1
        print("  !! 爆仓线没有用初始保证金 —— 补过保证金时会画出偏远的假线")

    # 强平判据本身也必须从 BaseMargin 推出来（否则上面那条"同口径"无从谈起）
    core_stripped = re.sub(r"//[^\n]*", "", core)
    if "pos.BaseMargin" in core_stripped and "baseM * StopOutFraction" in core_stripped:
        print("  OK 强平判据用初始保证金（追加保证金不会抬高强平线）")
    else:
        fail += 1
        print("  !! 强平判据没有用 BaseMargin —— 追加保证金会造成标尺错位（R05）")

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
    #
    # ── 2026-10-17：口径里的"保证金"漏过一次 ──
    # 当时结算用的属性定义为 balance + FloatingPnl，可是开仓是
    # `balance -= margin + fee`，所以 balance 是【可用】资金、不含锁住的保证金。
    # 于是开仓那一天的当日盈亏会凭空多出一笔 -margin 的巨亏（平仓则相反）。
    # 正确的权益是 balance + MarginUsed + FloatingPnl（FxAccount.Equity）。
    # 下面同时卡住三件事，防止再退回错误口径：
    #   1) 基线字段还在
    #   2) 结算用的是 acc.Equity
    #   3) 那个漏算保证金的 AccountEquity 属性没有被重新加回来
    core_nc = re.sub(r"//[^\n]*", "", core)
    ok_baseline = "equityAtLastSettle" in core_nc
    ok_equity = re.search(r"equityNow\s*=\s*acc\.Equity\b", core_nc) is not None
    reintroduced = re.search(r"AccountEquity\s*=>", core_nc) is not None

    if ok_baseline and ok_equity and not reintroduced:
        print("  OK 每日结算用权益差，且口径含保证金（acc.Equity）")
    else:
        fail += 1
        if not ok_baseline:
            print("  !! 每日结算缺少权益基线 —— 已实现盈亏会漏计")
        if not ok_equity:
            print("  !! 每日结算没有用 acc.Equity —— 口径可能漏掉锁住的保证金")
        if reintroduced:
            print("  !! AccountEquity（balance + FloatingPnl）又出现了 —— 它会漏算保证金")

    # ---- 4a12b. 结算必须扣掉【外部现金流】（2026-10-18）-------------------
    # 权益里含 balance，而存入/提取直接增删 balance —— 那是玩家自己搬钱，不是赚亏。
    # 不扣除的话：盈利平仓后把余额全部提取，权益掉到 0，
    # 结算会报出一笔等于提取额的假亏损（实测 真实 +56 → 报出 −1000）；
    # 存入则相反，会被报成等额的假盈利（存 500、不做交易 → 报 +500）。
    #
    # 判断三件事：字段存在、结算公式里减了它、存入与提取都更新了它。
    ok_field = re.search(r"public float cashFlowSinceSettle", core_nc) is not None
    ok_scribe = re.search(r'Scribe_Values\.Look\(ref cashFlowSinceSettle', core) is not None
    ok_formula = re.search(
        r"dayPnl\s*=\s*equityNow\s*-\s*acc\.equityAtLastSettle\s*-\s*acc\.cashFlowSinceSettle",
        core_nc) is not None
    dep_nc = re.sub(r"//[^\n]*", "", win)
    ok_deposit = re.search(r"cashFlowSinceSettle\s*\+=\s*deposited", dep_nc) is not None
    ok_withdraw = re.search(r"cashFlowSinceSettle\s*-=\s*taken", dep_nc) is not None

    if ok_field and ok_scribe and ok_formula and ok_deposit and ok_withdraw:
        print("  OK 结算扣除外部现金流（存入/提取不计入盈亏）")
    else:
        fail += 1
        if not ok_field:
            print("  !! 缺少 cashFlowSinceSettle 字段")
        if not ok_scribe:
            print("  !! cashFlowSinceSettle 没有存档")
        if not ok_formula:
            print("  !! 结算公式没减 cashFlowSinceSettle —— 提取会被报成亏损")
        if not ok_deposit:
            print("  !! 存入没有记入现金流 —— 会被报成假盈利")
        if not ok_withdraw:
            print("  !! 提取没有记入现金流 —— 会被报成假亏损")

    # ---- 4a12d. 结算信必须摊开手续费口径（2026-10-18）--------------------
    # 信里的"今日盈亏"是净额（已扣手续费），交易历史那列"盈亏"是毛利。
    # 只写净额时玩家会拿它去对账，然后认定算错（作者本人误判过三次）。
    # 所以要有一行"手续费 X，扣除前 Y"。
    # 判断四件事：字段在、存档、结算后归零、四个收费点都累加。
    ok_fee_field = re.search(r"public float feesSinceSettle", core_nc) is not None
    ok_fee_scribe = re.search(r'Scribe_Values\.Look\(ref feesSinceSettle', core) is not None
    ok_fee_reset = re.search(r"acc\.feesSinceSettle\s*=\s*0f", core_nc) is not None
    ok_fee_letter = "RimForex_LetterFeeBreakdown" in core_nc
    fee_sites = len(re.findall(r"feesSinceSettle\s*[+\-]=", core_nc + dep_nc))

    if ok_fee_field and ok_fee_scribe and ok_fee_reset and ok_fee_letter and fee_sites >= 4:
        print("  OK 结算信摊开手续费口径（%d 个收费点已接入）" % fee_sites)
    else:
        fail += 1
        if not ok_fee_field:
            print("  !! 缺少 feesSinceSettle 字段")
        if not ok_fee_scribe:
            print("  !! feesSinceSettle 没有存档")
        if not ok_fee_reset:
            print("  !! 结算后没有把 feesSinceSettle 归零 —— 会越攒越多")
        if not ok_fee_letter:
            print("  !! 结算信没用 RimForex_LetterFeeBreakdown —— 口径还是对不上")
        if fee_sites < 4:
            print("  !! 只有 %d 处累加手续费（应为 4：开仓 / 平仓 / 强平 / 退款回冲）" % fee_sites)

    # ---- 4a12c. 不能用"基线 == 0"判断有没有基线（2026-10-18）-------------
    # equityAtLastSettle 曾经既当数值又当哨兵：== 0 就被当成"没有基线"。
    # 但 0 是合法的权益值 —— 玩家满额提取或亏光之后，结算时权益正好是 0，
    # 基线被存成 0；第二天无论赚多少都会走进"没有基线"分支、把基线刷成当前值，
    # 于是当日盈亏恒为 0。
    # 实测：已实现 +1212 的一天，结算信报"今日盈亏 +0"。
    # 现在改用独立的 settleBaselineSet 回答"有没有基线"。
    bad_sentinel = re.search(r"equityAtLastSettle\s*==\s*0f", core_nc) is not None
    has_flag = re.search(r"if\s*\(\s*!\s*acc\.settleBaselineSet\s*\)", core_nc) is not None
    flag_scribed = re.search(r'Scribe_Values\.Look\(ref settleBaselineSet', core) is not None

    if has_flag and flag_scribed and not bad_sentinel:
        print("  OK 结算基线用独立标记判断（不再拿 0 当哨兵）")
    else:
        fail += 1
        if bad_sentinel:
            print("  !! 又用 equityAtLastSettle == 0 当哨兵 —— 满额提取后当日盈亏会恒为 0")
        if not has_flag:
            print("  !! 结算没有检查 settleBaselineSet")
        if not flag_scribed:
            print("  !! settleBaselineSet 没有存档")

    # 必须先剥掉注释再匹配：修复说明里就写着 `dayPnl = acc.FloatingPnl`，
    # 直接搜会把注释当成代码，误报失败（踩过一次）。
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
    #
    # 2026-10-21：强制跳空从 bool 泛化成两档（常规 1 / 极端 2），
    # 取走后把 forcedGapKind 置回 None。这里跟着改成新写法，
    # 同时顺带确认两档都存在（新增的普通跳空按钮不能只有按钮没有实现）。
    if re.search(r"int forcedKind = forcedGapKind;\s*\n\s*forcedGapKind = ForcedGapNone;", core):
        print("  OK 强制跳空是一次性事件")
    else:
        fail += 1
        print("  !! 强制跳空未做成一次性 —— 会连续跳空")

    has_normal = "ForceNormalGap()" in core and "ForcedGapNormal" in core
    has_extreme = "ForceExtremeGap()" in core and "ForcedGapExtreme" in core
    if has_normal and has_extreme:
        print("  OK 测试按钮两档齐全（常规跳空 / 极端跳空）")
    else:
        fail += 1
        print("  !! 缺少某档测试跳空：常规=%s 极端=%s" % (has_normal, has_extreme))

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
    # 2026-10-23：平仓逻辑从窗口下沉到 FxGameComponent.ClosePositionFromUi
    # （悬浮窗的"平仓"按钮要和终端共用同一套记账），所以检查目标改成 core。
    if "pendingWarnings.Remove(pos);" in core:
        n_drop = core.count("pendingWarnings.Remove(pos);")
        if n_drop >= 2:
            print("  OK 平仓的两条路径都会摘掉预警（%d 处调用）" % n_drop)
        else:
            fail += 1
            print("  !! 只有 %d 处摘除预警 —— 有一条平仓路径没清理" % n_drop)
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

    # ---- 4a18. About 目录的图像文件 -----------------------------------------
    # RimWorld 的路径是**写死**的（见 Verse.ModMetaData）：
    #     ModIconImagePath => <mod>/About/ModIcon.png     模组列表小图标
    #     PreviewImagePath => <mod>/About/Preview.png     仅 Workshop 上传用
    # 而 GetWorkshopPreviewImagePath() 直接返回 PreviewImagePath ——
    # 也就是说**没有 Preview.png 就无法上传创意工坊**。
    about_dir = os.path.join(ROOT, "About")

    def png_size(path):
        """读 PNG 的 IHDR 宽高。不是 PNG 或读不出就返回 None。"""
        try:
            d = open(path, "rb").read(24)
            if d[:8] != b"\x89PNG\r\n\x1a\n":
                return None
            w, h = struct.unpack(">II", d[16:24])
            return w, h
        except Exception:
            return None

    # ModIcon：游戏里只显示 32~64px。尺寸过大没有收益，只浪费显存 ——
    # 它会被 LoadImage 以**完整尺寸**读成 Texture2D。
    icon = os.path.join(about_dir, "ModIcon.png")
    if not os.path.isfile(icon):
        fail += 1
        print("  !! 缺少 About/ModIcon.png")
    else:
        sz = png_size(icon)
        if sz is None:
            fail += 1
            print("  !! About/ModIcon.png 不是有效 PNG")
        elif max(sz) > 512:
            fail += 1
            print("  .. About/ModIcon.png 是 %dx%d —— 会被全尺寸读进显存，"
                  "而它只显示 32~64px。建议 128x128" % sz)
        else:
            print("  OK About/ModIcon.png (%dx%d，尺寸合理)" % sz)

    # Preview：Steam 要求 >= 512x512，小于这个上传时会模糊
    prev = os.path.join(about_dir, "Preview.png")
    if not os.path.isfile(prev):
        fail += 1
        print("  !! 缺少 About/Preview.png —— 无法上传 Steam 创意工坊")
    else:
        sz = png_size(prev)
        if sz is None:
            fail += 1
            print("  !! About/Preview.png 不是有效 PNG")
        elif min(sz) < 512:
            fail += 1
            print("  .. About/Preview.png 只有 %dx%d，Steam 要求至少 512x512" % sz)
        else:
            print("  OK About/Preview.png (%dx%d，满足 Workshop 要求)" % sz)

    # ---- 4a19. 下单量估算不得退化成逐次递减（2026-10-17）-------------------
    # 原来 EstimateUnits 从 floor(margin/perUnitMargin) 开始每次减 1 直到负担得起。
    # 收敛步数 ≈ 手续费 / 每单位保证金：输入上限 1 亿、价格 113、杠杆 100 时
    # 实测 1,728,014 次，而它在 DoWindowContents 里每帧调用 —— 会把终端卡死。
    # 现在改成二分（log2(u)，最多 30 次）。这里卡住"不许再出现无界递减循环"。
    win_nc_local = re.sub(r"//[^\n]*", "", win)
    if re.search(r"while\s*\(\s*u\s*>\s*0\s*\)", win_nc_local):
        fail += 1
        print("  !! EstimateUnits 又出现了 while (u > 0) 递减循环 —— 大额输入会卡死")
    elif "Affordable" in win_nc_local and re.search(r"while\s*\(\s*lo\s*<\s*hi\s*\)", win_nc_local):
        print("  OK 下单量估算用二分（迭代次数与输入规模无关）")
    else:
        fail += 1
        print("  !! 找不到 EstimateUnits 的二分实现")

    # ---- 4a20. 已删除的死控件 / 死字段不得复活 -----------------------------
    # showChartNavHint：勾选框还在，但全工程没有消费者（它控制的"图表操作说明"
    # 随平移功能一起删掉了）。留着只会让玩家以为开关有效。
    if re.search(r"showChartNavHint", core + win + st):
        fail += 1
        print("  !! showChartNavHint 又出现了 —— 这个开关没有任何消费者")
    else:
        print("  OK 死控件 showChartNavHint 未复活")

    # MarginWarning.map：由 EnqueueWarning 扫 Find.Maps 填进来，但从不被读取。
    # 必须先剥注释：类定义上方那段说明里就写着 "public Map map;"，
    # 直接搜会把说明文字当成代码（这个坑又踩了一次）。
    core_nc2 = re.sub(r"//[^\n]*", "", core)
    if re.search(r"class MarginWarning\s*\{[^}]*public Map map;", core_nc2, re.S):
        fail += 1
        print("  !! MarginWarning.map 又出现了 —— 它是只写不读的死字段")
    else:
        print("  OK 死字段 MarginWarning.map 未复活")

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

    # ---- 5b. 强平距离公式必须用 (1 − StopOutFraction)（2026-10-20）-------
    # MaxSafeLeverageFor 里原本写的是 `StopOutFraction / (N × dailyVol)`，
    # 但"从开仓到强平还能走多少比例"是 (1 − StopOut) / 杠杆，不是 StopOut / 杠杆。
    # 旧值恰好是 0.5 时两者相等，错误被掩盖了很久；
    # 一旦改成 0.3，错误分子会让高波动品种的可用杠杆从 149x 掉到 64x，
    # **100x 会被静默降档到 50x**，且玩家看不到任何提示。
    core_nc_inv = re.sub(r"//[^\n]*", "", core)
    if re.search(r"\(\s*1f?\s*-\s*StopOutFraction\s*\)\s*/\s*\(\s*StopOutVolCoverage", core_nc_inv):
        print("  OK 最大安全杠杆用的是 (1 − StopOutFraction)")
    elif re.search(r"StopOutFraction\s*/\s*\(\s*StopOutVolCoverage", core_nc_inv):
        fail += 1
        print("  !! MaxSafeLeverageFor 用了 StopOutFraction 作分子 —— "
              "应为 (1 − StopOutFraction)，否则 100x 会被静默降档")
    else:
        fail += 1
        print("  !! 找不到 MaxSafeLeverageFor 的约束 2 公式")

    # ---- 5d. 所有"到强平线还有多远"的地方都必须用 (1 − StopOut) ----------
    # 这一族公式在 StopOutFraction = 0.5 时两种写法数值相等，错误被掩盖了很久。
    # 改成 0.3 后必须全部同步，否则会出现很诡异的现象：
    #   · MaxSafeLeverageFor 写错 → 高波动品种 100x 被静默降档
    #   · K 线爆仓线写错          → 线画在实际位置的 3/7 处，
    #                               价格早越过线了却还没被强平（作者实测截图）
    #   · 下单区"强平距离"写错    → 展示的百分比是真实值的 43%
    win_nc_inv = re.sub(r"//[^\n]*", "", win)
    bad_patterns = [
        r"StopOutFraction\s*\*\s*newest\.margin",        # K 线爆仓线距离
        r"StopOutFraction\s*/\s*orderLeverage",            # 下单区强平距离
    ]
    hit = [pat for pat in bad_patterns if re.search(pat, win_nc_inv)]
    if hit:
        fail += 1
        print("  !! 爆仓线距离仍在用 StopOutFraction 当余量 —— 应为 (1 − StopOutFraction)")
        for pat in hit:
            print("       命中: %s" % pat)
    else:
        print("  OK 爆仓线距离统一用 (1 − StopOutFraction)（K线 / 下单区 / 最大杠杆）")

    # ---- 5c. 心情必须只作用于自由殖民者（2026-10-20）-------------------
    # 账户按【地图】索引，不判身份的话囚犯、访客、任务寄宿者都会吃到
    # 殖民地交易盈亏的心情。作者实测发现过：殖民地亏钱，囚犯心情跟着掉。
    mood = open(os.path.join(SRC, "FxMood.cs"), encoding="utf-8").read()
    mood_nc = re.sub(r"//[^\n]*", "", mood)
    if re.search(r"if\s*\(\s*!\s*p\.IsFreeColonist\s*\)\s*return\s+ThoughtState\.Inactive", mood_nc):
        print("  OK 心情只作用于自由殖民者（囚犯/访客/寄宿者被排除）")
    else:
        fail += 1
        print("  !! ThoughtWorker 没有按 IsFreeColonist 过滤 —— 囚犯和访客会共担殖民地盈亏")

    # ---- 6. 派系货币生成必须排除临时派系（2026-10-20）------------------
    # 原版 AllFactionsListForReading 不过滤，任务系统会往里塞 temporary 派系
    # （boss 团、圣物朝圣者、被黑终端……），任务一结束就被原版删掉。
    # 我们每天结算都跑一次 EnsureAllFactionCurrencies，漏判的后果是：
    #   · 任何一次任务都可能让它多出一种货币
    #   · 派系被删后，那一行货币永远留着
    # 两个坑：判的是 def.hidden（应为实例属性 Hidden），以及根本没判 temporary。
    m = re.search(r"public void EnsureAllFactionCurrencies\(\).*?\n        \}", core_nc, re.S)
    if not m:
        fail += 1
        print("  !! 找不到 EnsureAllFactionCurrencies")
    else:
        body = m.group(0)
        if "f.temporary" in body and "f.Hidden" in body:
            print("  OK 派系货币生成排除了临时派系（f.Hidden + f.temporary）")
        else:
            fail += 1
            print("  !! 派系货币生成未排除临时派系 —— 任务生成的临时派系会变成货币")
            if "f.def.hidden" in body:
                print("       仍在使用 f.def.hidden（应为实例属性 f.Hidden）")

    # ---- 6b. 幽灵货币清理必须按 temporary 判（2026-10-20 实测修正）--------
    # 最初只认"派系已不在派系表里"，结果**完全没生效**：
    # 反编译确认 RimWorld 全程序没有任何地方调用 FactionManager.RemoveFaction，
    # 临时派系会永远留在表里，于是那个条件永远为假。
    if "PruneGhostCurrencies" not in core_nc:
        fail += 1
        print("  !! 找不到 PruneGhostCurrencies")
    else:
        m = re.search(r"private int PruneGhostCurrencies\(.*?\n        \}", core_nc, re.S)
        body = m.group(0) if m else ""
        if "f.temporary" in body:
            print("  OK 幽灵货币按 temporary 判定（临时派系不会永远留在表里）")
        else:
            fail += 1
            print("  !! 幽灵货币判据没有用 f.temporary —— 临时派系永不被删，清理会空转")

    # ---- 7. DefInjected 双向覆盖（2026-10-20）--------------------------
    # 背景：Defs 里的原文语言不统一 ——
    #   10 个 ThoughtDef 的 label/description 写的是中文
    #   终端 ThingDef 的 label/description 写的是英文（默认语言）
    # 于是两个方向各缺一块，只有一方能看懂：
    #   · 英文玩家曾经看到中文心情（因为英文没有 DefInjected）
    #   · 中文玩家一直看到 "FX trading terminal"（因为中文没有 ThingDef 的 DefInjected）
    # 这里直接把两个方向都查一遍，任何新增 Def 忘了翻译都会在这里报出来。
    cjk_re = re.compile(r"[\u4e00-\u9fff]")

    def _defs_fields():
        out = []
        for dp, _, fsn in os.walk(os.path.join(ROOT, "Defs")):
            for fn in sorted(fsn):
                if not fn.endswith(".xml"):
                    continue
                raw = open(os.path.join(dp, fn), encoding="utf-8-sig").read()
                raw = re.sub(r"<\?xml[^>]*\?>", "", raw)
                raw = re.sub(r"<!--.*?-->", "", raw, flags=re.S)
                try:
                    tree = ET.fromstring("<root>" + raw + "</root>")
                except ET.ParseError:
                    continue
                for defn in tree.iter():
                    if defn.tag == "root":
                        continue
                    nm = defn.findtext("defName")
                    if not nm:
                        continue
                    for fld in ("label", "description"):
                        el = defn.find(fld)
                        if el is not None and el.text and el.text.strip():
                            out.append((nm, fld, el.text.strip()))
                    st = defn.find("stages")
                    if st is not None:
                        for i, li in enumerate(st.findall("li")):
                            for fld in ("label", "description"):
                                el = li.find(fld)
                                if el is not None and el.text and el.text.strip():
                                    out.append((nm, "stages.%d.%s" % (i, fld), el.text.strip()))
        return out

    def _inj_keys(langdir):
        keys = set()
        base = os.path.join(ROOT, "Languages", langdir, "DefInjected")
        if not os.path.isdir(base):
            return keys
        for dp, _, fsn in os.walk(base):
            for fn in fsn:
                if not fn.endswith(".xml"):
                    continue
                txt = open(os.path.join(dp, fn), encoding="utf-8-sig").read()
                for m in re.finditer(r"<([A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+)>", txt):
                    keys.add(m.group(1))
        return keys

    dz = _inj_keys("ChineseSimplified (简体中文)")
    de = _inj_keys("English")
    fields = _defs_fields()
    miss_en = [f for f in fields if cjk_re.search(f[2]) and ("%s.%s" % (f[0], f[1])) not in de]
    miss_zh = [f for f in fields if not cjk_re.search(f[2]) and ("%s.%s" % (f[0], f[1])) not in dz]

    if not miss_en and not miss_zh:
        print("  OK DefInjected 双向覆盖（中文 %d 键 / 英文 %d 键）" % (len(dz), len(de)))
    else:
        fail += 1
        if miss_en:
            print("  !! %d 条中文 Def 文本缺英文翻译（英文玩家会看到中文）" % len(miss_en))
            for f in miss_en[:4]:
                print("       %s.%s" % (f[0], f[1]))
        if miss_zh:
            print("  !! %d 条英文 Def 文本缺中文翻译（中文玩家会看到英文）" % len(miss_zh))
            for f in miss_zh[:4]:
                print("       %s.%s" % (f[0], f[1]))

    # ---- 8. 预警必须带滞回与冷却（2026-10-21）--------------------------
    # 修好子步采样后，每步采样从 1 次变成 17 次。
    # 若仍按 "health > CriticalThreshold 就复位"，健康度在 0.45 附近摆动时
    # 会退化成 "复位→提醒→复位→提醒"，并每次都暂停游戏。
    # 100x 下盘内摆幅折算到健康度约 ±2.3 个百分点，几乎每步都穿越。
    if "WarnHysteresis" not in core_nc or "WarnCooldownTicks" not in core_nc:
        fail += 1
        print("  !! 缺少预警滞回/冷却常量（WarnHysteresis / WarnCooldownTicks）")
    else:
        m = re.search(r"private void MaybeWarn\(.*?\n        \}", core_nc, re.S)
        body = m.group(0) if m else ""
        if "WarnHysteresis" in body and "WarnCooldownTicks" in body:
            print("  OK 预警带滞回带与冷却（子步采样下不会刷屏）")
        else:
            fail += 1
            print("  !! MaybeWarn 未同时使用滞回与冷却 —— 修好采样后会刷屏")

    # ---- 9. 提交 3 的四项回归防线（2026-10-21）-------------------------
    # 只查"关键实现是否还在"，避免以后重构时悄悄退回旧行为。
    core9 = re.sub(r"//[^\n]*", "", core)
    win9 = re.sub(r"//[^\n]*", "", win)

    # R06：读档只能清理旧记录，不能重算新记录。
    # RecomputeSpreadCost 用平仓中间价重算，会把写入时的真实成本改掉。
    if "RecomputeSpreadCost" not in core9 and "spreadRateAtClose <= 0f) t.spreadCost = 0f" in core9:
        print("  OK 历史点差成本读档不改写（只清理旧记录）")
    else:
        fail += 1
        print("  !! 读档仍在重算历史点差成本 —— 同一笔交易的成本会在重启前后变化（R06）")

    # R08：梭哈必须把手续费一起算进预算，否则开不到最大量。
    if "m + feeForMax" in win9:
        print("  OK 梭哈预算含手续费（能开到最大可负担量）")
    else:
        fail += 1
        print("  !! 梭哈只回填保证金 —— 会少开一截（R08）")

    # D3：读档后必须重新裁剪 K 线缓冲。
    if "TrimToMaxCandles" in core9:
        print("  OK 读档后重新裁剪 K 线（防御超量存档）")
    else:
        fail += 1
        print("  !! 读档不裁剪 K 线 —— 来自旧上限的存档会一直超量")

    # D4：GetCandles 不应每帧分配。
    if "viewLive.close = last" in core9 and "viewBuf.Clear()" in core9:
        print("  OK GetCandles 复用缓冲（不再每帧分配）")
    else:
        fail += 1
        print("  !! GetCandles 又变成每帧分配 —— 图表开着时会持续产生垃圾")

    # ---- 10. 幽灵账户（2026-10-23 修「一次蹦出两三封结算信」）----
    #
    # 现象：结算时同时出现两三封「每日结算」，其中几封盈亏写 0。
    # 根因：账户按 map.Parent.ID 建，而 HUD（每帧 Find.CurrentMap）与心情系统
    #      （每 120 tick 的殖民者所在地图）会为任何地图建账户；账户从不删除，
    #      结算又遍历全部账户，空账户的当日盈亏恒为 0，只能走平淡日摘要分支，
    #      每 3 天各发一封 0 盈亏的信。
    # 实测：主存档有 3 个账户，744 正常，920/904 全空且世界里已无对应对象。
    core10 = re.sub(r"//[^\n]*", "", core)

    # ① 只有玩家殖民地才建账户
    if "IsPlayerHome" in core10 and re.search(r"if \(!map\.IsPlayerHome\) return null;", core10):
        print("  OK 只为玩家殖民地建账户（不再产生幽灵账户）")
    else:
        fail += 1
        print("  !! AccountFor 又会对任意地图建账户 —— 任务图/伏击图会留下幽灵账户")

    # ② 结算时必须剪掉空账户
    if "IsEmptyAccount" in core10 and "accounts.Remove(keys[k])" in core10:
        print("  OK 结算时剪掉空账户（存量幽灵不再发信）")
    else:
        fail += 1
        print("  !! 结算不再清理空账户 —— 老存档的幽灵账户会继续每 3 天发一封 0 盈亏的信")

    # ③ 结算必须只处理还活着的殖民地
    if "liveIds" in core10:
        print("  OK 结算只处理存活的殖民地地图")
    else:
        fail += 1
        print("  !! 结算没有按存活地图过滤")

    # ④ 除 ColonyAccount 内部外，不允许直接调 AccountFor(某张图)
    direct = []
    for fname in ("Window_FxTerminal.cs", "FxMood.cs", "FxHud.cs", "Building_FXTradingTerminal.cs"):
        fp = os.path.join(ROOT, "Source", fname)
        if not os.path.exists(fp):
            continue
        with open(fp, encoding="utf-8") as fh:
            src = re.sub(r"//[^\n]*", "", fh.read())
        for i, line in enumerate(src.split("\n"), 1):
            if ".AccountFor(" in line:
                direct.append("%s:%d" % (fname, i))
    if not direct:
        print("  OK 外部一律走 ColonyAccount（没有直接 AccountFor(当前地图)）")
    else:
        fail += 1
        print("  !! 这些地方又直接向地图要账户了: " + ", ".join(direct))

    # ⑤ 结算发信只看"有没有动"，不再按盈亏金额（2026-10-23 改）
    # 规则：动过仓 或 收盘还有持仓 → 发；空仓又没交易 → 彻底静默，没有摘要信。
    if re.search(r"if \(tradedToday \|\| openCount > 0\)", core):
        print("  OK 结算发信只看动没动仓 / 有没有持仓（空仓静默）")
    else:
        fail += 1
        print("  !! 结算发信条件被改回按盈亏金额了 —— 空仓的日子又会刷信")
    if "quietDays" not in core and "quietDaysBeforeDigest" not in core:
        print("  OK 平淡日摘要信已彻底移除")
    else:
        fail += 1
        print("  !! 平淡日摘要信复活了 —— 空仓也会每 N 天冒一封")

    # ---- 11. 终端可搬运（2026-10-23 加）----
    # 玩家要求能「卸载 → 搬走 → 在别处重装」，而不是拆了重建。
    # 两个字段必须成对，少一个就出问题：
    #   · minifiedDef     决定 Minifiable（ThingDef.Minifiable 就是 minifiedDef != null）
    #   · thingCategories 可搬运却不在任何存储分类里 → 配置错误
    bdef = ""
    bp = os.path.join(ROOT, "Defs", "ThingDefs_Buildings.xml")
    if os.path.exists(bp):
        with open(bp, encoding="utf-8") as fh:
            bdef = fh.read()
        # 先剥掉 XML 注释：注释里可能写着"不要这样写"的反例
        # （本文件里就有一条把 <Minifiable> 当反例写着），否则会误报。
        bdef = re.sub(r"<!--.*?-->", "", bdef, flags=re.S)
    if "<minifiedDef>MinifiedThing</minifiedDef>" in bdef:
        print("  OK 终端可搬运（有 minifiedDef，原版会给出「重新安装」命令）")
    else:
        fail += 1
        print("  !! 终端又变回不可搬运 —— 玩家只能拆了重建")
    if "<minifiedDef>" in bdef and "<thingCategories>" not in bdef:
        fail += 1
        print("  !! 可搬运但缺 thingCategories —— 会触发 is minifiable but not in any thing category")
    if "<Minifiable>" in bdef:
        fail += 1
        print("  !! 写了 <Minifiable> —— 原版没有这个字段，判定只看 minifiedDef")

    # 可搬运 ⇒ 必须有质量。Def 校验原文：
    #   "is haulable, but does not have an authored mass value"
    # 加 minifiedDef 的当天就踩了这个坑（只在主菜单加载 Def 时报红字，跑脚本看不出来）。
    if "<minifiedDef>" in bdef and re.search(r"<Mass>[\d.]+</Mass>", bdef):
        print("  OK 可搬运建筑有质量（不会报 is haulable, but does not have an authored mass value）")
    else:
        fail += 1
        print("  !! 可搬运但没有 <Mass> —— 主菜单会报红字：is haulable, but does not have an authored mass value")

    # ---- 12. 杠杆滑块（2026-10-23 改，原来是一排按钮）----
    # 离散档位而不是 1~100 连续：线性滑轨上 1~10 只占 9%，低倍区点不准。
    win12 = re.sub(r"//[^\n]*", "", win)
    if "LeverageLadder" in win12 and "Widgets.HorizontalSlider" in win12:
        print("  OK 杠杆用离散档位滑块（不是一排按钮）")
    else:
        fail += 1
        print("  !! 杠杆区又变回按钮 / 或滑块被删了")
    if re.search(r"MaxLadderIndex\(", win12) and "LeverageSafeFor" in win12:
        print("  OK 滑块上限仍受安全杠杆约束（拖不到解锁即强平的档位）")
    else:
        fail += 1
        print("  !! 滑块没有夹到安全杠杆上限 —— 玩家能滑出必死档位")
    if re.search(r"float\[\]\s+tiers", win12) or re.search(r"float\[\]\s+all\s*=\s*\{", win12):
        fail += 1
        print("  !! 又出现硬编码的杠杆档位数组（应该统一用 LeverageLadder）")

    # ---- 13. 悬浮窗三件事 + 最小值跟随（2026-10-23 轮 3）----
    hudp = os.path.join(SRC, "FxHud.cs")
    hud = ""
    if os.path.exists(hudp):
        with open(hudp, encoding="utf-8") as fh:
            hud = fh.read()
    bp2 = os.path.join(SRC, "Building_FXTradingTerminal.cs")
    bsrc = ""
    if os.path.exists(bp2):
        with open(bp2, encoding="utf-8") as fh:
            bsrc = fh.read()
    hud13 = re.sub(r"//[^\n]*", "", hud)
    win13 = re.sub(r"//[^\n]*", "", win)
    core13 = re.sub(r"//[^\n]*", "", core)

    if "ClosePositionFromUi" in core13 and "ClosePositionFromUi" in win13 and "ClosePositionFromUi" in hud13:
        print("  OK 平仓记账只有一份（终端与悬浮窗共用 ClosePositionFromUi）")
    else:
        fail += 1
        print("  !! 悬浮窗平仓没走共用逻辑 —— 两处记账迟早会分叉")

    if "TryOpenTerminalWindow" in hud13 and "TryOpenTerminalWindow" in re.sub(r"//[^\n]*", "", bsrc):
        print("  OK 悬浮窗可以一键打开终端")
    else:
        fail += 1
        print("  !! 悬浮窗打不开终端了")

    if "expanded" in hud13 and "RimForex_HudClose" in hud13:
        print("  OK 悬浮窗可展开持仓明细并逐笔平仓")
    else:
        fail += 1
        print("  !! 悬浮窗的持仓明细 / 平仓按钮被删了")

    # 悬浮窗平仓必须也受电力约束 —— 否则它是绕开 250W 的后门
    if "HasUsableTerminal" in hud13 and re.search(r"hasPower && fx != null && fx\.CanClose", hud13):
        print("  OK 悬浮窗平仓同样要求通电的终端（不是绕开电力的后门）")
    else:
        fail += 1
        print("  !! 悬浮窗平仓不受电力约束 —— 终端断电也能平仓")

    if "marginAtMinimum" in win13 and re.search(r"if \(marginAtMinimum\)", win13):
        print("  OK 最小值跟随生效（按过最小值后改杠杆会重算，手动输入即解除）")
    else:
        fail += 1
        print("  !! 最小值跟随被删了 —— 1x 点最小值再拉到 100x 会保留旧金额")

    # ---- 14. 0.3.0：隔夜利息 + 借贷体系（preview1）----
    core14 = re.sub(r"//[^\n]*", "", core)

    if "ChargeCarry(acc)" in core14 and "RimForex_LetterCarry" in core14:
        print("  OK 隔夜利息在结算时收取，并写进结算信")
    else:
        fail += 1
        print("  !! 隔夜利息没接进结算，或结算信没有单列一行")

    # 单利：利息只能按 principal 算，不得资本化进欠款
    if re.search(r"loan\.principal \* LenderDailyRate", core14):
        print("  OK 利息按本金 × 日利率累计（单利，期限按日）")
    else:
        fail += 1
        print("  !! 利息不再按本金计算 —— 单利语义被改")
    if re.search(r"owed \*= |unpaidInterest \*= |principal \+=", core14):
        fail += 1
        print("  !! 出现利息资本化写法（*= 或 principal +=）—— 必须是单利")

    # 借款必须计入现金流，否则当日结算把借款算成盈利
    if re.search(r"cashFlowSinceSettle \+= amount", core14):
        print("  OK 借款计入现金流（不会被算成当日盈利）")
    else:
        fail += 1
        print("  !! 借款没进 cashFlowSinceSettle —— 结算会把借款当盈利")

    # 事件必须先筛"这个派系能不能产生可观察效果"
    # （2026-10-25 实测：E2 连点三次都是"商队未能成行"，因为挑到了已灭亡/无商队的派系）
    if "CanSendCaravan" in core14 and "CanFireNow" in core14:
        print("  OK 派商队前先问原版 CanFireNow（不会对发不出商队的派系许诺）")
    else:
        fail += 1
        print("  !! 没有 CanSendCaravan 过滤 —— 会对发不出商队的派系发信")

    # 上限必须走缩放函数，且净值必须扣掉负债
    if "LenderLimit(" in core14 and "TryBorrow" in core14:
        print("  OK 借款上限经过 LenderLimit（随净值缩放）")
    else:
        fail += 1
        print("  !! 借款上限没有走缩放函数")

    # 净值必须扣掉负债，否则"借钱→净值涨→上限涨→再借"会成为正反馈
    # （2026-10-25 实测：连点 8 次，借款额从 1 涨到 14123）
    if re.search(r"Equity - TotalOwed\(acc\)", core14):
        print("  OK 借款上限用「净值 − 负债」（借款不会抬高自己的上限）")
    else:
        fail += 1
        print("  !! 借款上限没扣负债 —— 借得越多上限越高，可以无限借")

    # 违约必须打进余额
    if re.search(r"acc\.balance -= owed;", core14):
        print("  OK 违约把欠款打进余额（会转负 → 交给收债流程）")
    else:
        fail += 1
        print("  !! 违约没有写进余额 —— 借钱不还会没有代价")

    print()
    print("结果:", "全部通过" if fail == 0 else "%d 项失败" % fail)
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
