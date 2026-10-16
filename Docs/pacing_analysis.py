"""节奏 vs 摩擦 的权衡分析。

约束（互相冲突）：
  A. 单步波动不能太大 —— 否则每次报价价格跳得离谱
  B. 报价间隔不能太长 —— 否则数字看着"卡住"
  C. 往返成本 >= 单步波动 —— 否则机械单步开平是正期望（印钱机）

关键关系：单步波动 = DailyVol / sqrt(StepsPerDay) = DailyVol * sqrt(TicksPerStep/3600)
          即 单步波动 ∝ sqrt(步长)
而点差是"每笔固定比例"，与步长无关。
所以步子迈得越大，波动涨得比点差快，C 就越难满足。

本脚本枚举不同步长，算出"要让机械交易期望为负，点差至少要多大"，
并换算成"盈亏平衡需要抓多少行情"，用来判断是否误伤长线。
"""

TICKS_PER_DAY = 3600
DAILY_VOL = 0.014
REAL_SEC_PER_TICK_1X = 1.0 / 60.0     # 1x 下 60 tick = 1 现实秒
WORKDAY_REAL_SECONDS = 60.0           # 一个游戏日的现实时长（1x）

# 各档位波动倍率
TIERS = {
    "Raider":   (2.0, 2.8),
    "Tribal":   (1.7, 2.4),
    "Medieval": (1.3, 1.8),
    "Standard": (0.8, 1.25),
    "Spacer":   (0.45, 0.75),
}


def analyze(ticks_per_step):
    steps = TICKS_PER_DAY / ticks_per_step
    step_vol_base = DAILY_VOL / (steps ** 0.5)
    sec_per_step = ticks_per_step * REAL_SEC_PER_TICK_1X
    return steps, step_vol_base, sec_per_step


def main():
    print("=" * 92)
    print("步长 → 节奏 与 单步波动")
    print("=" * 92)
    print(f"  {'TicksPerStep':<14} {'每游戏日步数':<14} {'每步现实秒(1x)':<17} {'单步波动(基准)'}")
    for tps in (60, 120, 300, 600, 900, 1800, 3600):
        steps, sv, sec = analyze(tps)
        print(f"  {tps:<14} {steps:<14.0f} {sec:<17.1f} {sv*100:.4f}%")
    print()
    print("  注意：单步波动 ∝ sqrt(步长)。步长 ×10 → 波动只 ×3.16。")
    print()

    print("=" * 92)
    print("要让【机械单步开平】期望为负，往返成本必须 >= 单步波动")
    print("  => 需要的往返点差 = 单步波动 × 安全系数")
    print("=" * 92)
    print(f"  {'步长':<8} {'基准单步波动':<14} {'需要的往返点差(×1.3)':<22} {'换算成盈亏平衡所需行情'}")
    for tps in (60, 120, 300, 600, 900, 1800, 3600):
        steps, sv, sec = analyze(tps)
        need = sv * 1.3
        # 盈亏平衡行情 ≈ 往返点差（相对中间价）
        print(f"  {tps:<8} {sv*100:<13.4f}% {need*100:<21.4f}% {need*100:.4f}% 的行情")
    print()
    print("  对比：一个游戏日的总波动（基准档）约 1.4%。")
    print("  如果盈亏平衡就要 0.7%，等于一天里超过一半的行情幅度都被点差吃掉 —— 会误伤长线。")
    print()

    print("=" * 92)
    print("各档位在【不同点差方案】下的成本/单步波动 比值（>100% 才算压住）")
    print("=" * 92)
    for tps in (120, 300, 600):
        steps, sv_base, sec = analyze(tps)
        print(f"\n  --- 步长 {tps} tick（每步现实 {sec:.0f} 秒，每日 {steps:.0f} 步）---")
        print(f"    {'档位':<10} {'vol':<7} {'单步波动':<11} "
              f"{'方案A 0.1%/边':<15} {'方案B 0.2%/边':<15} {'方案C 0.3%/边'}")
        for name, (lo, hi) in TIERS.items():
            vol = (lo + hi) / 2
            sv = sv_base * vol
            row = f"    {name:<8} x{vol:<6.2f} {sv*100:<10.4f}% "
            for side in (0.001, 0.002, 0.003):
                rt = side * 2
                ratio = rt / sv * 100
                mark = "" if ratio >= 100 else "  <"
                row += f"{ratio:>6.0f}%{mark:<8} "
            print(row)
    print()
    print("  '<' 表示该方案下这个档位压不住，机械交易仍为正期望。")


if __name__ == "__main__":
    main()
