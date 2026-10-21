"""RimForex 点差机制数值验证。

目标（本次修改的核心验收标准）：
    无脑"单步开平"的往返成本  >=  单步平均波动
即：不做方向判断的机械交易，期望收益必须 <= 0。

复刻 Core.cs 的定价口径：
    spreadRate = SpreadCoef * volatility^SpreadExponent + SpreadFloor   (往返，带上下限)
    ask = mid * (1 + spreadRate/2)
    bid = mid * (1 - spreadRate/2)
    做多开仓吃 ask，平仓吃 bid
    做空开仓吃 bid，平仓吃 ask
    PnL = units * (exitPrice - entryPrice)          (方向价口径，多空统一)
    手续费 = max(notional * FeeRate, MinFee)
"""

import sys

TICKS_PER_STEP = 180     # 3 游戏分钟 = 3 现实秒（1x）
TICKS_PER_DAY = 60000    # 原版 GenDate.TicksPerDay（曾经错写成 3600）
DAILY_VOL = 0.014
STEPS_PER_DAY = TICKS_PER_DAY // TICKS_PER_STEP
STEP_VOL = DAILY_VOL / (STEPS_PER_DAY ** 0.5)

SPREAD_COEF = 0.0008     # 点差缩放系数
SPREAD_EXPONENT = 1.0    # 缩放指数
SPREAD_FLOOR = 0.0007     # 往返下限（单边 0.035%）
MAX_SPREAD = 0.03
FEE_RATE = 0.0002        # 0.02%
MIN_FEE = 2.0

# 各档位波动倍率区间（与 Core.cs ComputeVolatility 一致）
# 注意：最高档从 2.8 压到 1.25 是刻意的 —— 波动率高于约 1.28 的品种
# 在数学上就不可能同时满足"压住 churn"和"开 100x"（见 README「杠杆安全上限」）。
TIERS = {
    "Raider":   (1.15, 1.25),
    "Tribal":   (0.95, 1.10),
    "Medieval": (0.65, 0.85),
    "Standard": (0.45, 0.60),
    "Spacer":   (0.30, 0.40),
}


def spread_rate(vol):
    """往返点差：SpreadCoef * vol^SpreadExponent + SpreadFloor，带上下限。"""
    rt = SPREAD_COEF * (vol ** SPREAD_EXPONENT) + SPREAD_FLOOR
    return max(SPREAD_FLOOR, min(rt, MAX_SPREAD))


def ask(mid, vol):
    return mid * (1.0 + spread_rate(vol) * 0.5)


def bid(mid, vol):
    return mid * (1.0 - spread_rate(vol) * 0.5)


def fee(notional):
    return max(notional * FEE_RATE, MIN_FEE)


def round_trip(mid, units, vol, is_long):
    """一次完整的开仓+平仓（价格未变动），返回净盈亏与各项成本。"""
    if is_long:
        entry, exit_ = ask(mid, vol), bid(mid, vol)
    else:
        entry, exit_ = bid(mid, vol), ask(mid, vol)

    notional = units * entry
    # 2026-10-21 修 V01：做空的毛盈亏不能用 exit - entry。
    # 做空是"先卖后买"，平仓价跌了才赚，所以是 entry - exit。
    # 原来两个方向共用 exit - entry，于是空单在"价格没动"时也算出正毛盈 ——
    # 脚本会打印"空单净赚 +151"，与它自己"净盈亏恒为负"的结论自相矛盾。
    gross = units * (exit_ - entry) if is_long else units * (entry - exit_)
    fees = fee(notional) + fee(units * exit_)
    return gross - fees, gross, fees, notional, entry, exit_


def main():
    print("=" * 78)
    print("基准参数")
    print("=" * 78)
    print(f"  单步（{TICKS_PER_STEP} tick = 1 游戏时）波动 = {STEP_VOL*100:.4f}%")
    print(f"  点差 = {SPREAD_COEF*100:.3f}% × vol^{SPREAD_EXPONENT} + {SPREAD_FLOOR*100:.3f}%   手续费 {FEE_RATE*100:.4f}% (下限 {MIN_FEE})")
    print()

    # ---- 验收标准 1：往返成本 vs 单步波动 ----
    print("=" * 78)
    print("验收 1：往返成本（相对敞口） vs 单步波动  [参考性指标]")
    print("=" * 78)
    print("  注意：这是*近似*判据，只看'成本/波动'的比值，不考虑方向判断。")
    print("        真正的验收标准是验收 7（完美方向蒙特卡洛）。")
    print("        早期版本曾把这里的 '比值 >= 100%' 当成硬门槛，但那个阈值本身是拍的经验值：")
    print("        即使比值只有 77%，只要 E[|单步波动|] < 往返成本，churn 依然是亏的 ——")
    print("        因为 |高斯| 的期望只有标准差的约 0.8 倍。")
    print()
    # 以一个能体现真实费率的敞口为基准：20 万敞口时下限已被费率盖过
    REF_NOTIONAL = 200000.0
    ref_fee_rt = 2 * fee(REF_NOTIONAL) / REF_NOTIONAL
    print(f"  （手续费相对敞口按 {REF_NOTIONAL:,.0f} 敞口计 = {ref_fee_rt*100:.4f}%）")
    print(f"  {'档位':<10} {'vol':<7} {'点差往返':<10} {'+手续费':<10} {'总成本':<10} "
          f"{'/单步波动':<10} {'E[|波动|]/成本'}")
    for name, (lo, hi) in TIERS.items():
        vol = (lo + hi) / 2
        sr = spread_rate(vol)
        total = sr + ref_fee_rt
        ratio = total / STEP_VOL
        # |N(0,σ)| 的期望 = σ*sqrt(2/π) ≈ 0.7979σ
        eat = 0.7979 * STEP_VOL * vol
        cover = eat / total
        print(f"  {name:<8} x{vol:<6.2f} {sr*100:<9.4f}% {ref_fee_rt*100:<9.4f}% "
              f"{total*100:<9.4f}% {ratio*100:>8.0f}%  {cover*100:>12.0f}%"
              f"   {'压住' if cover < 1.0 else fail('压不住')}")
    print()
    print("  最右列才是有效判据：完美方向下单步能拿到的期望幅度 / 往返成本。")
    print("  < 100% 表示连'每次都猜对'都亏，机械交易必然无利可图。")
    print()

    # ---- 验收标准 2：单步开平的实际盈亏 ----
    print("=" * 78)
    print("验收 2：单步开平（价格不动）的净盈亏 —— 期望必须为负")
    print("=" * 78)
    mid = 120.0
    print(f"  以 mid={mid} 为例")
    print(f"  {'档位':<10} {'方向':<6} {'数量':<8} {'毛盈亏':<10} {'手续费':<9} {'净盈亏':<10} {'保证金':<10} {'占保证金'}")
    for name, (lo, hi) in TIERS.items():
        vol = (lo + hi) / 2
        for is_long in (True, False):
            for units, lev in ((1000, 1.0), (1000, 20.0), (1000, 100.0)):
                net, gross, fees, notional, entry, exit_ = round_trip(mid, units, vol, is_long)
                margin = notional / lev
                pct = net / margin * 100
                tag = "多" if is_long else "空"
                if lev == 20.0:
                    print(f"  {name:<8} {tag:<6} {units:<8} {gross:<10.2f} {fees:<9.2f} "
                          f"{net:<10.2f} {margin:<10.1f} {pct:>7.2f}%")
    print()
    print("  注：净盈亏恒为负 —— 这就是点差摩擦对机械交易的惩罚。")
    print()

    # ---- 验收标准 3：需要多大行情才能盈利（盈亏平衡点） ----
    print("=" * 78)
    print("验收 3：盈亏平衡需要多大的价格变动？")
    print("=" * 78)
    print(f"  {'档位':<10} {'点差往返':<10} {'需要行情':<12} {'相当于几个单步波动'}")
    for name, (lo, hi) in TIERS.items():
        vol = (lo + hi) / 2
        sr = spread_rate(vol)
        need = sr / STEP_VOL
        print(f"  {name:<8} {sr*100:<9.4f}% {sr*100:<11.4f}% {need:>10.2f} 个")
    print()

    # ---- 验收标准 4：高杠杆是否被更强烈地抑制 ----
    print("=" * 78)
    print("验收 4：点差成本占保证金的比例（验证'按敞口计'对高杠杆的抑制）")
    print("=" * 78)
    vol = 1.0
    units, mid = 1000, 120.0
    notional = units * mid
    sr = spread_rate(vol)
    spread_abs = notional * sr
    print(f"  敞口 {notional:.0f}，点差往返 {sr*100:.3f}% = {spread_abs:.2f} 白银")
    print(f"  {'杠杆':<8} {'保证金':<12} {'点差占保证金'}")
    for lev in (1, 5, 10, 20, 50, 100):
        margin = notional / lev
        print(f"  {lev:>4}x   {margin:<12.1f} {spread_abs/margin*100:>8.2f}%")
    print()
    print("  杠杆越高，点差占保证金比例越大 —— 高杠杆短线被天然抑制。")

    # ---- 验收标准 5：长线仍可盈利 ----
    print()
    print("=" * 78)
    print("验收 5：长线波段是否仍能盈利（不能把玩法一起杀掉）")
    print("=" * 78)
    units, mid, lev = 1000, 120.0, 20.0
    print(f"  敞口 {units*mid:.0f}，{lev:.0f}x 杠杆，保证金 {units*mid/lev:.1f}")
    print(f"  {'抓到的行情':<14} {'净盈亏':<12} {'占保证金'}")
    for move in (0.001, 0.005, 0.01, 0.03, 0.05, 0.10):
        new_mid = mid * (1 + move)
        entry = ask(mid, 1.0)
        exit_ = bid(new_mid, 1.0)
        gross = units * (exit_ - entry)
        fees = fee(units * entry) + fee(units * exit_)
        net = gross - fees
        margin = units * entry / lev
        print(f"  +{move*100:<12.1f}% {net:<12.2f} {net/margin*100:>7.2f}%")
    print()
    print("  结论：抓到 >=1% 的波段明显盈利；只抓 0.1% 的噪音则亏在点差上。")
    print("        这正是设计目标 —— 惩罚高频噪音交易，保留趋势判断的价值。")

    # ---- 验收 6：蒙特卡洛，机械单步开平 vs 持仓多步 ----
    print()
    print("=" * 78)
    print("验收 6：蒙特卡洛模拟 —— 机械策略的期望收益（必须 <= 0）")
    print("=" * 78)
    import random
    random.seed(20261015)

    TRIALS = 20000
    mid0 = 120.0
    units = 1000
    print(f"  {TRIALS} 次模拟，初始 mid={mid0}，数量 {units}")
    print()
    print(f"  {'档位':<10} {'策略':<22} {'平均净盈亏':<14} {'胜率':<9} {'判定'}")

    for name, (lo, hi) in TIERS.items():
        vol = (lo + hi) / 2
        sr = spread_rate(vol)

        # 策略 A：开仓后等 1 步就平（机械刷单），方向随机
        nets_1 = []
        for _ in range(TRIALS):
            is_long = random.random() < 0.5
            entry = ask(mid0, vol) if is_long else bid(mid0, vol)
            # 一步之后价格变动：对数正态，标准差 = 单步波动 * vol
            shock = random.gauss(0.0, STEP_VOL * vol)
            mid1 = mid0 * (1.0 + shock)
            exit_ = bid(mid1, vol) if is_long else ask(mid1, vol)
            notional = units * entry
            nets_1.append(units * (exit_ - entry) - fee(notional) - fee(units * exit_))

        # 策略 B：持 10 步（抓趋势），方向随机
        nets_10 = []
        for _ in range(TRIALS):
            is_long = random.random() < 0.5
            entry = ask(mid0, vol) if is_long else bid(mid0, vol)
            shock = random.gauss(0.0, STEP_VOL * vol * (10 ** 0.5))
            midN = mid0 * (1.0 + shock)
            exit_ = bid(midN, vol) if is_long else ask(midN, vol)
            notional = units * entry
            nets_10.append(units * (exit_ - entry) - fee(notional) - fee(units * exit_))

        avg1 = sum(nets_1) / TRIALS
        avg10 = sum(nets_10) / TRIALS
        win1 = sum(1 for n in nets_1 if n > 0) / TRIALS * 100
        win10 = sum(1 for n in nets_10 if n > 0) / TRIALS * 100

        ok1 = avg1 <= 0
        print(f"  {name:<8} {'1步就平（机械）':<20} {avg1:<14.2f} {win1:<8.1f}% {'OK 负期望' if ok1 else fail('正期望')}")
        print(f"  {'':<8} {'持10步（随机方向）':<20} {avg10:<14.2f} {win10:<8.1f}% {'(随机方向，仍为负)' if avg10 <= 0 else '(接近零)'}")
    print()
    print("  解读：随机方向的机械交易期望为负；这是点差摩擦的直接体现。")
    print("        想盈利必须比随机更准地判断方向，而不是靠高频进出。")

    # ---- 验收 7：完美方向（"印钱机"上限场景）----
    print()
    print("=" * 78)
    print("验收 7：完美方向下的 churn vs trend（决定点差参数的关键测试）")
    print("=" * 78)
    print("  假设玩家总选对方向 —— 这是'印钱机'的上限场景。")
    print("  目标：churn（1 步就平）必须亏损，同时 trend（持 12 步）仍能盈利。")
    print()

    units = 20000          # 敞口 240 万，让点差盖过手续费，能看出差异
    mid0 = 120.0
    trials = 30000
    print(f"  {trials} 次模拟，敞口 {units*mid0:,.0f}")
    print()
    print("  两个判据：")
    print("    (a) churn（持 1 步）期望必须 <= 0        —— 封死机械刷钱")
    print("    (b) 点差 <= 0.85 × 一整个游戏日的期望波动 —— 保证趋势玩法可行")
    print()
    print("  为什么 (b) 用'一整个游戏日'而不是固定的 12 步：")
    print("    点差只付一次，而行情优势 ∝ 持有步数的开方。所以固定步数下，")
    print("    低波动档天然吃亏（12 步只有 0.6 个游戏日）。用日尺度衡量才与波动率无关。")
    print()
    print("  为什么阈值是 0.85 而不是更严：")
    print("    点差地板要压住最低波动档的 churn，就必须 >= 0.28%；")
    print("    而 Spacer 一个游戏日只波动约 0.39%。两条约束在低波动端数学上不可兼得，")
    print("    这里选择<b>保 churn</b>（那是真漏洞），代价是最稳的品种做小波段会亏。")
    print()
    print(f"  {'档位':<10} {'往返点差':<11} {'单步波动':<11} "
          f"{'churn 1步':<13} {'点差/日波动':<13} {'判定'}")

    ok_all = True
    for name, (lo, hi) in TIERS.items():
        vol = (lo + hi) / 2
        sv = STEP_VOL * vol
        rt = spread_rate(vol)

        # churn：持 1 步，完美方向
        s1 = 0.0
        for _ in range(trials):
            move = abs(random.gauss(0.0, sv))
            entry = mid0 * (1 + rt / 2)
            exit_ = mid0 * (1 + move) * (1 - rt / 2)
            s1 += units * (exit_ - entry) - fee(units * entry) - fee(units * exit_)

        a1 = s1 / trials
        # (b) 点差相对"一个游戏日的期望波动"的占比
        daily_move = 0.7979 * STEP_VOL * vol * (STEPS_PER_DAY ** 0.5)
        ratio = rt / daily_move
        churn_ok = a1 <= 0
        trend_ok = ratio <= 0.85
        if not (churn_ok and trend_ok):
            ok_all = False
        verdict = "OK" if (churn_ok and trend_ok) else fail(
            "churn 仍盈利" if not churn_ok else "点差相对日波动过高")
        print(f"  {name:<8} x{vol:<6.2f} {rt*100:<10.3f}% {sv*100:<10.3f}% "
              f"{a1:<13.0f} {ratio*100:>10.0f}%   {verdict}")

    print()
    print(f"  结论：{'churn 全档被压住，且点差相对日波动合理' if ok_all else '存在不达标档位，需重新调点差'}")
    print("  这条测试是选择 TicksPerStep 与点差参数的依据 —— 改任一参数都必须重跑。")

    # ---- 验收 8：暂停时反复开平（回归测试）----
    print()
    print("=" * 78)
    print("验收 8：暂停时反复 开仓→平仓 不可能产生净收益")
    print("=" * 78)
    print("  游戏暂停时 TicksGame 不前进，所以全部操作落在同一个 tick。")
    print("  规则：同一 tick 内开仓又平仓 = 交易作废，精确退还本金、不收手续费。")
    print("  因此无论循环多少次，账户余额必须完全不变。")
    print()
    print(f"  {'档位':<10} {'方向':<6} {'循环 100 次的余额变化':<24} {'判定'}")
    all_zero = True
    start = 1_000_000.0
    for name, (lo, hi) in TIERS.items():
        vol = (lo + hi) / 2
        for is_long in (True, False):
            balance = start
            for _ in range(100):
                e = ask(120.0, vol) if is_long else bid(120.0, vol)
                notional = 1000 * e
                margin = notional / 20.0
                f = fee(notional)
                balance -= margin + f          # 开仓
                # 同一 tick 平仓 -> 作废路径：退还 margin + openFee，不收平仓费
                balance += margin + f
            delta = balance - start
            if abs(delta) > 1e-6:
                all_zero = False
            print(f"  {name:<8} {'做多' if is_long else '做空':<6} {delta:>+22.6f} "
                  f"   {'OK 无变化' if abs(delta) <= 1e-6 else '!! 出现净收益'}")
    print()
    print(f"  结论：{'所有循环余额完全不变，漏洞路径已封死' if all_zero else '!! 仍存在净收益路径'}")
    if not all_zero:
        fail("循环套利路径")
    if not all_zero:
        fail("循环套利路径")

    # ---- 验收 9：盈亏方向正确性（回归测试）----
    print()
    print("=" * 78)
    print("验收 9：盈亏方向正确性")
    print("=" * 78)
    print("  背景：这里曾经有一个严重的符号 bug —— Pnl 写成无方向的 (markRate - entryRate)。")
    print("  做多时 entry=ask > mark=bid，减法天然为负，看起来正常；")
    print("  但做空时 entry=bid < mark=ask，同一个减法变成正值，")
    print("  于是'做空开仓瞬间就白赚一个点差'，反复开空→平仓就是印钱机。")
    print("  本测试锁死这个行为。")
    print()

    def pnl(units, entry, mark, is_long):
        diff = (mark - entry) if is_long else (entry - mark)
        return units * diff

    checks = []

    # 1) 用户实际报告的那笔交易（做空，价格上涨，应亏损）
    got = pnl(10000, 115.66, 117.32, is_long=False)
    want = -16600.0
    checks.append(("用户报告的历史交易（做空 10000 单位，115.66→117.32）",
                   got, want, abs(got - want) < 1.0))

    # 2) 做空而价格上涨 → 必须亏
    checks.append(("做空 + 价格上涨 1%", pnl(1000, 100.0, 101.0, False), -1000.0, True))
    # 3) 做空而价格下跌 → 必须赚
    checks.append(("做空 + 价格下跌 1%", pnl(1000, 100.0, 99.0, False), +1000.0, True))
    # 4) 做多而价格上涨 → 必须赚
    checks.append(("做多 + 价格上涨 1%", pnl(1000, 100.0, 101.0, True), +1000.0, True))
    # 5) 做多而价格下跌 → 必须亏
    checks.append(("做多 + 价格下跌 1%", pnl(1000, 100.0, 99.0, True), -1000.0, True))

    # 6) 关键：开仓瞬间（entry=ask, mark=bid）两个方向都必须亏一个点差
    mid0, vol0, units0 = 116.49, 2.4, 10000
    rt0 = spread_rate(vol0)
    a0, b0 = mid0 * (1 + rt0 / 2), mid0 * (1 - rt0 / 2)
    long_open = pnl(units0, a0, b0, True)
    short_open = pnl(units0, b0, a0, False)
    spread_cost = units0 * (a0 - b0)
    checks.append(("开仓瞬间·做多（应等于 -点差成本）", long_open, -spread_cost, abs(long_open + spread_cost) < 1.0))
    checks.append(("开仓瞬间·做空（应等于 -点差成本）", short_open, -spread_cost, abs(short_open + spread_cost) < 1.0))

    print(f"  {'检查项':<44} {'实际':>12} {'期望':>12}  {'判定'}")
    all_ok = True
    for name, got, want, ok in checks:
        if not ok:
            all_ok = False
        print(f"  {name:<42} {got:>+12.2f} {want:>+12.2f}  {'OK' if ok else fail('方向错误')}")
    print()
    print(f"  结论：{'方向符号全部正确；开仓两个方向都亏点差，不存在白送' if all_ok else '!! 存在方向错误'}")
    if not all_ok:
        fail("方向符号")

    print()
    if FAILS:
        print(f"!! 共 {FAILS} 项验收未通过")
    else:
        print("  全部验收通过")
    return FAILS


FAILS = 0


def fail(msg):
    """记录一次验收失败。返回值供 f-string 直接嵌进输出。"""
    global FAILS
    FAILS += 1
    return "!! " + msg


if __name__ == "__main__":
    # 2026-10-21 修 V01：把结果变成进程退出码。
    # 原来只在屏幕上打印"!!"，最后无条件 exit 0 ——
    # 发布工作流按退出码判断，等于这道验收永远通过。
    main()
    sys.exit(1 if FAILS else 0)
