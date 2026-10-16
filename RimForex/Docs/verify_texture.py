#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
纹理度验收 —— 量化"K 线看起来像不像行情"。

背景
----
原来的模型只有一个时间尺度（theta = 0.04 / 步，相关时间 25 步 ≈ 75 现实秒），
噪声在不到 2 根 K 线内就衰减掉 60%，可视窗里只剩"底色"，没有任何可持续的段落。
表现就是图上一条规整的"尺子"。

`Docs/verify_spread.py` 覆盖的是**经济**验收（churn / 点差 / 杠杆 / 盈亏方向），
它验的是**性质**，所以纹理度再差也照样通过 —— 这正是本脚本要补的缺口。

本脚本不依赖游戏，按 Core.cs 的公式离线复刻两套模型：
    旧：单 OU  + 确定性正弦锚
    新：多尺度 OU 堆栈（5 级，tau = 2h / 8h / 1d / 3d / 9d）

判据
----
  1. 预算守恒（回归门禁）：|Σ 2sigma_k^2/(2-theta_k) - sigma_step^2| / sigma_step^2 < 2%
  2. 纹理度：窗口极差 / 单步sigma —— 新模型应显著高于旧模型
  3. 段间离散：窗口切 10 段，段均值的离散度 —— 衡量"有没有波段"
  4. 单步sigma 漂移：新/旧 比值应在 [0.9, 1.1]，否则经济验收的依据变了
  5. 影线/实体：不应因盘内路径的改动而显著变化
"""
import math
import random

# ── 与 Core.cs 同步的常量 ──
DAILY_VOL   = 0.014
STEPS_PER_DAY = 333          # 60000 / 180
SUBS_PER_CANDLE = 6
SUBSTEPS    = 16
THETA_OLD   = 0.04           # 旧单 OU 的每步回归强度
TAU_DAYS    = (1.0 / 12, 1.0 / 3, 1.0, 3.0, 9.0)
WIN_CANDLES = 100

TRIALS = 30
STEPS_PER_CANDLE = SUBS_PER_CANDLE


def ou_kernel(tau_steps):
    a = math.exp(-1.0 / tau_steps)
    return (1.0 - a) / (1.0 + a)


def allocate_budget(sigma_step, steps_per_day):
    """按等方差把单步预算分配给各级尺度 —— 复刻 AllocateOscBudget。"""
    taus = [t * steps_per_day for t in TAU_DAYS]
    k0 = ou_kernel(taus[0])
    raw = [1.0 / math.sqrt(ou_kernel(t) / k0) for t in taus]
    norm = sigma_step / math.sqrt(sum(r * r for r in raw))
    return taus, [r * norm for r in raw]


def step_variance(sig, decay):
    """单尺度 OU 的步位移方差：sigma^2 + (1-a)^2·Var(x)，Var(x) = sigma^2/(1-a^2)。"""
    return sig * sig + (1.0 - decay) ** 2 * (sig * sig / (1.0 - decay * decay))


# ══════════════════════════════════════════════════════════════════
#  判据 1：预算守恒
# ══════════════════════════════════════════════════════════════════
def check_budget():
    print("=" * 92)
    print("判据 1：预算守恒（回归门禁）")
    print("=" * 92)
    sigma_step = DAILY_VOL / math.sqrt(STEPS_PER_DAY)
    taus, sigs = allocate_budget(sigma_step, STEPS_PER_DAY)

    total = 0.0
    print("  %-10s %-12s %-14s %-18s %s" % ("tau(步)", "a", "sigma_k/sigma_step", "步位移方差/sigma^2", "占预算"))
    budget = sigma_step ** 2
    for t, s in zip(taus, sigs):
        a = math.exp(-1.0 / t)
        v = step_variance(s, a)
        total += v
        print("  %-10.1f %-12.6f %-14.4f %-18.6f %.1f%%" % (
            t, a, s / sigma_step, v / budget, v / total * 100 if total else 0))
    err = abs(total - budget) / budget
    ok = err < 0.02
    print()
    print("  Σ 步位移方差 / sigma_step^2 = %.6f   相对误差 %.4f%%   %s" % (
        total / budget, err * 100, "OK" if ok else "!! 超限"))
    print("  Σsigma_k^2 / sigma_step^2        = %.6f" % (sum(s * s for s in sigs) / budget))
    return ok


# ══════════════════════════════════════════════════════════════════
#  两套模型的路径生成（只到"步边界"层，盘内路径另算）
# ══════════════════════════════════════════════════════════════════
def run_old(vol, n_steps, seed, p0=116.0):
    rnd = random.Random(seed)
    sigma = DAILY_VOL * vol / math.sqrt(STEPS_PER_DAY)
    x = math.log(p0)
    anchor = x
    out = []
    for _ in range(n_steps):
        x += rnd.gauss(0.0, 1.0) * sigma - THETA_OLD * (x - anchor)
        out.append(x)
    return out


def run_new(vol, n_steps, seed, p0=116.0):
    rnd = random.Random(seed)
    sigma = DAILY_VOL * vol / math.sqrt(STEPS_PER_DAY)
    taus, sigs = allocate_budget(sigma, STEPS_PER_DAY)
    decays = [math.exp(-1.0 / t) for t in taus]
    x = math.log(p0)
    state = [0.0] * len(taus)
    out = []
    for _ in range(n_steps):
        disp = 0.0
        for k in range(len(taus)):
            state[k] = state[k] * decays[k] + rnd.gauss(0.0, 1.0) * sigs[k]
            disp += state[k] * (decays[k] - 1.0) + rnd.gauss(0.0, 1.0) * sigs[k]
        x += disp
        out.append(x)
    return out


# ══════════════════════════════════════════════════════════════════
#  判据 2 / 3 / 4
# ══════════════════════════════════════════════════════════════════
def measure(fn, vol, seed):
    n = WIN_CANDLES * STEPS_PER_CANDLE
    path = fn(vol, n, seed)
    closes = [path[i] for i in range(STEPS_PER_CANDLE - 1, n, STEPS_PER_CANDLE)]

    step_sd = math.sqrt(sum((path[i + 1] - path[i]) ** 2
                            for i in range(len(path) - 1)) / (len(path) - 1))
    texture = (max(path) - min(path)) / step_sd

    seg = len(closes) // 10
    means = [sum(closes[i * seg:(i + 1) * seg]) / seg for i in range(10)]
    mm = sum(means) / 10
    segdisp = math.sqrt(sum((v - mm) ** 2 for v in means) / 10) / step_sd
    return step_sd, texture, segdisp


def check_texture():
    print()
    print("=" * 92)
    print("判据 2/3/4：纹理度 / 段间离散 / 单步sigma 漂移")
    print("=" * 92)

    stats = {}
    for name, fn in (("旧（单 OU + 正弦锚）", run_old), ("新（多尺度 OU 堆栈）", run_new)):
        sd, tx, sg = [], [], []
        for s in range(TRIALS):
            a, b, c = measure(fn, 1.0, 1000 + s)
            sd.append(a); tx.append(b); sg.append(c)
        mean = lambda v: sum(v) / len(v)
        stats[name] = (mean(sd), mean(tx), mean(sg))
        print("  %-22s 单步sigma=%.4e  纹理度=%.1f  段间离散=%.2f" % (
            name, mean(sd), mean(tx), mean(sg)))

    old = stats["旧（单 OU + 正弦锚）"]
    new = stats["新（多尺度 OU 堆栈）"]

    ok = True
    print()
    print("  %-28s %-12s %s" % ("指标", "新/旧", "判定"))
    r_tx = new[1] / old[1]
    good = r_tx > 1.15
    ok &= good
    print("  %-28s %-12.3f %s" % ("纹理度（越高越有波段）", r_tx,
                                  "OK 明显改善" if good else "!! 没有改善"))

    r_sg = new[2] / old[2]
    good = r_sg > 1.15
    ok &= good
    print("  %-28s %-12.3f %s" % ("段间离散（越高越有段落）", r_sg,
                                  "OK 明显改善" if good else "!! 没有改善"))

    r_sd = new[0] / old[0]
    good = 0.9 <= r_sd <= 1.1
    ok &= good
    print("  %-28s %-12.3f %s" % ("单步sigma（应接近 1）", r_sd,
                                  "OK" if good else "!! 漂移过大，经济验收的依据变了"))
    return ok


# ══════════════════════════════════════════════════════════════════
#  判据 5：影线 / 实体
# ══════════════════════════════════════════════════════════════════
def check_wick_body():
    print()
    print("=" * 92)
    print("判据 5：影线/实体（盘内路径改动不应显著影响它）")
    print("=" * 92)

    def make_osc(seed, n=SUBSTEPS):
        rnd = random.Random(seed)
        w = [0.0] * (n + 1)
        for k in range(1, n + 1):
            w[k] = w[k - 1] + rnd.gauss(0.0, 1.0)
        b = [0.0] * (n + 1)
        mx = 0.0
        for k in range(1, n):
            v = w[k] - (k / n) * w[n]
            b[k] = v
            mx = max(mx, abs(v))
        if mx > 1e-9:
            for k in range(1, n):
                b[k] /= mx
        return b

    def candles(path, osc_pool, vol):
        """按 Core 的采样顺序：每步先记步起点，再记 SUBSTEPS 个子步。"""
        sigma = DAILY_VOL * vol / math.sqrt(STEPS_PER_DAY)
        out = []
        idx = 0
        n_steps = len(path)
        per = STEPS_PER_CANDLE
        for c in range(n_steps // per):
            samples = [path[c * per]]
            for j in range(per):
                start = path[c * per + j]
                end = path[c * per + j + 1] if c * per + j + 1 < n_steps else start
                osc = osc_pool[idx % len(osc_pool)]
                idx += 1
                for k in range(1, SUBSTEPS + 1):
                    t = k / SUBSTEPS
                    samples.append(start + (end - start) * t + sigma * 0.3 * osc[k])
            o = samples[0]
            cl = samples[-1]
            out.append((o, max(samples), min(samples), cl))
        return out

    pool = [make_osc(5000 + b) for b in range(8)]
    single = [pool[0]]      # 旧：所有 K 线共用一条

    print("  %-34s %-14s %-14s %s" % ("盘内路径", "影线/实体", "平均(高-低)", "样本"))
    res = {}
    for name, use_pool in (("旧：全局唯一静态桥", single), ("新：8 条轮换", pool)):
        hl = []; bd = []
        for s in range(20):
            path = run_new(1.0, WIN_CANDLES * STEPS_PER_CANDLE, 2000 + s)
            cs = candles(path, use_pool, 1.0)
            for o, h, l, c in cs:
                hl.append(h - l)
                bd.append(abs(c - o))
        r = (sum(hl) / len(hl)) / (sum(bd) / len(bd))
        res[name] = r
        print("  %-34s %-14.3f %-14.6f %d" % (name, r, sum(hl) / len(hl), len(hl)))

    r = res["新：8 条轮换"] / res["旧：全局唯一静态桥"]
    good = 0.8 <= r <= 1.25
    print()
    print("  影线/实体 新/旧 = %.3f   %s" % (r, "OK 未显著变化" if good else "!! 变化过大"))
    return good


def main():
    print()
    print("纹理度验收 —— 量化 K 线有多像行情")
    print("（离线复刻 Core.cs 的两套模型，不依赖游戏）")
    print()
    results = [
        ("预算守恒", check_budget()),
        ("纹理度/段间离散/单步sigma", check_texture()),
        ("影线/实体", check_wick_body()),
    ]
    print()
    print("=" * 92)
    n_ok = sum(1 for _, v in results if v)
    for name, v in results:
        print("  %s  %s" % ("OK" if v else "!!", name))
    print()
    if n_ok == len(results):
        print("全部通过 —— 多尺度堆栈在不动预算的前提下显著提升了纹理度")
    else:
        print("有 %d 项未通过，见上方明细" % (len(results) - n_ok))
    return 0 if n_ok == len(results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
