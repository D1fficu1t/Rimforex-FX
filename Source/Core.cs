using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 派系经济体的波动性档位。决定货币汇率的日波动倍率。
    /// 越边缘、越军事化的经济体，汇率越动荡 —— 部落以实物/贝壳计价，没有央行稳定汇率。
    /// </summary>
    public enum FxVolTier
    {
        Standard = 0,
        Medieval = 1,
        Spacer = 2,
        Tribal = 3,
        Raider = 4,
    }

    /// <summary>
    /// 一种"货币"。Alpha 阶段实现为"按派系生成的汇率标的"，以白银计价。
    /// 名称按派系 loadID 作为种子确定性生成，因此同一存档里稳定不变。
    /// </summary>
    public class FxCurrency : IExposable
    {
        /// <summary>白银的 ISO 4217 代码。货币对写法用得到（如 "Credit/XAG"）。</summary>
        public const string QuoteCode = "XAG";

        /// <summary>volatility 未初始化时的哨兵值。旧存档缺该字段时会停在 -1，据此触发重算。</summary>
        private const float VolUnset = -1f;

        public int factionLoadID = -1;
        public string factionName = "Unknown";
        /// <summary>
        /// 派系的 FactionDef.defName。持久化它用来还原 tier ——
        /// 因为 tier 是从 FactionDef 派生的，而旧存档里没有 tier 字段。
        /// 有了 defName 就能通过 DefDatabase 查到 FactionDef，从而还原档位。
        /// </summary>
        public string factionDefName = "";
        public string name = "Unnamed";
        public string symbol = "???";
        public float rate = 100f;
        /// <summary>昨日收盘价，用于展示日涨跌幅。</summary>
        public float previousRate = 100f;
        /// <summary>
        /// 最近一次跳空的百分比幅度（0 = 上一步没跳空）。
        /// 只用于 UI 提示，不参与任何计算，所以不需要持久化。
        /// </summary>
        public float lastGapPct;

        // ── 0.3.0 新增（定名后不可改）──

        /// <summary>该货币上次触发世界事件的 tick（每货币冷却）。</summary>
        public int lastEventTick;
        /// <summary>E4/E5 新闻造成的额外对数漂移（每步）。0 = 无。</summary>
        public float newsDriftLog;
        /// <summary>新闻漂移的到期 tick。</summary>
        public int newsDriftUntilTick;
        /// <summary>
        /// 本步是否包含跳空（运行时，不存盘）。
        ///
        /// 跳空必须是"一瞬间"的事：价格在整步内保持不动，只在最后一个子步砸到位。
        /// 否则会被 16 个子步摊平，价格一格一格走过强平线 ——
        /// 第一个越线的子步就把仓位平掉了，永远走不到负余额（穿仓）。
        /// </summary>
        public bool stepIsGap;

        /// <summary>当前空头名义仓位缓存（E6 检测用）。</summary>
        public float shortNotional;
        /// <summary>空头仓位首次超过门槛的 tick（0 = 当前没超）。</summary>
        public int shortSinceTick;

        /// <summary>
        /// 本报价步内已经推进到第几个子步（0 ~ SubSteps）。
        /// 让推进与渲染解耦：即使一帧跨过多个子步、或低帧率下漏掉若干 tick，
        /// 也能补齐欠下的子步，而不会让 K 线的影线出现空洞。
        /// 运行时状态，无需持久化 —— 读档后从当前步的起点重新累积即可。
        /// </summary>
        public int subStep;

        /// <summary>
        /// 本步的起点与终点（对数空间）。由 <c>TickStep</c> 在步边界一次算好，
        /// 子步在两者之间行走。写成"整步预算"而不是"每子步各抽一份"，
        /// 是为了让步边界位移与原单步实现完全一致（详见 AdvanceSubStep）。
        ///
        /// ⚠️ <b>这两个字段不持久化，读档后必须由 stepBudgetValid 判定为无效，
        /// 否则会把价格打到 MinRate。</b>
        /// 原因：两者默认值都是 0，而 <c>Exp(0) = 1</c> —— 正好等于 MinRate。
        /// 读档后的时序是：第一帧 <c>lastStepTick</c> 才被设为当前 tick（欠 0 个子步），
        /// 但<b>下一帧</b>欠 1 个子步就会调用 AdvanceSubSteps，
        /// 而此时还没有任何 TickStep 跑过 → 价格瞬间变成 1，之后靠均值回归慢慢爬回锚点。
        /// 症状就是"每次读档所有货币跌到极低价再慢慢涨回来"。
        /// </summary>
        public float stepLogStart;
        public float stepLogTarget;

        // ── 多尺度 OU 的运行时状态（<b>不持久化</b>）──
        // 与 stepLogStart 同理：读档后回到零态，而零态 = 零位移 = 价格不动，
        // 所以不会跳价；跑几十步就自然建立稳态。这样就不需要任何存档迁移。
        //
        // ⚠️ 用 double 而不是 float：这些量要跨成千上万步累加，
        //    float 的精度会在长局里漂移。

        /// <summary>各级 OU 的中心化状态量。</summary>
        private double[] ouX;

        /// <summary>各级 σ。由 <see cref="EnsureOsc"/> 按当前预算构建。</summary>
        private double[] ouSigma;

        /// <summary>各级 τ（步）。由 <see cref="EnsureOsc"/> 构建 —— 最后一级受设置影响，所以缓存。</summary>
        private double[] ouTau;

        /// <summary>构建时用的"慢周期行情强度"，用于滑块变化时自动重建。</summary>
        private float ouBuildTrendAmp = -1f;

        /// <summary>构建时用的"慢周期长度"，同上。</summary>
        private float ouBuildTrendPeriod = -1f;

        /// <summary>构建时的 StepsPerDay，用于在节奏变化时自动重建。</summary>
        private int ouBuildSteps = -1;

        /// <summary>构建时用的波动率，用于在档位变化时自动重建。</summary>
        private float ouBuildVol = -1f;

        /// <summary>波动率聚集乘子。<c>E[m²] = 1</c>，不动预算。</summary>
        private double volMult = 1.0;

        /// <summary>
        /// 盘内路径的轮换下标。每步 +1，让每根 K 线的盘中形状不同 ——
        /// 原先所有货币共用同一条静态桥，是"规整感"的一个真实来源。
        /// </summary>
        private int oscIdx;

        /// <summary>
        /// 按当前预算惰性构建多尺度参数；节奏或波动档位变化时自动重建。
        ///
        /// 重建即等价于"本步零位移"：清空状态并把预算置为无效，
        /// 由下一个 TickStep 正式建立 —— 避免改设置/读档瞬间跳价。
        /// </summary>
        public void EnsureOsc()
        {
            float vol = volatility > 0.01f ? volatility : 1f;
            int steps = FxMarket.StepsPerDay;
            RimForexSettings cfg = RimForexMod.Settings;
            float tAmp = cfg != null ? cfg.trendAmp : FxMarket.BaseTrendAmp;
            float tPer = cfg != null ? cfg.trendPeriodDays : FxMarket.BaseTrendPeriodDays;

            // 两个趋势滑块也是构建指纹的一部分：改了它们必须重建 σ 与 τ，
            // 否则玩家拉滑块看不到任何变化（这正是它们原来失效的原因）。
            if (ouX != null && ouBuildSteps == steps && ouBuildVol == vol
                && ouBuildTrendAmp == tAmp && ouBuildTrendPeriod == tPer) return;

            double sigmaStep = FxMarket.DailyVol * vol / System.Math.Sqrt(steps);
            ouSigma = FxMarket.AllocateOscBudget(sigmaStep, steps, out ouTau);
            ouX = new double[ouSigma.Length];
            ouBuildSteps = steps;
            ouBuildVol = vol;
            ouBuildTrendAmp = tAmp;
            ouBuildTrendPeriod = tPer;

            // 重建 = 零位移。把预算标为无效，下一个 TickStep 重新建立，
            // 这样价格停在原处而不会因为状态清零而跳变。
            stepBudgetValid = false;
        }

        /// <summary>推进本货币的多尺度状态一步，返回本步的对数位移（不含跳空）。</summary>
        public double StepOsc()
        {
            double disp = 0.0;
            for (int k = 0; k < ouX.Length; k++)
            {
                // τ 用构建时缓存的 —— 最后一级受"慢周期长度"滑块影响，每次重算既慢又易错
                double a = System.Math.Exp(-1.0 / ouTau[k]);
                // 位移 = 衰减 + 新冲击；衰减部分就是均值回归
                ouX[k] = ouX[k] * a + Rand.Gaussian(0f, 1f) * ouSigma[k];
                disp += ouX[k] * (a - 1.0) + Rand.Gaussian(0f, 1f) * ouSigma[k];
            }
            return disp;
        }

        /// <summary>把振荡子下标轮换到下一个（每步调用一次）。</summary>
        public void AdvanceOscIndex()
        {
            oscIdx = (oscIdx + 1) % FxMarket.OscBankSize;
        }

        /// <summary>当前应使用的盘内路径下标。</summary>
        public int OscIndex { get { return oscIdx; } }

        /// <summary>
        /// 本步的波动率聚集乘子。E[m²] = 1，所以单步方差的期望不变。
        /// VolOfVol = 0 时恒为 1（完全关闭）。
        /// </summary>
        public double VolMult { get { return volMult; } }

        /// <summary>抽取本步的聚集乘子。</summary>
        public void RollVolMult()
        {
            double v = FxMarket.VolOfVol;
            if (v <= 0.0) { volMult = 1.0; return; }
            volMult = System.Math.Exp(Rand.Gaussian(0f, 1f) * (float)v - 0.5f * (float)(v * v));
        }

        /// <summary>
        /// 本步的"整步预算"是否已经由 TickStep 建立。
        /// 见 <see cref="stepLogStart"/> 的说明 —— 它是读档保护的核心。
        /// </summary>
        public bool stepBudgetValid;

        /// <summary>派系波动档位。派生自 FactionDef，每次读档由 factionDefName 重新解析。</summary>
        public FxVolTier tier = FxVolTier.Standard;
        /// <summary>派生属性，读档后由 ResolveTier 标记为已解析。</summary>
        private bool tierResolved;

        /// <summary>供生成器调用：由派系定义直接设定档位并标记为已解析。</summary>
        public void SetTier(FactionDef def)
        {
            tier = TierFor(def);
            tierResolved = true;
        }

        /// <summary>
        /// 波动倍率。1.0 = 基准日波动率。
        /// 注意：这是<b>派生属性</b>，不是存档字段 —— 从 tier + loadID 确定性重算。
        /// 历史上它曾是存档字段，结果旧存档缺失该字段时 Scribe_Values.Look 静默回退到 1f，
        /// 导致所有货币波动倍率都显示 x1.0（并连带让点差倍率全部失效）。
        /// </summary>
        private float _volatility = VolUnset;

        public float volatility
        {
            get
            {
                if (_volatility < 0f) _volatility = ComputeVolatility(tier, factionLoadID);
                return _volatility;
            }
            set { _volatility = value; }
        }

        /// <summary>部落派系（科技 ≤ 新石器）。由 tier 派生，不再单独存档。</summary>
        public bool isTribal => tier == FxVolTier.Tribal;

        /// <summary>货币对写法，例如 "Vega Credit/XAG"。</summary>
        public string PairLabel => name + "/" + QuoteCode;
        public void ExposeData()
        {
            Scribe_Values.Look(ref factionLoadID, "factionLoadID", -1);
            Scribe_Values.Look(ref factionName, "factionName");
            Scribe_Values.Look(ref factionDefName, "factionDefName");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref symbol, "symbol");
            Scribe_Values.Look(ref rate, "rate", 100f);
            Scribe_Values.Look(ref previousRate, "previousRate", 100f);
            Scribe_Values.Look(ref lastEventTick, "lastEventTick", 0);
            Scribe_Values.Look(ref newsDriftLog, "newsDriftLog", 0f);
            Scribe_Values.Look(ref newsDriftUntilTick, "newsDriftUntilTick", 0);
            Scribe_Values.Look(ref shortNotional, "shortNotional", 0f);
            Scribe_Values.Look(ref shortSinceTick, "shortSinceTick", 0);

            // 历史字段：仅读取用于向后兼容，不再写入。
            bool legacyTribal = false;
            Scribe_Values.Look(ref legacyTribal, "isTribal", false);

            float legacyVol = VolUnset;
            Scribe_Values.Look(ref legacyVol, "volatility", VolUnset);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                _volatility = legacyVol;   // 读不到就是哨兵值，等 PostLoadInit 重算
                tier = FxVolTier.Standard;
                tierResolved = false;
            }

            // 读档收尾：这时 Def 与派系都已就绪，把派生值补齐。
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                ResolveTier(legacyTribal);
                _volatility = EnsureVolatility(_volatility, tier, factionLoadID);
            }
        }

        /// <summary>
        /// 还原 tier。优先级：
        ///   1. factionDefName → DefDatabase（最可靠，不依赖派系是否已加载）
        ///   2. factionLoadID  → FactionManager（兜底）
        ///   3. 旧存档的 isTribal 布尔值（最后的线索）
        /// 全部失败时退回 Standard —— 好过让货币停在 1.0 倍的错误状态。
        /// </summary>
        private void ResolveTier(bool legacyTribal)
        {
            if (tierResolved) return;

            if (!string.IsNullOrEmpty(factionDefName))
            {
                FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail(factionDefName);
                if (def != null)
                {
                    tier = TierFor(def);
                    tierResolved = true;
                    return;
                }
            }

            if (factionLoadID >= 0 && Find.FactionManager != null)
            {
                var all = Find.FactionManager.AllFactionsListForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    var f = all[i];
                    if (f != null && f.loadID == factionLoadID && f.def != null)
                    {
                        tier = TierFor(f.def);
                        tierResolved = true;
                        return;
                    }
                }
            }

            if (legacyTribal)
            {
                tier = FxVolTier.Tribal;
                tierResolved = true;
            }
        }

        /// <summary>volatility 的合法区间。旧存档的默认值恰好落在区间外的判定依据。</summary>
        private const float VolMin = 0.40f;
        private const float VolMax = 3.50f;

        /// <summary>
        /// 只接受"看起来像真的算过"的值。旧存档缺失 volatility 字段时，
        /// Scribe 会把它留在默认值 1.0f —— 那个值无法与真实的 1.00 倍区分，
        /// 但波动倍率区间里没有任何一档的区间包含 1.0，所以用它当判据是安全的。
        /// </summary>
        private static float EnsureVolatility(float current, FxVolTier tier, int loadID)
        {
            if (current >= VolMin && current <= VolMax) return current;
            return ComputeVolatility(tier, loadID);
        }

        /// <summary>
        /// 由档位与 loadID 确定性生成波动倍率。同一个派系在任何时候算出来都一样，
        /// 所以不需要存档 —— 这也正是它能自愈旧存档的原因。
        /// </summary>
        public static float ComputeVolatility(FxVolTier tier, int loadID)
        {
            // 用派生种子，避免消耗全局 Rand 状态（否则会受 UI 刷新时机影响）
            int derived = loadID * 31 + (int)tier * 7919 + 104729;
            float v;
            // try/finally 不是洁癖：原版 Rand 的种子栈是**全局**的，
            // Push 之后若中途抛异常，Pop 就永远不会执行，
            // 之后所有用到 Rand 的地方（包括别的 mod）都会拿到错乱的序列。
            Rand.PushState(derived);
            try
            {
                switch (tier)
                {
                    case FxVolTier.Raider: v = Rand.Range(1.15f, 1.25f); break;
                    case FxVolTier.Tribal: v = Rand.Range(0.95f, 1.10f); break;
                    case FxVolTier.Spacer: v = Rand.Range(0.30f, 0.40f); break;
                    case FxVolTier.Medieval: v = Rand.Range(0.65f, 0.85f); break;
                    default: v = Rand.Range(0.45f, 0.60f); break;
                }
            }
            finally
            {
                Rand.PopState();
            }
            return v;
        }

        /// <summary>由派系定义判定波动档位。</summary>
        public static FxVolTier TierFor(FactionDef def)
        {
            if (def == null) return FxVolTier.Standard;
            if (def.permanentEnemy) return FxVolTier.Raider;
            if (def.techLevel <= TechLevel.Neolithic) return FxVolTier.Tribal;
            if (def.techLevel >= TechLevel.Spacer) return FxVolTier.Spacer;
            if (def.techLevel <= TechLevel.Medieval) return FxVolTier.Medieval;
            return FxVolTier.Standard;
        }
    }

    /// <summary>
    /// 一个持仓。
    ///
    /// 定价口径（重要）：<c>entryRate</c> 是开仓成交价，<c>markRate</c> 是
    /// <b>按本仓方向现在平仓能拿到的价</b>（做多看 bid，做空看 ask）。两者都是"方向价"，
    /// 所以浮动盈亏恒为 <c>units × (markRate - entryRate)</c>，多空统一，不需要按方向改符号。
    ///
    /// 副作用（符合真实市场）：开仓瞬间 markRate 就已经被点差推差，
    /// 所以浮动盈亏开局即为一个点差的小额负数 —— 这就是"入场即亏点差"。
    ///
    /// 保证金在开仓时已从账户扣除，因此账户净权益 = 可用余额 + 各持仓保证金 + 浮动盈亏。
    /// </summary>
    public class FxPosition : IExposable
    {
        public int currencyFactionLoadID = -1;
        public string currencyName = "?";
        public string symbol = "?";
        public bool isLong = true;
        public int units;
        public float leverage = 1f;
        public float entryRate;
        public float margin;
        public float markRate;
        public int openTick;
        /// <summary>
        /// 可以平仓的最早 tick（= 开仓时刻 + 一个报价步长）。
        /// 开仓时一次性算好，之后不再变化 —— 用绝对阈值而不是滚动比较，
        /// 否则按钮状态会随着每次报价来回翻转。
        /// </summary>
        public int closableAtTick;
        /// <summary>
        /// 上次发出预警时的保证金健康度。<b>-1 表示从未提醒过</b>。
        /// 只有健康度继续跌到比这个值更低的"水位"才会再次提醒，
        /// 这样玩家点"暂不处理"后不会立刻被同一个提醒反复打扰。
        /// </summary>
        public float warnedHealth = -1f;
        /// <summary>是否已经就"跌破爆仓线"强制提醒过。</summary>
        public bool criticalWarned;

        /// <summary>
        /// 上次预警发生的 tick（0 = 从未预警）。
        ///
        /// 与 <see cref="criticalWarned"/> 一样【不持久化】—— 它只影响"多久之后允许再弹一次"，
        /// 读档后重新计时既安全又简单，不值得为它增加存档字段。
        /// </summary>
        public int warnedAtTick;

        /// <summary>行情回到安全区时复位告警状态，将来再恶化会重新提醒。</summary>
        public void ResetWarnState()
        {
            warnedHealth = -1f;
            criticalWarned = false;
            // warnedAtTick 故意不复位：冷却时间要跨过"复位"继续有效，
            // 否则"回到安全区 → 立刻跌回 → 再弹"还是能刷屏。
        }
        /// <summary>开仓时支付的手续费。平仓时把两次合计写入交易历史。</summary>
        public float openFee;
        /// <summary>开仓时的每单位点差（ask - bid）。用于统计点差成本，不随行情变化。</summary>
        public float spreadAtOpen;
        /// <summary>
        /// 开仓时的往返点差率。用于把 markRate 还原成中间价（见 MarketPnl / SpreadCost）。
        /// 0 表示旧存档没有这个字段，此时按"无点差"退化处理。
        /// </summary>
        public float entrySpreadRate;
        /// <summary>已追加的保证金累计额，仅用于展示。</summary>
        public float addedMargin;

        /// <summary>
        /// 初始保证金（不含后续追加的部分）。
        ///
        /// 它是<b>健康度与强平判据的共同标尺</b>：健康度 = 净值 / 本值，
        /// 强平线 = 本值 × <see cref="StopOutFraction"/>。
        ///
        /// ── 为什么必须是这个，而不是 margin（2026-10-21 修 R05）──
        /// 原来健康度用 (margin − addedMargin) 作分母，强平却用 margin × StopOutFraction，
        /// 追加保证金后两个标尺就永久错位了。
        /// 实测例子：原始 100、追加 100、浮亏 140 →
        ///     健康度 = 60/100 = 0.60（离 45% 预警线还很远）
        ///     强平阈值 = 200 × 0.30 = 60 → 已经满足强平
        /// 玩家会在界面显示"安全"的时候被静默强平。
        /// </summary>
        public float BaseMargin => margin - addedMargin;

        /// <summary>
        /// 保证金健康度：净值 / 初始保证金。1.0 = 打平，0 = 爆仓。
        /// 用初始保证金作分母，追加保证金不会篡改这个比例的含义。
        /// </summary>
        public float MarginHealth
        {
            get
            {
                float baseMargin = BaseMargin;
                if (baseMargin <= 0.01f) return 0f;
                return Equity / baseMargin;
            }
        }

        /// <summary>
        /// 浮动盈亏。
        ///
        /// entryRate / markRate 存的都是"方向价"（做多看 ask/bid，做空看 bid/ask），
        /// <b>但它们本身不带方向符号</b>，所以这里必须按方向翻转：
        ///   做多：units × (markRate - entryRate)     价格涨 → 赚
        ///   做空：units × (entryRate - markRate)     价格涨 → 亏
        ///
        /// 这里曾经是个严重的符号 bug：公式写成无方向的 `markRate - entryRate`。
        /// 做多时点差天然走负（entry=ask &gt; mark=bid），看起来正常；
        /// 但做空时 entry=bid &lt; mark=ask，同一个减法得到<b>正值</b> ——
        /// 于是"做空开仓瞬间就白赚一个点差"，平仓即可锁定，
        /// 反复 开空→平仓 就是一台印钱机。
        /// 症状表现：做空时价格上涨反而盈利（盈利额恰好等于点差 × 数量）。
        /// </summary>
        public float Pnl
        {
            get
            {
                float diff = isLong ? markRate - entryRate : entryRate - markRate;
                return units * diff;
            }
        }

        public float Equity => margin + Pnl;

        /// <summary>
        /// 开仓时的<b>中间价</b>。与 entryRate（方向成交价，含半个点差）不同，
        /// 这是不含点差的市场价，专门用于把盈亏拆成"行情"与"成本"两笔账。
        ///
        /// 0 表示旧存档没有这个字段，此时退化为用 entryRate 估算（分解会略有偏差）。
        /// </summary>
        public float entryMid;

        /// <summary>
        /// 按当前方向平仓时，对应的<b>中间价</b>。
        ///
        /// markRate 是方向价（做多=bid、做空=ask），除以 (1 ∓ 半价差) 就能还原成 mid。
        /// 点差率是货币属性、与时间无关，所以这个还原是精确的。
        /// </summary>
        private float CurrentMid
        {
            get
            {
                if (entrySpreadRate <= 0f) return markRate;
                float half = entrySpreadRate * 0.5f;
                float denom = isLong ? (1f - half) : (1f + half);
                return denom > 0.0001f ? markRate / denom : markRate;
            }
        }

        /// <summary>
        /// <b>纯行情盈亏</b>：中间价从开仓到现在的变动带来的盈亏，<b>不含点差成本</b>。
        ///
        /// ── 为什么必须用中间价口径 ──
        /// entryRate 与 markRate 都是"方向价"，两者相减时点差项对做多是 +、对做空是 −，
        /// 所以无法用同一个式子表达多空的"成本"。换成中间价后点差项消失，
        /// 只剩下纯粹的价格变动，多空可以用同一个符号规则。
        /// </summary>
        public float MarketPnl
        {
            get
            {
                float mid0 = entryMid > 0f ? entryMid : entryRate;
                float diff = isLong ? CurrentMid - mid0 : mid0 - CurrentMid;
                return units * diff;
            }
        }

        /// <summary>
        /// 本仓的<b>入场点差成本</b>（正数，白银）。
        ///
        /// 直接用恒等式反解：<c>成本 = MarketPnl - Pnl</c>。
        /// 这样"总盈亏 = 行情盈亏 - 成本"永远严格成立，不存在两套公式对不上的风险。
        ///
        /// 数额上它等于 <c>units × 中间价 × 往返点差</c> ——
        /// 开仓时付一次（买入吃 ask），平仓时再付一次（卖出吃 bid），合计就是一个往返点差。
        /// </summary>
        public float SpreadCost
        {
            get
            {
                float c = MarketPnl - Pnl;
                return c > 0f ? c : 0f;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref currencyFactionLoadID, "currencyFactionLoadID", -1);
            Scribe_Values.Look(ref currencyName, "currencyName");
            Scribe_Values.Look(ref symbol, "symbol");
            Scribe_Values.Look(ref isLong, "isLong", true);
            Scribe_Values.Look(ref units, "units");
            Scribe_Values.Look(ref leverage, "leverage", 1f);
            Scribe_Values.Look(ref entryRate, "entryRate");
            Scribe_Values.Look(ref margin, "margin");
            Scribe_Values.Look(ref markRate, "markRate");
            Scribe_Values.Look(ref openTick, "openTick");
            Scribe_Values.Look(ref closableAtTick, "closableAtTick", 0);
            Scribe_Values.Look(ref warnedHealth, "warnedHealth", -1f);
            // ⚠️ `criticalWarned` 刻意【不】持久化 —— 它与 `pendingWarnings` 必须同进退。
            //
            // 这两个字段是一对：`criticalWarned = true` 表示"已经塞进预警队列了"，
            // 而队列 `pendingWarnings` 是纯运行时对象、不进存档。
            // 曾经这里也存了 `criticalWarned`，于是：
            //     存档时正弹着预警 → 读档 → 队列空了，但标志还是 true
            //     → MaybeWarn 第 3168 行直接 return
            //     → **这个仓位再也不会被提醒**，直到行情反弹回安全区又跌破。
            //   玩家会毫无预告地被爆仓。
            //
            // 现在两者都只活在运行时：读档后标志为 false，若仓位仍然危险，
            // 下一个 tick 会重新提醒一次 —— 这本来就是更对的行为。
            Scribe_Values.Look(ref openFee, "openFee", 0f);
            Scribe_Values.Look(ref spreadAtOpen, "spreadAtOpen", 0f);
            Scribe_Values.Look(ref entrySpreadRate, "entrySpreadRate", 0f);
            Scribe_Values.Look(ref entryMid, "entryMid", 0f);
            Scribe_Values.Look(ref addedMargin, "addedMargin", 0f);
            Scribe_Values.Look(ref entryHasSpread, "entryHasSpread", false);
        }

        /// <summary>
        /// 该持仓的开仓价是否已按点差口径记录。
        /// 引入点差之前，entryRate/markRate 都是中间价；旧存档读进来后需要折算，
        /// 否则老的价差口径会与新口径混用，盈亏会算错。
        /// </summary>
        public bool entryHasSpread;
    }

    /// <summary>一根 K 线（OHLC）。</summary>
    public class FxCandle : IExposable
    {
        public float open, high, low, close;

        public FxCandle() { }

        public FxCandle(float o, float h, float l, float c)
        {
            open = o; high = h; low = l; close = c;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref open, "open");
            Scribe_Values.Look(ref high, "high");
            Scribe_Values.Look(ref low, "low");
            Scribe_Values.Look(ref close, "close");
        }
    }

    /// <summary>一种货币的 K 线序列。仅在模拟推进时采样，因此暂停时会自然冻结。
    ///
    /// 三级周期（分/时/日）各有一份独立缓冲，见 <see cref="FxChartSpan"/>。</summary>
    public class FxRateSeries : IExposable
    {
        public List<FxCandle> candles = new List<FxCandle>();
        /// <summary>当前 K 线的起始 tick。-1 表示尚未初始化。</summary>
        public int periodStartTick = -1;
        public float curOpen, curHigh, curLow;

        // ── 时K / 日K 的独立缓冲 ──
        // 旧存档读进来时这两个列表为 null，按"空"处理，之后自然累积。

        /// <summary>时K（1 游戏小时一根）。</summary>
        public List<FxCandle> hourCandles = new List<FxCandle>();
        public int hourStartTick = -1;
        public float hourOpen, hourHigh, hourLow;

        /// <summary>日K（1 游戏日一根）。</summary>
        public List<FxCandle> dayCandles = new List<FxCandle>();
        public int dayStartTick = -1;
        public float dayOpen, dayHigh, dayLow;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref candles, "candles", LookMode.Deep);
            Scribe_Values.Look(ref periodStartTick, "periodStartTick", -1);
            Scribe_Values.Look(ref curOpen, "curOpen", 0f);
            Scribe_Values.Look(ref curHigh, "curHigh", 0f);
            Scribe_Values.Look(ref curLow, "curLow", 0f);
            if (candles == null) candles = new List<FxCandle>();

            // 时K / 日K（旧存档没有这些字段，读进来是 null，下面补空列表）
            Scribe_Collections.Look(ref hourCandles, "hourCandles", LookMode.Deep);
            Scribe_Values.Look(ref hourStartTick, "hourStartTick", -1);
            Scribe_Values.Look(ref hourOpen, "hourOpen", 0f);
            Scribe_Values.Look(ref hourHigh, "hourHigh", 0f);
            Scribe_Values.Look(ref hourLow, "hourLow", 0f);
            if (hourCandles == null) hourCandles = new List<FxCandle>();

            Scribe_Collections.Look(ref dayCandles, "dayCandles", LookMode.Deep);
            Scribe_Values.Look(ref dayStartTick, "dayStartTick", -1);
            Scribe_Values.Look(ref dayOpen, "dayOpen", 0f);
            Scribe_Values.Look(ref dayHigh, "dayHigh", 0f);
            Scribe_Values.Look(ref dayLow, "dayLow", 0f);
            if (dayCandles == null) dayCandles = new List<FxCandle>();

            // ── 读档后重新裁剪（2026-10-21 补 D3）──
            // 写入前一定会裁到 MaxCandles，所以正常存档不会超。
            // 但存档可能来自上限更大的旧版本、被外部工具改过、或来自别的分支 ——
            // 那些情况下列表会原样保留，直到这根 K 线再次封根才被裁，
            // 中间的显示与内存都按超量算。这里补一道防御，成本可以忽略。
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                TrimToMaxCandles(candles);
                TrimToMaxCandles(hourCandles);
                TrimToMaxCandles(dayCandles);
            }
        }

        /// <summary>把缓冲裁到 <see cref="FxMarket.MaxCandles"/>，保留最新的那些。</summary>
        private static void TrimToMaxCandles(List<FxCandle> buf)
        {
            if (buf == null) return;
            int extra = buf.Count - FxMarket.MaxCandles;
            if (extra > 0) buf.RemoveRange(0, extra);
        }
    }

    /// <summary>
    /// 图表周期。三级<b>互相独立聚合</b>，不是同一批 K 线换个标签 ——
    /// 因为一根日 K 要横跨 333 个报价步，靠"每步一根再合并"既费内存又要遍历 20 万个子步。
    ///
    /// 三级各自维护自己的"当前未完成 K 线"，由 <c>PeriodTicksOf</c> 决定的
    /// 绝对相位触发封根。三级共用同一串价格路径，所以互相自洽。
    /// </summary>
    public enum FxChartSpan
    {
        /// <summary>分K：按模组设置「每根K线由几次报价合成」决定，默认 6 次报价 ≈ 26 游戏分钟。</summary>
        Minute = 0,
        /// <summary>时K：固定 1 游戏小时（<c>GenDate.TicksPerHour</c> = 2500 tick）。</summary>
        Hour = 1,
        /// <summary>日K：固定 1 游戏日（<c>GenDate.TicksPerDay</c> = 60000 tick）。</summary>
        Day = 2
    }

    /// <summary>一笔已平仓交易的记录，用于"交易历史"页面。</summary>
    public class FxTradeRecord : IExposable
    {
        public string currencyName = "?";
        public string pairLabel = "?";
        public bool isLong = true;
        public int units;
        public float entryRate;
        public float exitRate;
        public float margin;
        /// <summary>本笔交易的实现盈亏（已扣平仓手续费前）。</summary>
        public float pnl;
        /// <summary>开仓 + 平仓手续费合计。</summary>
        public float totalFee;
        /// <summary>本笔交易承担的点差成本（开仓时每单位点差 × 数量）。</summary>
        public float spreadCost;
        /// <summary>
        /// 平仓时该货币的往返点差率。
        ///
        /// 存下来是为了能在读档时<b>精确重算</b> spreadCost：
        /// 交易记录里只有 entryRate/exitRate，没有当时的波动率，
        /// 光靠价格反推点差会有误差。有了这个字段就能用
        /// <c>units × mid × 点差率</c> 精确复原。
        /// 0 表示旧存档（该字段尚不存在），读档时会按"无点差"处理。
        /// </summary>
        public float spreadRateAtClose;
        /// <summary>平仓时的游戏 tick，用于排序与显示。</summary>
        public int closedTick;
        /// <summary>本次是爆仓强平还是手动平仓。</summary>
        public bool wasLiquidation;
        /// <summary>
        /// 是否"穿仓"（强平时净值已经为负，亏损超过保证金，账户被推成负余额）。
        /// 只有行情跳空才可能造成。
        /// </summary>
        public bool wasBlowUp;

        /// <summary>
        /// 这是一条"费用凭证"而不是一笔交易。
        ///
        /// 作者 2026-10-25：托管费/隔夜利息/贷款利息每天都在扣，
        /// 但一天没交易就查不到任何记录，等于没有凭证。
        /// 所以把这些扣款也写进交易历史，只是不参与胜率/交易笔数统计。
        /// </summary>
        public bool isFee;
        /// <summary>费用名（"托管费" / "隔夜利息" / "贷款利息"）。</summary>
        public string feeLabel = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref isFee, "isFee", false);
            Scribe_Values.Look(ref feeLabel, "feeLabel");
            Scribe_Values.Look(ref currencyName, "currencyName");
            Scribe_Values.Look(ref pairLabel, "pairLabel");
            Scribe_Values.Look(ref isLong, "isLong", true);
            Scribe_Values.Look(ref units, "units");
            Scribe_Values.Look(ref entryRate, "entryRate");
            Scribe_Values.Look(ref exitRate, "exitRate");
            Scribe_Values.Look(ref margin, "margin");
            Scribe_Values.Look(ref pnl, "pnl");
            Scribe_Values.Look(ref totalFee, "totalFee");
            Scribe_Values.Look(ref spreadCost, "spreadCost", 0f);
            Scribe_Values.Look(ref spreadRateAtClose, "spreadRateAtClose", 0f);
            Scribe_Values.Look(ref closedTick, "closedTick");
            Scribe_Values.Look(ref wasLiquidation, "wasLiquidation", false);
            Scribe_Values.Look(ref wasBlowUp, "wasBlowUp", false);
        }
    }

    /// <summary>殖民地的 FX 保证金账户。</summary>
    /// <summary>延迟事件的类型（0.3.0 调度器）。</summary>
    public enum FxPendingKind
    {
        None = 0,
        /// <summary>E1-A：商队 2 天后到达。</summary>
        CaravanArrive = 1,
        /// <summary>E6：报复袭击，每 2 天一次。</summary>
        RetaliationRaid = 2,
        /// <summary>E7：收债期限（3 天）。</summary>
        DebtDeadline = 3,
        /// <summary>E1-B：3 天后掷骰决定是否发起袭击。</summary>
        RaidChance = 4,
        /// <summary>
        /// 轨道贸易船到达。
        /// 帝国、轨道贸易联盟这类派系没有可派的陆路商队（CanFireNow 恒 false），
        /// 它们走的是 orbitalTraderKinds —— 用贸易船代替商队。
        /// </summary>
        OrbitalArrive = 5,
        /// <summary>
        /// 帝国征收队到达（Royalty）。
        ///
        /// 帝国两条正常贸易路都被皇家许可卡死（TradeCaravan / TradeOrbital），
        /// 但 CaravanArrivalTributeCollector **重写了 TraderKindCommonality，
        /// 绕过了许可判定** —— 所以帝国唯一能无条件派来的是"征收队"。
        /// 它不卖货，只收礼（用白银换好感）。作者 2026-10-25 拍板走这条。
        /// </summary>
        TributeArrive = 6,
        /// <summary>E6 最后一次危机：延迟 2 小时再宣布派系名存实亡。</summary>
        CrisisDefeat = 7,
    }

    /// <summary>
    /// 一条待处理的延迟事件。
    ///
    /// 为什么需要它：0.3.0 的事件有大量"几天后发生"的需求
    /// （商队 2 天后到、报复每 2 天一次、收债 3 天期限）。
    /// 没有这个队列就只能靠一堆各自的倒计时字段，加一个事件改一次结构。
    /// </summary>
    public class FxPendingEvent : IExposable
    {
        public FxPendingKind kind;
        public int factionLoadID = -1;
        public int currencyFaction = -1;
        public int dueTick;
        public int payload;

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind", FxPendingKind.None);
            Scribe_Values.Look(ref factionLoadID, "factionLoadID", -1);
            Scribe_Values.Look(ref currencyFaction, "currencyFaction", -1);
            Scribe_Values.Look(ref dueTick, "dueTick", 0);
            Scribe_Values.Look(ref payload, "payload", 0);
        }
    }

    /// <summary>E6：针对某个派系的报复循环。</summary>
    public class FxRetaliation : IExposable
    {
        public int factionLoadID = -1;
        public int currencyFaction = -1;
        public int raidCount;
        public int crisisCount;
        public int nextRaidTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref factionLoadID, "factionLoadID", -1);
            Scribe_Values.Look(ref currencyFaction, "currencyFaction", -1);
            Scribe_Values.Look(ref raidCount, "raidCount", 0);
            Scribe_Values.Look(ref crisisCount, "crisisCount", 0);
            Scribe_Values.Look(ref nextRaidTick, "nextRaidTick", 0);
        }
    }

    /// <summary>E2-B：被"经济崩溃"削弱的据点。窗口期内去打，守军少一半。</summary>
    public class FxWeakenedSettlement : IExposable
    {
        /// <summary>用 <c>WorldObject.ID</c> 定位据点 —— 它稳定且永不复用。</summary>
        public int settlementID = -1;
        public int factionLoadID = -1;
        public int untilTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref settlementID, "settlementID", -1);
            Scribe_Values.Look(ref factionLoadID, "factionLoadID", -1);
            Scribe_Values.Look(ref untilTick, "untilTick", 0);
        }
    }

    /// <summary>
    /// 一笔贷款（0.3.0 借贷体系）。
    ///
    /// ── 单利，不是复利 ──
    /// `principal` 是**永恒的计息基数**，每季产生的利息进 `unpaidInterest`，
    /// **绝不滚进本金**。所以玩家能提前算清"借 20000、30%、一季 6000"。
    /// </summary>
    public class FxLoan : IExposable
    {
        public int lender;
        public float principal;
        public float unpaidInterest;
        public int startTick;
        /// <summary>下一次计息的 tick（每日一次）。</summary>
        public int nextInterestTick;
        /// <summary>期限（游戏日）。0 = 无期限。</summary>
        public int termDays;
        /// <summary>连续多少天连利息都付不出。>= 3 即违约。</summary>
        public int unpaidDays;

        public float Owed => principal + unpaidInterest;

        public void ExposeData()
        {
            Scribe_Values.Look(ref lender, "lender", 0);
            Scribe_Values.Look(ref principal, "principal", 0f);
            Scribe_Values.Look(ref unpaidInterest, "unpaidInterest", 0f);
            Scribe_Values.Look(ref startTick, "startTick", 0);
            Scribe_Values.Look(ref nextInterestTick, "nextInterestTick", 0);
            Scribe_Values.Look(ref termDays, "termDays", 0);
            Scribe_Values.Look(ref unpaidDays, "unpaidDays", 0);
            // 兼容 preview1 的旧字段（当时按季计息、按季统计未付）
            int legacyQuarters = 0;
            Scribe_Values.Look(ref legacyQuarters, "quarters", 0);
            int legacyUnpaidQuarters = 0;
            Scribe_Values.Look(ref legacyUnpaidQuarters, "unpaidQuarters", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && unpaidDays == 0)
            {
                unpaidDays = legacyUnpaidQuarters;
            }
            // 兼容 preview1 的旧字段（当时期限按"季"存）
            int legacyTermQuarters = 0;
            Scribe_Values.Look(ref legacyTermQuarters, "termQuarters", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && termDays == 0 && legacyTermQuarters > 0)
            {
                termDays = legacyTermQuarters * 15;
            }
        }
    }

    public class FxAccount : IExposable
    {
        public float balance;
        public List<FxPosition> positions = new List<FxPosition>();
        public float lastDayPnl;
        public int lastDayPositions;
        /// <summary>
        /// 自上次结算以来发生过多少笔交易动作（开仓 + 平仓）。
        ///
        /// 用途：决定结算信件是否值得发。早先这里靠 <c>lastMoodStage != 2</c> 判断
        /// "今天有没有事发生"，但心情改成按平仓记录之后，结算不再写 lastMoodStage，
        /// 那个判据会永久失效 —— 结果是平淡的一天连信都不发，玩家以为结算坏了。
        /// 现在直接数动作，语义更直白。
        /// </summary>
        public int activityToday;

        // ── 0.3.0 新增（定名后不可改）──

        /// <summary>余额转负的时刻（0 = 当前不为负）。E7 收债的触发依据。</summary>
        public int negativeSinceTick;
        /// <summary>账户冻结到期（E7 报复：1 游戏年）。</summary>
        public int frozenUntilTick;
        /// <summary>本周期已扣的隔夜利息，结算信里单列一行。</summary>
        public float carrySinceSettle;
        /// <summary>本周期已扣的贷款利息，结算信里单列一行。</summary>
        public float loanInterestSinceSettle;
        /// <summary>本周期已扣的托管费，结算信里单列一行。</summary>
        public float custodySinceSettle;
        /// <summary>贷款记录（四家借贷方）。</summary>
        public List<FxLoan> loans = new List<FxLoan>();

        /// <summary>
        /// 上一个结算时点的账户权益（余额 + 浮动盈亏）。
        ///
        /// 用来算"当日盈亏"：<b>当日盈亏 = 本次权益 − 上次权益</b>。
        /// 这样一次就涵盖了「白天已平仓的已实现盈亏」与「此刻还挂着的浮动盈亏」，
        /// 不需要分别累加两个口径 —— 分别累加容易漂移，而权益差是天然自洽的。
        /// </summary>
        public float equityAtLastSettle;

        /// <summary>
        /// <see cref="equityAtLastSettle"/> 是否已经有过基线。
        ///
        /// ── 为什么不能拿 0 当"没有基线"的标记（2026-10-18 修）──
        /// 早先的写法是 `if (equityAtLastSettle == 0f)` 就认为没有基线、
        /// 把它重设成当前权益。但 <b>0 是一个合法的权益值</b>：
        /// 玩家把余额全部提取、或爆仓亏光之后，结算时的权益正好是 0，
        /// 于是基线被存成 0；到了第二天，无论赚了多少都会走进"没有基线"分支，
        /// 基线被刷成当前值 → 当日盈亏恒为 0。
        ///
        /// 实测：已实现 +1212 的一天，结算信报"今日盈亏 +0"。
        /// 因为前一次满额提取把基线留成了 0。
        ///
        /// 所以改用独立的布尔标记：数值可以是任何值（含 0），
        /// "有没有基线"由这个字段单独回答。
        /// </summary>
        public bool settleBaselineSet;

        /// <summary>已平仓交易记录，最新的在末尾。用于"交易历史"页面。</summary>
        public List<FxTradeRecord> history = new List<FxTradeRecord>();
        /// <summary>历史记录上限，避免长存档无限增长。</summary>
        public const int MaxHistory = 200;

        /// <summary>
        /// 记一条费用凭证。<paramref name="amount"/> 传正数，内部记成负的盈亏。
        /// </summary>
        public void RecordFee(string label, float amount)
        {
            if (amount < 0.01f) return;
            RecordTrade(new FxTradeRecord
            {
                isFee = true,
                feeLabel = label,
                currencyName = label,
                pairLabel = label,
                units = 0,
                pnl = -amount,
                closedTick = GenTicks.TicksGame
            });
        }

        public void RecordTrade(FxTradeRecord rec)
        {
            if (rec == null) return;
            activityToday++;
            history.Add(rec);
            while (history.Count > MaxHistory) history.RemoveAt(0);
        }

        /// <summary>
        /// 活跃中的平仓心情档，最多 <see cref="FxMoodSystem.MaxActive"/> 条。
        /// 由 <see cref="RecordCloseMood"/> 维护，读的是 ThoughtWorker 那边。
        /// </summary>
        public List<FxMoodEntry> moodEntries = new List<FxMoodEntry>();

        /// <summary>
        /// 记一笔平仓事件的心情档。
        ///
        /// 规则（与设计一致）：
        ///   · 该档已在列表里 → 叠一层（上限 <see cref="FxMoodSystem.MaxStack"/> 层）
        ///   · 是新档         → 追加一条
        ///   · 超过 MaxActive 条 → 挤掉<b>最早</b>的那条（先进先出），
        ///     保证最新发生的事一定看得见
        ///
        /// 金额为 0 的平仓不记档（<see cref="FxMoodSystem.TierForClose"/> 返回 -1）。
        /// </summary>
        public void RecordCloseMood(float pnl)
        {
            RecordMoodTier(FxMoodSystem.TierForClose(pnl));
        }

        /// <summary>
        /// 会被本档挤掉的对立档。
        ///
        /// 穿仓与爆仓是同一件事的两个严重程度（穿仓 = 保证金亏光之外还倒欠），
        /// 同一账户不可能同时"爆仓"又"穿仓"。若两条都挂，一次事故吃两份心情
        /// （-20 与 -28 相加 = -48），那不是更真实，只是更狠。
        ///
        /// ── 必须是<b>双向</b>（2026-10-21 修 R07）──
        /// 原来只写了"穿仓 → 顶掉爆仓"，反过来"爆仓 → 顶掉穿仓"没有实现。
        /// 实测：穿仓先记、爆仓后记时，两档会同时留在列表里，合计 -48。
        /// 可触发于"穿仓后补款、在心情有效期内再次爆仓"，
        /// 以及同一次强平检查连续处理多笔仓位、余额先负后正的情形。
        /// </summary>
        private static int SupersededBy(int tier)
        {
            if (tier == FxMoodSystem.TierBlowUp) return FxMoodSystem.TierMarginCall;
            if (tier == FxMoodSystem.TierMarginCall) return FxMoodSystem.TierBlowUp;
            return -1;
        }

        /// <summary>
        /// 按<b>档位</b>记一笔。爆仓走这里 —— 它的盈亏金额可能落进任意亏损档，
        /// 但玩家该看到的是"爆仓"，不是"亏了若干"。
        ///
        /// （这段文档原来悬在 <see cref="SupersededBy"/> 上方，成了孤儿注释：
        ///  两段 &lt;summary&gt; 连着写、中间没有方法体，于是 RecordMoodTier
        ///  一直没有文档，而 SupersededBy 头上顶着两段。2026-10-20 归位。）
        /// </summary>
        public void RecordMoodTier(int tier)
        {
            if (tier < 0) return;

            if (moodEntries == null) moodEntries = new List<FxMoodEntry>();

            int now = GenTicks.TicksGame;
            // 顺手清掉已过期的，免得死条目占着 MaxActive 的名额
            FxMoodSystem.PruneExpired(this, now);

            int drop = SupersededBy(tier);
            if (drop >= 0) moodEntries.RemoveAll(x => x != null && x.tier == drop);

            for (int i = 0; i < moodEntries.Count; i++)
            {
                FxMoodEntry e = moodEntries[i];
                if (e == null) continue;
                if (e.tier != tier) continue;

                // 同一档：叠层。已在列表里的位置不变，
                // 于是"最早"的判定仍然看它第一次入列的时间 —— 这正是先进先出要的。
                // 上限按档取 —— 穿仓只允许 1 层（见 MaxStackFor 的说明）
                if (e.count < FxMoodSystem.MaxStackFor(tier)) e.count++;
                e.sinceTick = now;      // 刷新计时：反复小亏不该一闪而过
                return;
            }

            moodEntries.Add(new FxMoodEntry(tier, 1, now));
            while (moodEntries.Count > FxMoodSystem.MaxActive) moodEntries.RemoveAt(0);
        }

        public float MarginUsed
        {
            get
            {
                float sum = 0f;
                for (int i = 0; i < positions.Count; i++) sum += positions[i].margin;
                return sum;
            }
        }

        public float FloatingPnl
        {
            get
            {
                float sum = 0f;
                for (int i = 0; i < positions.Count; i++) sum += positions[i].Pnl;
                return sum;
            }
        }

        /// <summary>账户权益：可用余额 + 占用保证金 + 浮动盈亏。</summary>
        public float Equity => balance + MarginUsed + FloatingPnl;

        /// <summary>
        /// 自上次结算以来的<b>净外部资金流</b>：存入记正、提取记负。
        ///
        /// ── 为什么必须有这个字段 ──
        /// 每日盈亏是用"权益差"算的（见 ReportDaily）。权益包含 balance，
        /// 而存入 / 提取会直接增删 balance —— 那是<b>玩家自己搬钱</b>，
        /// 不是交易赚亏。若不扣除，就会：
        ///     盈利平仓后把余额全部提取 → 权益掉到 0
        ///     → 结算报出一笔等于"提取额"的假亏损
        /// （实测：真实 +56，报出 −1000）
        /// 反过来，存入会被报成等额的假盈利。
        ///
        /// 这正是基金会计里的老规矩：<b>净值变化 = 投资收益 + 外部现金流</b>，
        /// 所以 投资收益 = 净值变化 − 外部现金流。
        ///
        /// 注意：追加保证金不走这里 —— 它是 balance 减、margin 加，
        /// 权益不变，本来就不影响盈亏。爆仓后补银填平则走"存入"，记入现金流，
        /// 因此不会把那笔亏损抵掉。
        /// </summary>
        public float cashFlowSinceSettle;

        /// <summary>
        /// 自上次结算以来的手续费累计（开仓 + 平仓 + 强平，退款会回冲）。
        ///
        /// 用途只有一个：让结算信能<b>摊开口径</b>。
        ///
        /// ── 为什么需要它 ──
        /// 结算信报的是"净额"（权益变化，已扣手续费），而交易历史里那列"盈亏"
        /// 是<b>毛利</b>（不含手续费）。玩家看到历史写着 +830、+42，
        /// 结算信却写 −11，第一反应必然是"结算算错了"——作者本人就误判过三次。
        ///
        /// 实测：毛利 +872、手续费 882.88，净额 −10.88 → 信里显示 −11。
        /// 数字完全正确，但只写一个 −11 无法自证。
        ///
        /// 有了这个字段，信里可以补一行"手续费 883，扣除前 +872"，
        /// 玩家自己就能对上账，不需要去翻手续费列。
        /// </summary>
        public float feesSinceSettle;

        public void ExposeData()
        {
            Scribe_Values.Look(ref balance, "balance", 0f);
            Scribe_Collections.Look(ref positions, "positions", LookMode.Deep);
            Scribe_Values.Look(ref equityAtLastSettle, "equityAtLastSettle", 0f);
            Scribe_Values.Look(ref cashFlowSinceSettle, "cashFlowSinceSettle", 0f);
            Scribe_Values.Look(ref feesSinceSettle, "feesSinceSettle", 0f);
            // 旧存档没有这个字段：用"基线是不是 0"推断。
            // 非 0 → 说明存过基线，视为已建立；恰好 0 → 按"无基线"处理，
            // 第一晚结算会记为 0 并把基线建起来（一次性，之后正常）。
            Scribe_Values.Look(ref settleBaselineSet, "settleBaselineSet",
                equityAtLastSettle != 0f);
            Scribe_Collections.Look(ref moodEntries, "moodEntries", LookMode.Deep);
            Scribe_Values.Look(ref lastDayPnl, "lastDayPnl", 0f);
            Scribe_Values.Look(ref lastDayPositions, "lastDayPositions", 0);
            Scribe_Values.Look(ref activityToday, "activityToday", 0);
            Scribe_Collections.Look(ref history, "history", LookMode.Deep);
            Scribe_Values.Look(ref negativeSinceTick, "negativeSinceTick", 0);
            Scribe_Values.Look(ref frozenUntilTick, "frozenUntilTick", 0);
            Scribe_Values.Look(ref carrySinceSettle, "carrySinceSettle", 0f);
            Scribe_Values.Look(ref loanInterestSinceSettle, "loanInterestSinceSettle", 0f);
            Scribe_Values.Look(ref custodySinceSettle, "custodySinceSettle", 0f);
            Scribe_Collections.Look(ref loans, "loans", LookMode.Deep);
            if (loans == null) loans = new List<FxLoan>();
            if (history == null) history = new List<FxTradeRecord>();
            if (positions == null) positions = new List<FxPosition>();

            // ── 修复旧存档里记录错误的盈亏 ──
            // 早期版本往 FxTradeRecord.pnl 里写的其实是:
            //   手动平仓: equity - margin （多算了追加保证金）
            //   强平:     equity          （多算了整笔保证金）
            // 两者都不等于真实盈亏，导致历史/报告里出现"做空涨了还盈利""爆仓却显示大赚"。
            //
            // entryRate / exitRate / units / isLong 都是正确持久化的，
            // 所以真实盈亏可以精确重算，而且是幂等的 —— 修过的存档再读也不会被改坏。
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                for (int i = 0; i < history.Count; i++)
                {
                    FxTradeRecord t = history[i];
                    if (t == null) continue;

                    // (1) 盈亏：从方向价重算（旧版记录里存的是错误公式的结果）
                    t.pnl = t.isLong
                        ? t.units * (t.exitRate - t.entryRate)
                        : t.units * (t.entryRate - t.exitRate);

                    // (2) 点差成本：**只清理旧记录，不再重算新记录**（2026-10-21 修 R06）
                    //
                    // 旧版公式对做空恒为 0、对做多会混进价格变动，
                    // 所以凡是没存下点差率的记录一律按"无点差"处理，不沿用错值。
                    //
                    // 而带 spreadRateAtClose 的新记录，写入时存的就是**开仓时真正被扣掉的那笔**
                    // （|units| × spreadAtOpen）。原来这里对所有记录无条件重算，
                    // 且重算用的是**平仓中间价** —— 价格一动，数字就和存档里的不一样了：
                    // 实测 1000 单位、开 100 平 110、点差 0.00112 时，
                    // 写入 112，重算得 123.2。
                    // 结果是同一笔交易的历史成本在重启前后不一致。现在新记录读档原样保留。
                    if (t.spreadRateAtClose <= 0f) t.spreadCost = 0f;
                }
            }
        }
    }

    /// <summary>
    /// 汇率市场。在 log 空间做"有锚随机游走"：既随机波动，又会被锚价轻微拉回，
    /// 避免长时间运行后汇率漂移到荒谬的数值。
    /// </summary>
    public class FxMarket : IExposable
    {
        /// <summary>
        /// 每日对数波动率（基准档）。公开是因为杠杆安全上限需要它 ——
        /// 强平线距离必须扛得住一天的正常波动，而这个判断要用到日波动。
        /// </summary>
        public const float DailyVol = 0.014f;
        private const float MinRate = 1f;
        private const float MaxRate = 100000f;

        public Dictionary<int, FxCurrency> currencies = new Dictionary<int, FxCurrency>();
        private Dictionary<int, float> anchors = new Dictionary<int, float>();
        private List<int> keyBuffer = new List<int>();

        /// <summary>
        /// 1x 速度下每现实秒的 tick 数。游戏固定 60 tick/现实秒，
        /// 所以把 tick 除以它可以换算成"现实秒"。
        /// </summary>
        public const int TicksPerRealSecond = 60;

        /// <summary>
        /// 取模组设置；设置尚未初始化时回退到默认值，避免空引用。
        /// </summary>
        private static RimForexSettings Cfg
        {
            get
            {
                RimForexSettings s = RimForexMod.Settings;
                if (s == null) s = new RimForexSettings();
                return s;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  多尺度均值回归堆栈（替代单 OU + 正弦锚）
        //
        //  ── 为什么需要它 ──
        //  原实现只有一个时间尺度（θ = 0.04，相关时间 25 步 ≈ 75 现实秒）。
        //  这意味着噪声在不到 2 根 K 线内就衰减掉 60%，窗口里只剩"底色"，
        //  没有任何可持续的段落 —— 图上就是一条尺子。
        //
        //  ── 唯一的硬不变式 ──
        //      Σ_k  2σ_k²/(2−θ_k)  =  σ_step²  =  DailyVol² / StepsPerDay
        //
        //  把预算<按尺度重新分配>，总量一个字节都不多。所以：
        //    · 日波动率仍恒为 DailyVol（求和恒等）
        //    · 点差 / churn / 杠杆上限等全部经济验收的依据不变
        //  这与之前四种"随机趋势项"方案的根本区别：那些是【加法】，
        //  在预算之外注入方差；本方案是【预算内重排】。
        //
        //  ── σ 倍率怎么来的 ──
        //  单尺度 OU（x ← a·x + ε，a = exp(−1/τ)）的步位移方差是
        //      σ²·(1 + (1−a)/(1+a)) = σ²·2/(1+a)
        //  令 K(τ) = (1−a)/(1+a)，要让各级贡献相等就给 σ_k ∝ 1/√(K(τ_k)/K(τ_min))，
        //  最后整体归一化到 σ_step。实测 Σ 2σ_k²/(2−θ_k) = 1.0006 σ_step²。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 各级尺度的<b>基准</b>时间常数，单位游戏日。
        ///
        /// 从"2 小时"到"9 日"跨约 100 倍，覆盖：
        ///   极快 → 点差 / churn 依赖的噪声底
        ///   快   → 盯盘时看到的分钟级往复
        ///   中   → <b>可交易的波段</b>（这是原来最缺的一档）
        ///   慢   → 趋势
        ///   极慢 → 大级别方向（取代原正弦锚）
        ///
        /// 最后一档是"趋势"那一级，玩家在设置里的两个滑块就是缩放它：
        ///   慢周期行情强度 → 它的<b>权重</b>
        ///   慢周期长度     → 它的 τ
        /// 见 <see cref="AllocateOscBudget"/>。
        /// </summary>
        public static readonly double[] TauDays = { 1.0 / 12, 1.0 / 3, 1.0, 3.0, 9.0 };

        /// <summary>基准的"趋势级"τ（日）。滑块以它为中点做缩放。</summary>
        public const double BaseSlowTauDays = 9.0;
        /// <summary>基准的"趋势级"周期长度（日）。滑块默认值即它。</summary>
        public const float BaseTrendPeriodDays = 6f;

        /// <summary>
        /// 盘内路径（振荡子）的条数。每根 K 线轮换一条，
        /// 这样各货币、各 K 线的盘中形状不再完全相同。
        /// </summary>
        public const int OscBankSize = 8;

        /// <summary>盘内路径幅度，语义等同旧的 BridgeAmp。</summary>
        public const float OscAmp = 0.3f;

        /// <summary>
        /// 波动率聚集强度（0 = 关闭，退化为恒定波动率）。
        ///
        /// 每步抽 m = exp(gauss·ν − ν²/2)，于是 <b>E[m²] = 1</b> ——
        /// 单步方差的<b>期望</b>不变，只改它的<b>分布形状</b>。
        /// 所以它同样不动预算，只是让行情出现"几根大阴大阳 → 一片平静"的交替。
        ///
        /// 第 1 段先设为 0，确认多尺度本身没问题后再打开。
        /// </summary>
        public const double VolOfVol = 0.0;

        /// <summary>由 τ 算响应核 K(τ) = (1−a)/(1+a)，a = exp(−1/τ)。</summary>
        public static double OuKernel(double tauSteps)
        {
            double a = System.Math.Exp(-1.0 / tauSteps);
            return (1.0 - a) / (1.0 + a);
        }

        /// <summary>
        /// 把单步方差预算按"等方差"分配给各级尺度，返回各级 σ。
        /// 结果满足 Σ 2σ_k²/(2−θ_k) ≈ σ_step²（实测误差 0.06%）。
        /// </summary>
        public static double[] AllocateOscBudget(double sigmaStep, int stepsPerDay, out double[] tauOut)
        {
            int n = TauDays.Length;
            var tau = new double[n];
            for (int k = 0; k < n; k++) tau[k] = TauDays[k] * stepsPerDay;

            // ── 趋势级（最后一档）由玩家设置缩放 ──
            // 周期长度只改这一级的 τ；强度改它的权重。
            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg != null)
            {
                float p = cfg.trendPeriodDays > 0.01f ? cfg.trendPeriodDays : BaseTrendPeriodDays;
                tau[n - 1] = BaseSlowTauDays * (p / BaseTrendPeriodDays) * stepsPerDay;
            }
            tauOut = tau;

            double k0 = OuKernel(tau[0]);
            var w = new double[n];
            for (int k = 0; k < n; k++) w[k] = 1.0 / System.Math.Sqrt(OuKernel(tau[k]) / k0);

            // 把"趋势级"的权重按设置缩放（0 = 关掉趋势，退回纯短周期噪声）。
            // 之后统一重新归一化，所以 Σσ_k² 恒为 1 —— 预算仍然一个字节不差。
            // 这就是"重排尺度但不改总量"：滑块只挪动各方差之间的比例。
            double trendScale = cfg != null ? cfg.trendAmp / (double)BaseTrendAmp : 1.0;
            if (trendScale < 0.0) trendScale = 0.0;
            w[n - 1] *= System.Math.Sqrt(trendScale);

            double sumSq = 0.0;
            for (int k = 0; k < n; k++) sumSq += w[k] * w[k];

            var s = new double[n];
            double norm = sigmaStep / System.Math.Sqrt(sumSq);
            for (int k = 0; k < n; k++) s[k] = w[k] * norm;
            return s;
        }

        /// <summary>滑块默认值，对应"趋势级拿它原本应得的权重"。</summary>
        public const float BaseTrendAmp = 0.02f;

        // ══════════════════════════════════════════════════════════════════
        //  慢周期行情：从"正弦锚"改成"多尺度 OU 的最慢一级"
        //
        //  ── 曾经为什么需要它 ──
        //  原实现里锚价固定在开局价，`MeanReversion = 0.04` 让价格 17 步就回到锚，
        //  于是每个货币都只是在自己开局价附近做高频噪声，永远走不出一段趋势。
        //
        //  ── 试过四种"加趋势"的方案，前三种全部失败（记录在案，别重蹈）──
        //   1. 慢速 EMA 锚：θ/α 差 18 倍，锚每移动一点价格 17 步内就跟上并抹平，
        //      实测自相关 −0.0004，等于没做。
        //   2. 随机趋势项：均值回归把随机趋势吸收成噪声，
        //      稳态贡献 = σ_drift/(θ√2) ≈ 17.7×，实测步级 σ 放大 1.14~4.6 倍，
        //      而自相关仍是 −0.035（纯加噪声）。
        //   3. 锚做随机游走：稳定后"价格−锚"的 gap 方差被 θ 锁死，
        //      价格只是「锚 + 固定偏移」，自相关 −0.039。
        //   4. 确定性正弦锚：唯一成功的一个 —— 每步位移是确定的，
        //      <b>完全不给步级方差加东西</b>（实测步级 σ 放大仅 1.0006~1.0158）。
        //
        //  ── 现在为什么又换掉它 ──
        //  正弦是<b>严格周期性</b>的：每个货币的行情模式固定且精确重复，
        //  玩久了会看出"这个币每 6 天走一个来回"，像钟摆而不像行情。
        //  而且它只能提供单一尺度，窗口内要么太直、要么太碎。
        //
        //  多尺度 OU 堆栈解决了这两点：最慢那一级（τ ≈ 9 日）承担趋势，
        //  因为是多级叠加，合成波形在很长时间内不会重复；
        //  而且它<b>计入方差预算</b>（见 AllocateOscBudget），比正弦锚在账目上更干净。
        //
        //  ── 这两个滑块控制的就是那一级 ──
        //  慢周期行情强度：它的权重。设为 0 即关掉趋势，退回纯短周期噪声。
        //  慢周期长度：它的 τ。
        //  两者都只<b>重排</b>预算比例，Σ 步位移方差恒为 σ_step²（实测误差 < 0.14%）。
        // ══════════════════════════════════════════════════════════════════


        /// </summary>
        public static int TicksPerStep
        {
            get
            {
                int t = Mathf.RoundToInt(Cfg.secondsPerQuote * FxGameComponent.TicksPerRealSecond);
                // 下限一个 tick：防止任何意外把步长弄成 0 而触发除零
                return t < 1 ? 1 : t;
            }
        }

        /// <summary>一个游戏日的报价步数。随 TicksPerStep 变化，默认 333（60000 / 180）。</summary>
        public static int StepsPerDay
        {
            get
            {
                int s = FxGameComponent.TicksPerDay / TicksPerStep;
                return s < 1 ? 1 : s;
            }
        }

        /// <summary>
        /// 每根 K 线包含多少次报价。由模组设置驱动（3 ~ 10，默认 6）。
        /// 报价次数太少会让影线没有信息量（单根内开=高或低）。
        /// </summary>
        public static int QuotesPerCandle
        {
            get
            {
                int q = Cfg.quotesPerCandle;
                if (q < RimForexSettings.MinQuotesPerCandle) q = RimForexSettings.MinQuotesPerCandle;
                if (q > RimForexSettings.MaxQuotesPerCandle) q = RimForexSettings.MaxQuotesPerCandle;
                return q;
            }
        }

        /// <summary>每根 K 线覆盖的 tick 数。</summary>
        public static int TicksPerCandle => TicksPerStep * QuotesPerCandle;

        /// <summary>最多保留多少根 K 线。</summary>
        public const int MaxCandles = 100;

        /// <summary>
        /// 点差随波动率缩放的指数。
        ///
        /// 往返点差 = SpreadCoef × volatility^SpreadExponent + SpreadFloor
        ///
        /// ── 2026-10-16：整体下调点差（用户诉求：100x 下回本太贵）──
        ///
        /// 旧参数 `Coef=0.0003, Floor=0.003` 让点差落在 0.31%~0.34%，
        /// 而且被地板主导，几乎不随品种变化。问题出在：
        ///     回本需移动 / 强平距离 = 点差 / (1 − StopOutFraction)
        /// 这个比值<b>与杠杆无关</b>，所以 100x 下必须走完 63% 的强平距离才回本 ——
        /// 风险预算几乎全被交易成本吃掉，手感极差。
        ///
        /// 新参数 `Coef=0.0008, Floor=0.0007` 把点差压到 0.094%~0.170%：
        ///     档位     旧点差    新点差    100x 比值
        ///     Spacer   0.311%   0.094%    0.63 → 0.19
        ///     Standard 0.316%   0.118%    0.63 → 0.24
        ///     Raider   0.336%   0.170%    0.67 → 0.34
        /// 顺带解决了"点差几乎不随品种变化"的老毛病：地板从 89% 的占比降到
        /// 约 45%，点差重新对波动率有可见的响应。
        ///
        /// ── 为什么指数仍是 1.0（线性）──
        /// 试过凸缩放（1.5 / 1.8），收益很小。线性已经够用，
        /// 而且线性下"点差 / 单步波动"这个比值对波动率不敏感，
        /// 各档位的 churn 余量更均匀（见 verify_spread.py 验收 7）。
        ///
        /// ── 下限在哪 ──
        /// 点差同时被两个方向的约束夹击，而且随波动率<b>反向</b>变化：
        ///   防 churn（下限，∝ 波动率）：往返点差 &gt; 0.7979 × 单步波动
        ///   开 100x（上限，与波动率无关）：往返点差 ≤ MaxSpreadImpactOnMargin / 100 = 0.35%
        /// 两条线交叉在波动率约 1.28 处 —— 所以<b>波动率高于 1.28 的品种永远上不了 100x</b>，
        /// 这是数学事实，与点差怎么调无关（现行方案把 tier 上限压在 1.25 以内）。
        /// 另外 verify_spread.py 还有一条趋势判据：点差 ≤ 0.85 × 一整日期望波动（上限 0.95%）。
        /// 新参数距这两条下限都还有充分余量。
        /// </summary>
        public const float SpreadExponent = 1.0f;

        /// <summary>
        /// 点差缩放系数。往返点差 = 本值 × volatility + <see cref="SpreadFloor"/>。
        /// </summary>
        public const float SpreadCoef = 0.0008f;

        /// <summary>点差上限，防止极端波动倍率把点差推到荒谬的水平。</summary>
        public const float MaxSpread = 0.03f;

        /// <summary>
        /// 往返点差下限。2026-10-16 从 0.003 降到 0.0007。
        ///
        /// 它同时承担"防 churn"的职责：再低 churn 就会变成正期望。
        /// 但下限远不止于此 —— 真正的硬下限是 `0.7979 × 单步波动`
        /// （低波动档约 0.018%），以及趋势判据的 0.95%。0.0007 有充分余量。
        /// </summary>
        public const float SpreadFloor = 0.0007f;

        /// <summary>
        /// 该货币的实际点差率（往返）。
        ///
        /// 数值依据（Docs/verify_spread.py 的验收 1 / 7 可复现）：
        ///   churn 期望 = 0.7979 × 单步波动 − 往返点差，必须恒为负；
        ///   同时 trend（持 12 步）必须全档盈利。
        /// 现行参数下 churn 期望为 −0.013% ~ −0.181%，全档为负。
        ///
        /// 因为按<b>名义仓位</b>计收，杠杆越高，点差占保证金的比例越大
        /// （= 点差 × 杠杆），所以高杠杆短线天然被抑制，
        /// 并且构成 MaxSpreadImpactOnMargin 那条约束。
        /// </summary>
        public static float SpreadRateFor(FxCurrency c)
        {
            if (c == null) return SpreadFloor;
            float vol = c.volatility > 0.01f ? c.volatility : 1f;
            float rt = SpreadCoef * Pow(vol, SpreadExponent) + SpreadFloor;
            if (rt < SpreadFloor) rt = SpreadFloor;
            if (rt > MaxSpread) rt = MaxSpread;
            return rt;
        }

        /// <summary>按 id 取点差率；货币不存在时用基准值。</summary>
        public float SpreadRateFor(int factionLoadID)
        {
            return SpreadRateFor(Get(factionLoadID));
        }

        public static float AskOf(FxCurrency c)
        {
            if (c == null) return 0f;
            return c.rate * (1f + SpreadRateFor(c) * 0.5f);
        }

        public static float BidOf(FxCurrency c)
        {
            if (c == null) return 0f;
            return c.rate * (1f - SpreadRateFor(c) * 0.5f);
        }

        public float AskOf(int factionLoadID) => AskOf(Get(factionLoadID));
        public float BidOf(int factionLoadID) => BidOf(Get(factionLoadID));

        /// <summary>
        /// 开仓成交价：做多吃卖价（ask，更贵），做空吃买价（bid，更便宜）。
        /// </summary>
        public float EntryPriceOf(int factionLoadID, bool isLong)
        {
            return isLong ? AskOf(factionLoadID) : BidOf(factionLoadID);
        }

        /// <summary>
        /// 平仓成交价：与开仓反向。做多平仓是卖出（吃 bid），做空平仓是买回（吃 ask）。
        /// 由于 FxPosition.Pnl 用的是"方向价"口径，markRate 永远存这个值。
        /// </summary>
        public float ExitPriceOf(int factionLoadID, bool isLong)
        {
            return isLong ? BidOf(factionLoadID) : AskOf(factionLoadID);
        }

        public Dictionary<int, FxRateSeries> series = new Dictionary<int, FxRateSeries>();

        public FxRateSeries SeriesFor(int factionLoadID)
        {
            FxRateSeries s;
            if (!series.TryGetValue(factionLoadID, out s))
            {
                s = new FxRateSeries();
                series[factionLoadID] = s;
            }
            return s;
        }

        public void EnsureCurrency(Faction faction)
        {
            if (faction == null || faction.def == null) return;
            if (currencies.ContainsKey(faction.loadID)) return;

            var currency = FxCurrencyGenerator.Generate(faction);
            currencies[faction.loadID] = currency;
            anchors[faction.loadID] = currency.rate;
        }

        /// <summary>
        /// 彻底删除某个货币的全部状态（货币本体、锚点、K 线序列）。
        ///
        /// 只用于**孤儿清理**：该派系已经从世界上消失了，留着这个货币没有意义，
        /// 而且会一直占着行情表的一行。
        ///
        /// 调用方必须先确认"没有持仓引用它" —— 持仓里存的是 currencyFactionLoadID，
        /// 货币一旦被删，那个仓位就再也找不到自己的行情了。
        /// </summary>
        public bool RemoveCurrency(int factionLoadID)
        {
            anchors.Remove(factionLoadID);
            series.Remove(factionLoadID);
            return currencies.Remove(factionLoadID);
        }

        public FxCurrency Get(int factionLoadID)
        {
            FxCurrency c;
            return currencies.TryGetValue(factionLoadID, out c) ? c : null;
        }

        /// <summary>全部已生成的货币，供遍历（跳跃播报等）使用。</summary>
        public IEnumerable<FxCurrency> AllCurrencies => currencies.Values;

        /// <summary>按 loadID 升序返回所有货币，保证 UI 顺序稳定（不受字典哈希顺序影响）。</summary>
        public List<FxCurrency> AllSorted()
        {
            keyBuffer.Clear();
            keyBuffer.AddRange(currencies.Keys);
            keyBuffer.Sort();
            var list = new List<FxCurrency>();
            for (int i = 0; i < keyBuffer.Count; i++) list.Add(currencies[keyBuffer[i]]);
            return list;
        }

        /// <summary>
        /// 常规跳空的<b>每游戏日</b>概率。
        ///
        /// ── 为什么以"日"为单位，而不是"每步" ──
        /// 每步概率会随报价频率变化：同样的 0.00012/步，
        /// 在 3 秒一报价（333 步/日）下是 3.9%/日，
        /// 在 10 秒一报价（100 步/日）下就只剩 1.2%/日。
        /// 那样玩家一调"报价频率"就会连带改变风险，两个旋钮互相干扰。
        /// 以日为单位标定后，报价频率与跳空频率<b>互相独立</b>。
        ///
        /// 强度旋钮的映射（0 = 偏真实，1 = 偏赌狗）：
        ///     0.0 → 1.0%/日   （约 100 游戏日一次）
        ///     0.5 → 3.9%/日   （约 26 游戏日一次）← 默认
        ///     1.0 → 15%/日    （约 6.7 游戏日一次）
        /// 用平方插值，所以滑块前半段变化平缓、后半段急剧变凶。
        /// </summary>
        public static float GapChancePerDay
        {
            get
            {
                float t = Cfg.gapIntensity;          // 0 ~ 1
                float eased = t * t;                 // 平方：前半段温和
                return 0.01f + eased * 0.14f;        // 1% ~ 15%
            }
        }

        /// <summary>常规跳空的每步概率 = 日概率折算到当前步数。</summary>
        public static float GapChancePerStep
        {
            get
            {
                // 1-(1-p)^n = 日概率  =>  p = 1-(1-日概率)^(1/n)
                double perDay = GapChancePerDay;
                double step = 1.0 - System.Math.Pow(1.0 - perDay, 1.0 / StepsPerDay);
                return (float)step;
            }
        }

        /// <summary>常规跳空幅度范围（相对当步正常波动的倍数）。</summary>
        public const float GapMinMult = 3f;
        public const float GapMaxMult = 9f;

        /// <summary>
        /// 极端跳空的<b>每游戏日</b>概率 —— 只有这一档才可能造成穿仓（负余额）。
        ///
        /// 与常规跳空同样按日标定，随强度旋钮一起放大，但始终比常规跳空低两个数量级：
        ///     0.0 → 0.03%/日  （约 3300 游戏日一次）
        ///     0.5 → 0.10%/日  （约 1000 游戏日一次）← 默认
        ///     1.0 → 0.40%/日  （约 250 游戏日一次）
        /// 这样"爆仓常见、穿仓罕见"的结构不会因为拉高滑块而消失。
        /// </summary>
        public static float ExtremeGapChancePerDay
        {
            get
            {
                float t = Cfg.gapIntensity;
                return 0.0003f + t * t * 0.0037f;    // 0.03% ~ 0.40%
            }
        }

        /// <summary>极端跳空的每步概率。</summary>
        public static float ExtremeGapChancePerStep
        {
            get
            {
                double perDay = ExtremeGapChancePerDay;
                double step = 1.0 - System.Math.Pow(1.0 - perDay, 1.0 / StepsPerDay);
                return (float)step;
            }
        }

        /// <summary>极端跳空幅度范围（倍数），足以越过强平线并吃掉全部保证金。</summary>
        public const float ExtremeGapMinMult = 14f;
        public const float ExtremeGapMaxMult = 34f;

        /// <summary>
        /// 测试用强制跳空的σ倍数下限。
        /// 60σ 对最稳定的档位（vol 0.35）约等于 1.60% 的价格跌幅，
        /// 而 100x 多仓在 1% 跌幅时就已经穿仓 —— 留足余量。
        /// </summary>
        public const float ForcedGapMinMult = 60f;

        /// <summary>
        /// <b>步内子采样数</b> —— 每个报价步内部细分成多少个小步来推进价格。
        ///
        /// ── 为什么需要它（D1：影线被系统性低估）──
        /// 原本一个报价步只抽<b>一个</b>高斯数、只产生<b>一个</b>价格点，
        /// 而 K 线的 high/low 只在这个点上更新。一根 K 线只有 6 个采样点，
        /// 于是"影线"退化成"6 个离散价的极值"，蜡烛偏方块：
        ///   实测 影线/实体 ≈ 0.49（真实 FX 远高于此）
        /// 长上影、锤子线、十字星这类依赖"步内冲高回落"的形态<b>结构上不可能出现</b>。
        ///
        /// ── 为什么这样做是安全的（不破坏经济验收）──
        /// <see cref="AdvanceSubStep"/> 把 <b>shock 与均值回归项都按 1/SubSteps 等分</b>：
        ///     Σ(N 个小步的位移) = shock − MeanReversion × log(rate/anchor)
        /// 即<b>步边界上的价格与单步实现完全相等</b>（不是"同分布"，是逐点相等）。
        /// 所以 Docs/verify_spread.py 的整套 churn / 点差验收<b>原样继续成立</b>。
        ///
        /// 由此也确定了实现的形态：位移必须放在子步里算，跳空单独留在边界施加。
        /// 若反过来（位移留在边界、子步只插值），子步就只是"打点"而非真实路径，
        /// 影线不会变长。
        ///
        /// 取 16：影线/实体从 0.49 提到约 0.86，再往上收益递减。
        /// </summary>
        public const int SubSteps = 16;

        // ── 测试专用：下一次 TickStep 强制一次跳空 ──
        // 极端跳空的日概率约 0.1%，正常要玩约 1000 游戏日才碰上一次；
        // 常规跳空虽然常见，但要"恰好落在某个货币上并达到播报阈值"也得碰运气。
        // 这两个开关让设置页的测试按钮把事件提前引发。
        //
        // 0 = 无，1 = 常规跳空，2 = 极端跳空
        private const int ForcedGapNone = 0;
        private const int ForcedGapNormal = 1;
        private const int ForcedGapExtreme = 2;
        private int forcedGapKind = ForcedGapNone;

        /// <summary>安排下一次报价步发生<b>极端</b>跳空（对所有货币生效，必然向下）。</summary>
        public void ForceExtremeGap()
        {
            forcedGapKind = ForcedGapExtreme;
        }

        /// <summary>
        /// 安排下一次报价步发生<b>常规</b>跳空（对所有货币生效）。
        ///
        /// 幅度取常规区间 3σ~9σ 的<b>上半段</b>（8σ~9σ），原因有二：
        ///   · 保证能过播报阈值（现在按 6σ 判定，8σ 必然过线），
        ///     否则按钮按下去可能什么都不提示，看起来像"没生效"
        ///   · 8σ 仍然是"常规"量级，与极端跳空（14σ~34σ）在观感上分得开
        /// 方向交给下面那句统一的随机翻转（50% 向上/向下），
        /// 这样两个方向都能测到。
        /// </summary>
        public void ForceNormalGap()
        {
            forcedGapKind = ForcedGapNormal;
        }

        /// <summary>
        /// E4/E5 的新闻漂移：每步均摊。
        ///
        /// `newsDriftLog` 存的是**整段窗口的累计对数漂移**（例如 −1.5σ），
        /// 摊到 NewsDriftDays 天、每天 StepsPerDay 步上，逐步施加。
        /// 这样价格会走出一个缓坡，而不是跳空式的瞬时跳变。
        /// </summary>
        private static float NewsDriftStep(FxCurrency c)
        {
            if (c == null || c.newsDriftLog == 0f) return 0f;
            if (GenTicks.TicksGame >= c.newsDriftUntilTick)
            {
                c.newsDriftLog = 0f;
                return 0f;
            }
            return c.newsDriftLog / (FxGameComponent.NewsDriftDays * StepsPerDay);
        }

        public void TickStep(int stepStartTick)
        {
            keyBuffer.Clear();

            // 一次性事件：本步对所有货币生效，取走后就清掉，避免连续跳空
            int forcedKind = forcedGapKind;
            forcedGapKind = ForcedGapNone;
            keyBuffer.AddRange(currencies.Keys);
            for (int i = 0; i < keyBuffer.Count; i++)
            {
                int id = keyBuffer[i];
                var c = currencies[id];
                if (c == null) continue;

                float anchor;
                if (!anchors.TryGetValue(id, out anchor) || anchor <= 0f)
                {
                    anchor = c.rate;
                    anchors[id] = anchor;
                }

                // 正弦锚已由"极慢尺度 OU"（τ = 9 日）取代：
                // 它同样提供窗口级趋势，但<计入预算>、不需要相位常数，
                // 而且读档后从零态重建即自洽。anchor 现在只作兜底。

                // ── 跳空：低概率的大幅单步移动，<b>只在本步的最后一个子步施加</b> ──
                // 常规单步波动只有 sigma，几乎穿不透强平线；
                // 跳空则可直接越过，从而产生"爆仓"乃至"穿仓"（负余额）。
                //
                // 分两档：
                //   常规跳空 → 打穿强平线，通常仍是普通强平
                //   极端跳空 → 亏损超过全部保证金，余额被推成负数
                // 两档共用同一段代码，只是概率与幅度不同。
                //
                // 方向不偏：对数空间带均值回归，价格会被拉回锚点，
                // 所以长期不会单边漂移。跳空只负责制造尾部风险。
                float mult = 0f;
                if (forcedKind == ForcedGapExtreme)
                {
                    // 测试按钮安排的：<b>强制向下</b>且幅度足够大。
                    //
                    // 向下是刻意的 —— 测试流程是"百倍杠杆做多"，只有向下跳空才会穿仓。
                    //
                    // 幅度必须保证【最稳定的货币也能穿仓】：
                    //   100x 多仓亏损占保证金的比例 = 杠杆 × 价格跌幅
                    //   要 > 1（穿仓）就需要跌幅 > 1%。
                    //   实测最稳档（vol 0.35）在 34σ 时只跌 0.91% —— 亏 91% 保证金，
                    //   还差一点，测试会"看起来失败"。所以起点抬到 60σ（跌 1.60%）。
                    mult = -(ForcedGapMinMult + Rand.Value * ForcedGapMinMult);
                }
                else if (forcedKind == ForcedGapNormal)
                {
                    // 常规区间 3σ~9σ 的上半段（8σ~9σ）：
                    // 足以过播报阈值（6σ），又与极端跳空在观感上分得开。
                    // 方向不在这里定 —— 交给下面那句统一的随机翻转，
                    // 这样按一次就能随机测到向上或向下。
                    mult = (GapMaxMult - 1f) + Rand.Value * 1f;
                }
                else if (Rand.Chance(ExtremeGapChancePerStep))
                {
                    mult = ExtremeGapMinMult + Rand.Value * (ExtremeGapMaxMult - ExtremeGapMinMult);
                }
                else if (Rand.Chance(GapChancePerStep))
                {
                    mult = GapMinMult + Rand.Value * (GapMaxMult - GapMinMult);
                }
                if (mult > 0f)
                {
                    if (Rand.Chance(0.5f)) mult = -mult;
                }
                c.stepIsGap = mult != 0f;

                float sigma = DailyVol * c.volatility / Mathf.Sqrt(StepsPerDay);

                // ── 本步的"整步预算" ──
                // 位移由【多尺度 OU 堆栈】给出，子步只负责把它走完。
                //
                // 硬不变式：Σ_k 2σ_k²/(2−θ_k) = σ_step²  （见 AllocateOscBudget）
                // 所以点差 / churn / 日波动率等全部经济验收的依据不变。
                // 各级自带均值回归（衰减项），不再需要单独的 -MeanReversion·log(rate/anchor)。
                //
                // 跳空仍是预算外的尾部项，按 σ_step 标定，语义与原实现一致。
                c.EnsureOsc();
                c.RollVolMult();
                c.stepLogStart = Log(c.rate);
                // 多尺度位移是 double（要跨成千上万步累加，float 会漂移），
                // 但 stepLogTarget 是 float（要跟着存档 & 参与 UI），这里显式收敛精度。
                double oscDisp = c.StepOsc() * c.VolMult;
                c.stepLogTarget = c.stepLogStart
                    + (float)oscDisp
                    + mult * sigma
                    + NewsDriftStep(c);
                c.subStep = 0;
                c.stepBudgetValid = true;   // 见 stepLogStart 的说明（读档保护）
                c.AdvanceOscIndex();        // 每步换一条盘内路径

                // ── 先把步边界价本身记进 K 线 ──
                // 子步样本位于步的 1/N、2/N … N/N 处，也就是说第一个子步样本
                // 落在步的<b>中间</b>。若只靠子步采样，K 线的"开盘"就会取自
                // 步中而非步起点，整根蜡烛相对旧实现错位一个子步 ——
                // 实测这会让实体凭空胖 9%（amp=0 时依然如此，与桥无关）。
                // 补上这个边界点后，开盘恰好落在步起点，实体与原实现一致。
                AccumulateCandle(c, stepStartTick);

                // 本步开始时的价格。此刻 c.rate 还停在【上一步的终点】——
                // 子步由 WorldComponentTick 调 AdvanceSubSteps 逐步推进，不在这里跑。
                float rateBefore = c.rate;

                // ── 子步不在这里推进（2026-10-21 修）────────────────────────
                //
                // 这里原来写的是：
                //     for (int k = 0; k < SubSteps; k++) AdvanceSubStep(c);
                //     c.rate = Clamp(Exp(c.stepLogTarget), ...);
                //
                // 后果是 subStep 当场被推到 16，于是 AdvanceSubSteps 里的
                //     while (c.subStep < targetSubStep) { AdvanceSubStep(); AccumulateCandle(); }
                // 从此永远不成立 —— **每个子步的路径点一次都没有被采样过**，
                // K 线的影线只剩下步边界那几个点，桥的形状完全看不见。
                // （16 次子步运算照样跑了，只是结果直接被覆盖，纯属白算。）
                //
                // 现在这里只建预算（上面已完成），推进与采样交回 AdvanceSubSteps，
                // 那本来就是它的职责 —— 见该方法与 SubSteps 的说明。
                //
                // 端点依旧精确：桥在 k = SubSteps 处恒为 0，所以
                //     rate @ k=16 == Clamp(Exp(stepLogTarget))
                // 步边界分布、日波动、跳空幅度都不受影响。

                // 记下跳空的实际幅度（相对本步开始时的价格），供 UI 提示用。
                // 必须用**预算终点**算，不能等 c.rate —— 它要到子步走完才到终点。
                //
                // ⚠️ 判据是 mult != 0，不是 mult > 0（2026-10-21 修 R02）。
                // mult 在下面有 50% 概率被翻成负数（向下跳空），强制跳空测试按钮
                // 更是直接给负值 —— 原来的 `mult > 0f` 把所有向下跳空都写成了 0，
                // 于是"价格暴跌却没有任何提示"。
                c.lastGapPct = 0f;
                if (mult != 0f && rateBefore > 0.001f)
                {
                    float rateAtEnd = Clamp(Exp(c.stepLogTarget), MinRate, MaxRate);
                    c.lastGapPct = (rateAtEnd / rateBefore - 1f) * 100f;
                }
            }
        }

        /// <summary>
        /// 推进<b>一个子步</b>的价格。
        ///
        /// <b>它不采样</b> —— 采样由调用方 <see cref="AdvanceSubSteps"/> 在每次推进后
        /// 调 <c>AccumulateCandle</c> 完成。（注释曾写成"并把新价累加进 K 线"，
        /// 与实现不符，正是这个错觉掩盖了 2026-10-21 修掉的那个 bug：采样循环永远不执行。）
        ///
        /// ── 走错过的两条路，记下来免得重犯 ──
        /// ① 每个子步各抽一个 <c>σ/√N</c> 的独立冲击：
        ///    N 个累加方差确实是 σ²，但<b>均值回归项也被重复施加 N 次</b>，
        ///    且子步之间价格在变、<c>log(rate/anchor)</c> 随之改变，总量无法闭合。
        ///    实测步边界标准差从 0.317 塌到 0.079（−75%），等于把日波动率改小 4 倍。
        /// ② 整步位移 + 线性插值：
        ///    边界正确，但路径是直线，影线只从 0.49 提到 0.56，像"斜着的方块"。
        ///
        /// ── 现在采用的写法：整步预算 + 布朗桥 ──
        /// 步位移由 <see cref="TickStep"/> 一次算好（<c>stepLogStart → stepLogTarget</c>），
        /// 子步 = 线性插值 + σ × 预生成的桥。
        ///
        /// 桥的最后一个增量<b>恒为 0</b>（<c>(N−N)/N = 0</c>），所以
        ///     log(rate) @ k=N  ==  stepLogTarget
        /// <b>逐点相等，不是同分布</b> —— 步边界位移与原实现完全一致，
        /// 方差 / 跳空 / 均值回归全部原样保留，经济验收不受影响。
        /// </summary>
        private void AdvanceSubStep(FxCurrency c)
        {
            // ⚠️ 双保险：步预算未建立时绝不改价。
            // 此时 stepLogStart / stepLogTarget 都是默认值 0，而 Exp(0) = 1 = MinRate，
            // 一旦往下走价格就被打到下限。调用方（AdvanceSubSteps）也会跳过，
            // 但这里再挡一次 —— 这个 bug 的代价太大（读档即崩），值得两道防线。
            if (!c.stepBudgetValid) return;

            c.subStep++;

            float sigma = DailyVol * c.volatility / Mathf.Sqrt(StepsPerDay);
            float t = c.subStep / (float)SubSteps;

            // ── 跳空步：整步不动，最后一个子步一次到位 ──
            // 见 stepIsGap 的说明：插值会让强平在跳空"途中"触发，
            // 结果是普通爆仓而不是穿仓，极端跳空的测试价值就没了。
            if (c.stepIsGap)
            {
                float logJump = t >= 1f ? c.stepLogTarget : c.stepLogStart;
                c.rate = Clamp(Exp(logJump), MinRate, MaxRate);
                return;
            }

            float logMid = c.stepLogStart + (c.stepLogTarget - c.stepLogStart) * t;

            // 盘内形状从【按步轮换的振荡子池】取，而不是那条全局唯一的静态桥。
            // 静态桥的后果是：所有货币、所有 K 线的盘中路径完全相同 ——
            // 影线的纹理是假的，这是视觉"规整感"的一个真实来源。
            float[] osc = OscBank[c.OscIndex];
            float bridge = sigma * OscAmp * osc[c.subStep];

            c.rate = Clamp(Exp(logMid + bridge), MinRate, MaxRate);
        }


        /// <summary>
        /// 离散布朗桥在 N 等分点上的取值，只在启动时生成一次。
        ///
        /// ── 必须是"两端都为 0"的桥 ──
        /// 单端桥（只钉 B(N)=0）会让 B(1) 带上非零方差，
        /// 而 K 线的<b>开盘价</b>取自第一个子步 ——
        /// 于是每根蜡烛的开盘被随机抬高/压低，实体被系统性放大。
        /// 实测：12 组种子下实体比值 1.079，95% 置信区间 [1.061, 1.097]，明显不含 1。
        ///
        /// ── 正确构造 ──
        /// 先生成随机游走 <c>W</c>，再减去它到原点的线性插值：
        ///     B_k = W_k − (k/N)·W_N
        /// 这样 <c>B_0 = B_N = 0</c> 天然成立，中间自由摆动。
        ///
        /// 最后按最大偏离归一化到 1，于是 <see cref="BridgeAmp"/> 就是
        /// "桥幅度 = 多少个单步 σ"，参数含义明确、可调。
        /// </summary>
        private static float[][] oscBank;

        /// <summary>
        /// 盘内路径池：<see cref="OscBankSize"/> 条互不相同的振荡子，按步轮换使用。
        ///
        /// 每条都是"两端钉零"的布朗桥，构造方式与原静态桥相同
        /// （先随机游走，再减去 (k/N)·W_N 投影，最后归一化到 maxAbs = 1）。
        ///
        /// <b>两端钉零是硬要求</b>：它保证 <c>log(rate) @ k=N == stepLogTarget</c>
        /// <b>逐点相等</b>，步边界分布不被盘内路径影响。
        /// 曾经用单端桥，实测让 K 线实体凭空胖 7.9%（95% CI [1.061, 1.097]）——
        /// 因为开盘价取自第一个子步，而单端桥在 k=1 处不为零。
        /// </summary>
        private static float[][] OscBank
        {
            get
            {
                if (oscBank != null) return oscBank;

                oscBank = new float[OscBankSize][];
                for (int b = 0; b < OscBankSize; b++)
                {
                    // 随机游走；w[0] = 0 是起点，从 1 开始累加
                    var w = new float[SubSteps + 1];
                    for (int k = 1; k <= SubSteps; k++)
                    {
                        w[k] = w[k - 1] + Rand.Gaussian(0f, 1f);
                    }

                    var bn = new float[SubSteps + 1];
                    float maxAbs = 0f;
                    for (int k = 1; k < SubSteps; k++)
                    {
                        float v = w[k] - (k / (float)SubSteps) * w[SubSteps];   // 两端钉零
                        bn[k] = v;
                        float a = v < 0f ? -v : v;
                        if (a > maxAbs) maxAbs = a;
                    }
                    if (maxAbs > 1e-6f)
                    {
                        for (int k = 1; k < SubSteps; k++) bn[k] /= maxAbs;
                    }
                    bn[0] = 0f;
                    bn[SubSteps] = 0f;

                    oscBank[b] = bn;
                }
                return oscBank;
            }
        }


        /// <summary>
        /// 把价格推进到"本步内已完成 <paramref name="targetSubStep"/> 个子步"的位置，
        /// 并<b>在每个子步后采样 K 线</b>。
        ///
        /// 用<b>目标进度</b>而不是"再走 k 个子步"：低帧率下一帧可能欠好几个子步，
        /// 按目标补齐才不会漏；同时天然幂等 —— 同一帧重复调用不会重复推进。
        ///
        /// ── 采样必须在循环内 ──
        /// 若只在函数返回后采样一次，进入 K 线 high/low 的仍只是"本帧最后一个子步"，
        /// 影线长度不会有任何改善 —— 子步就白做了。
        ///
        /// ── 子步的"虚拟 tick" ──
        /// 一次调用可能推进多个子步，但它们都发生在同一个游戏 tick 上。
        /// 若把当前 tick 直接传给 K 线累积，十几根子步会挤在同一 tick，
        /// 封根判定（nowTick − periodStartTick ≥ TicksPerCandle）就会失真。
        /// 所以按子步序号折算一个虚拟 tick：同一帧内的子步单调递增，
        /// 步边界恰好落在真实 tick 上。
        /// </summary>
        public void AdvanceSubSteps(int targetSubStep, int stepStartTick)
        {
            if (targetSubStep <= 0) return;
            if (targetSubStep > SubSteps) targetSubStep = SubSteps;

            int subTicks = FxMarket.TicksPerStep / SubSteps;
            if (subTicks < 1) subTicks = 1;

            keyBuffer.Clear();
            keyBuffer.AddRange(currencies.Keys);
            for (int i = 0; i < keyBuffer.Count; i++)
            {
                int id = keyBuffer[i];
                var c = currencies[id];
                if (c == null) continue;

                // ⚠️ 读档保护：本步的整步预算还没建立时，绝不能推进子步。
                // 此时 stepLogStart / stepLogTarget 仍是默认值 0，而 Exp(0) = 1 = MinRate ——
                // 一旦推进，价格会瞬间被打到下限，再靠均值回归慢慢爬回锚点。
                // 直接跳过；下一个 TickStep 会建立预算（顶多晚一个报价步，价格保持不动，
                // 这比"先崩再爬"正确得多）。
                if (!c.stepBudgetValid) continue;

                while (c.subStep < targetSubStep)
                {
                    AdvanceSubStep(c);

                    // 每推进一个子步就记一次路径点 —— 影线的信息量就来自这里
                    AccumulateCandle(c, stepStartTick + c.subStep * subTicks);
                }
            }
        }

        /// <summary>每日结算前调用：把当前价记为"昨日收盘价"，供日涨跌列使用。</summary>
        public void AnchorDayOpen()
        {
            keyBuffer.Clear();
            keyBuffer.AddRange(currencies.Keys);
            for (int i = 0; i < keyBuffer.Count; i++)
            {
                var c = currencies[keyBuffer[i]];
                if (c != null) c.previousRate = c.rate;
            }
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref currencies, "currencies", LookMode.Value, LookMode.Deep);
            Scribe_Collections.Look(ref anchors, "anchors", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref series, "series", LookMode.Value, LookMode.Deep);
            if (currencies == null) currencies = new Dictionary<int, FxCurrency>();
            if (anchors == null) anchors = new Dictionary<int, float>();
            if (series == null) series = new Dictionary<int, FxRateSeries>();
        }

        /// <summary>
        /// 为所有货币建立"本步的整步预算"，但不推进任何子步。
        ///
        /// 开局与<b>读档</b>后立即调用：把 <c>stepLogStart / stepLogTarget</c> 填成
        /// 当前价（预算为零位移），于是第一个子步就把价格保持在原处，
        /// 而不是停留在"预算未建立"的状态干等一个报价步。
        ///
        /// 这是 <see cref="FxCurrency.stepBudgetValid"/> 的配套：
        /// 不建立预算就直接推进子步的话，<c>Exp(0) = 1 = MinRate</c>，
        /// 价格会被瞬间打到下限。
        /// </summary>
        public void BeginStepBudget()
        {
            keyBuffer.Clear();
            keyBuffer.AddRange(currencies.Keys);
            for (int i = 0; i < keyBuffer.Count; i++)
            {
                var c = currencies[keyBuffer[i]];
                if (c == null) continue;

                float anchor;
                int id = keyBuffer[i];
                if (!anchors.TryGetValue(id, out anchor) || anchor <= 0f)
                {
                    anchor = c.rate;
                    anchors[id] = anchor;
                }

                // 先建立多尺度参数（读档后 ouX 为 null，必须在这里补齐，
                // 否则第一个 TickStep 会因为改动幅度过大而跳价）
                c.EnsureOsc();

                // 预算 = 零位移：不消耗随机数、不改变价格，只是把字段填成当前价
                c.stepLogStart = Log(c.rate);
                c.stepLogTarget = c.stepLogStart;
                c.subStep = 0;
                c.stepBudgetValid = true;
            }
        }

        /// <summary>
        /// 某个周期的 tick 长度。
        ///
        /// 时K / 日K 用<b>绝对游戏时间</b>（2500 / 60000），不用"多少报价步"——
        /// 因为报价间隔是玩家可调的滑块，用步数会让"一小时"随设置漂移。
        /// 锚在绝对时间上还有两个好处：日K 每根都从 0 时整点开始，
        /// 与每日结算同一相位，两者对得上。
        /// </summary>
        public static int PeriodTicksOf(FxChartSpan span)
        {
            switch (span)
            {
                case FxChartSpan.Hour: return FxGameComponent.TicksPerHour;
                case FxChartSpan.Day: return FxGameComponent.TicksPerDay;
                default: return TicksPerCandle;
            }
        }

        /// <summary>
        /// 采样一次价格，同时喂给三级周期（分/时/日）。
        ///
        /// 三级各自判断"是否该封根"，共用同一串价格路径，所以互相自洽。
        /// 每级只在自己的缓冲里保留 <see cref="MaxCandles"/> 根 ——
        /// 日K 缓冲能留下 100 游戏日的历史，而分K 缓冲的长度由报价节奏决定。
        /// </summary>
        private void AccumulateCandle(FxCurrency c, int nowTick)
        {
            var s = SeriesFor(c.factionLoadID);
            float rate = c.rate;

            // ── 分K：相位锚在报价步上（periodStartTick 由 TickStep 的虚拟 tick 驱动）──
            if (s.curOpen <= 0f || s.periodStartTick < 0)
            {
                s.curOpen = rate;
                s.curHigh = rate;
                s.curLow = rate;
                s.periodStartTick = nowTick;
            }
            else
            {
                if (rate > s.curHigh) s.curHigh = rate;
                if (rate < s.curLow) s.curLow = rate;

                if (nowTick - s.periodStartTick >= TicksPerCandle)
                {
                    s.candles.Add(new FxCandle(s.curOpen, s.curHigh, s.curLow, rate));
                    if (s.candles.Count > MaxCandles) s.candles.RemoveAt(0);
                    s.curOpen = rate;
                    s.curHigh = rate;
                    s.curLow = rate;
                    s.periodStartTick = nowTick;
                }
            }

            // ── 时K / 日K：相位锚在绝对 tick 的整除边界上 ──
            AccumulatePeriod(s.hourCandles, FxGameComponent.TicksPerHour, nowTick, rate,
                ref s.hourStartTick, ref s.hourOpen, ref s.hourHigh, ref s.hourLow);
            AccumulatePeriod(s.dayCandles, FxGameComponent.TicksPerDay, nowTick, rate,
                ref s.dayStartTick, ref s.dayOpen, ref s.dayHigh, ref s.dayLow);
        }

        /// <summary>
        /// 把价格喂给一个"按绝对时间对齐"的周期缓冲。
        ///
        /// 封根判据是 <c>nowTick / periodTicks</c> 跨过了一个整数 ——
        /// 也就是"进入了下一个整点/整日"。这样每根 K 线都从整点开始，
        /// 而且不管采样多稀疏都不会漏掉边界（因为是比较所属区间，不是比较差值）。
        /// </summary>
        private void AccumulatePeriod(List<FxCandle> buf, int periodTicks, int nowTick, float rate,
            ref int startTick, ref float open, ref float high, ref float low)
        {
            if (periodTicks < 1) return;

            int bucket = nowTick / periodTicks;

            // 首次采样：开一根新的，起点对齐到本区间的整点
            if (startTick < 0)
            {
                startTick = bucket * periodTicks;
                open = rate; high = rate; low = rate;
                return;
            }

            // 还在同一区间内：更新高低
            if (nowTick - startTick < periodTicks)
            {
                if (rate > high) high = rate;
                if (rate < low) low = rate;
                return;
            }

            // 跨过区间边界：封掉旧根。用 rate 作收盘价 ——
            // 它已经包含了所有被跨过的子步位移（采样是连续的，不会跳价）。
            //
            // ⚠️ 必须先把 rate 并入旧根的 high/low（2026-10-21 修 R03）：
            // 同一区间内只更新 high/low，跨区间时却直接把它当 close 写出去，
            // 于是"跨边界那一笔恰好创了新高/新低"时会出现 close > high 的非法 K 线。
            // 采样是连续的，把边界样本同时算进旧根的收盘与高低，K 线才自洽
            // （新根的开盘价也是 rate，两根在边界上相接）。
            if (rate > high) high = rate;
            if (rate < low) low = rate;
            buf.Add(new FxCandle(open, high, low, rate));
            if (buf.Count > MaxCandles) buf.RemoveAt(0);

            startTick = bucket * periodTicks;
            open = rate; high = rate; low = rate;
        }

        /// <summary>
        /// 清空所有货币的 K 线历史（分/时/日三级全清），并让累积器从<b>当前价</b>重新起步。
        ///
        /// 用途：调完行情模型（慢周期强度、跳空强度、波动率…）之后，
        /// 图上还混着旧模型生成的 K 线，肉眼看不出新模型到底长什么样。
        /// 清空后从零积累，形态就是纯新模型的产物。
        ///
        /// <b>安全性</b>：K 线纯粹是派生数据 —— 汇率、持仓、余额、交易历史全都不在这里，
        /// 所以清空不影响任何账目。清完马上从当前价开始画，不会出现空图。
        /// 被封装的 curOpen/curHigh/curLow 也一并复位成当前价，
        /// 否则"正在形成的那一根"还会带着旧极值。
        /// </summary>
        public void ClearAllCandles()
        {
            keyBuffer.Clear();
            keyBuffer.AddRange(currencies.Keys);
            for (int i = 0; i < keyBuffer.Count; i++)
            {
                var c = currencies[keyBuffer[i]];
                if (c == null) continue;

                FxRateSeries s;
                if (!series.TryGetValue(c.factionLoadID, out s) || s == null) continue;

                ResetSeries(s.candles, ref s.periodStartTick,
                    ref s.curOpen, ref s.curHigh, ref s.curLow, c.rate);
                ResetSeries(s.hourCandles, ref s.hourStartTick,
                    ref s.hourOpen, ref s.hourHigh, ref s.hourLow, c.rate);
                ResetSeries(s.dayCandles, ref s.dayStartTick,
                    ref s.dayOpen, ref s.dayHigh, ref s.dayLow, c.rate);
            }
        }

        /// <summary>
        /// 把一条 K 线缓冲复位成"从 rate 起一根新 K 线"。
        /// 起点 tick 置 -1，交给 AccumulatePeriod 下一帧重新对齐到整数周期边界，
        /// 这样封根相位仍然正确（不会因为清空而错位）。
        /// </summary>
        private static void ResetSeries(List<FxCandle> buf, ref int startTick,
            ref float open, ref float high, ref float low, float rate)
        {
            if (buf != null) buf.Clear();
            startTick = -1;
            open = rate;
            high = rate;
            low = rate;
        }

        /// <summary>
        /// 取某货币最近 want 根 K 线（含正在形成中的当前根），按周期选择缓冲。
        ///
        /// 三级缓冲各自独立，所以切换周期时看到的是各自的历史长度：
        /// 分K 约 1.8 游戏日、时K 约 4.2 游戏日、日K 约 100 游戏日（默认设置下）。
        ///
        /// ── 返回值是<b>复用的内部缓冲</b>（2026-10-21 改，D4）──
        /// 原来是每次调用 new 一个 List、再把每根引用拷进去、最后再 new 一个"正在形成"
        /// 的 K 线对象。而它被 DrawCandles 在 OnGUI 的每一趟都调一次（Layout + Repaint），
        /// 图表开着时每秒要丢掉上百 KB 短命对象。
        ///
        /// 现在固定复用 <see cref="viewBuf"/> 与 <see cref="viewLive"/>：
        ///   · 不再分配 List / K 线对象
        ///   · 「正在形成」那根每次原地更新字段
        ///
        /// ⚠️ 调用方约定：**只能在当前帧内使用返回值**，不要持有到下一帧，
        /// 也不要修改它（下一帧会被覆盖）。目前唯一的调用方是 DrawCandles，
        /// 它在同一帧里画完就丢，符合约定。
        /// </summary>
        public List<FxCandle> GetCandles(int factionLoadID, int want, FxChartSpan span = FxChartSpan.Minute)
        {
            viewBuf.Clear();

            FxRateSeries s;
            if (!series.TryGetValue(factionLoadID, out s) || s == null) return viewBuf;

            List<FxCandle> buf;
            float curOpen, curHigh, curLow;
            switch (span)
            {
                case FxChartSpan.Hour:
                    buf = s.hourCandles; curOpen = s.hourOpen; curHigh = s.hourHigh; curLow = s.hourLow;
                    break;
                case FxChartSpan.Day:
                    buf = s.dayCandles; curOpen = s.dayOpen; curHigh = s.dayHigh; curLow = s.dayLow;
                    break;
                default:
                    buf = s.candles; curOpen = s.curOpen; curHigh = s.curHigh; curLow = s.curLow;
                    break;
            }
            if (buf == null) return viewBuf;

            int from = buf.Count - want;
            if (from < 0) from = 0;
            for (int i = from; i < buf.Count; i++) viewBuf.Add(buf[i]);

            // 正在形成中的当前根，让图表实时跟着走（复用同一个对象）
            if (curOpen > 0f)
            {
                float last = currencies.TryGetValue(factionLoadID, out var cur) && cur != null
                    ? cur.rate : curOpen;
                viewLive.open = curOpen;
                viewLive.high = curHigh;
                viewLive.low = curLow;
                viewLive.close = last;
                viewBuf.Add(viewLive);
            }
            return viewBuf;
        }

        /// <summary>GetCandles 的复用缓冲。见该方法的返回值约定。</summary>
        private readonly List<FxCandle> viewBuf = new List<FxCandle>(128);
        /// <summary>"正在形成"那根 K 线的复用对象，每次调用原地更新字段。</summary>
        private readonly FxCandle viewLive = new FxCandle();

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static float Log(float v) => (float)System.Math.Log(v);
        private static float Exp(float v) => (float)System.Math.Exp(v);
        /// <summary>幂运算。与 Log/Exp 一样用 double 精度，避免 float 在幂上累积误差。</summary>
        private static float Pow(float v, float p) => (float)System.Math.Pow(v, p);
    }

    /// <summary>
    /// 货币名称生成器。
    ///
    /// ── 设计要点 ──
    /// 1. <b>确定性</b>：种子由 (全局名字种子, 派系 loadID) 共同派生，
    ///    所以同一存档里同一派系的名字稳定不变；改全局种子则全部重掷。
    /// 2. <b>数据驱动</b>：每个档位有自己的词库与组装规则（见 TierStyle），
    ///    而不是在一堆 if/else 里硬编码。加档位只要加一条风格定义。
    /// 3. <b>语言感知</b>：游戏语言是中文时用中文词库，否则用英文词库 ——
    ///    否则中文界面里会混进 "Royal Groat" 这种英文货币名。
    /// 4. <b>长度受控</b>：名字会显示在行情表、图表标题、下单标题里，
    ///    过长会把 /XAG 后缀挤出列宽，所以组装时限制词长与段数。
    /// </summary>
    public static class FxCurrencyGenerator
    {
        /// <summary>一个档位的命名风格。</summary>
        private class TierStyle
        {
            /// <summary>货币主体名词（信用点、贝壳……）。</summary>
            public string[] Units;
            /// <summary>修饰前缀（王朝、公司、地理……）。</summary>
            public string[] Prefixes;
            /// <summary>把派系名放进名字里的概率。</summary>
            public float FactionNameChance;
            /// <summary>用"前缀 + 主体"形式的概率。</summary>
            public float PrefixChance;
            /// <summary>初始汇率区间。</summary>
            public float RateMin;
            public float RateMax;
        }

        /// <summary>一个语言的完整档位词库。</summary>
        private class LangPack
        {
            public TierStyle Tribal;
            public TierStyle Medieval;
            public TierStyle Industrial;
            public TierStyle Spacer;
            public TierStyle Raider;

            public TierStyle For(FxVolTier tier)
            {
                switch (tier)
                {
                    case FxVolTier.Tribal: return Tribal;
                    case FxVolTier.Medieval: return Medieval;
                    case FxVolTier.Spacer: return Spacer;
                    case FxVolTier.Raider: return Raider;
                    default: return Industrial;
                }
            }
        }

        // ==================== 英文词库 ====================

        private static readonly LangPack EnglishPack = new LangPack
        {
            Tribal = new TierStyle
            {
                Units = new[]
                {
                    "Shell", "Bead", "Cowrie", "Feather", "Amber", "Tusk",
                    "Seed", "Scale", "Tooth", "Obsidian", "Salt", "Coral"
                },
                Prefixes = new[] { "Great", "Sun", "Moon", "River", "Elder", "Sacred", "Deep" },
                FactionNameChance = 0.55f, PrefixChance = 0.20f,
                RateMin = 20f, RateMax = 140f
            },
            Medieval = new TierStyle
            {
                Units = new[]
                {
                    "Crown", "Mark", "Taler", "Florin", "Ducat", "Guilder",
                    "Groat", "Penny", "Sovereign", "Realm", "Shilling", "Denier"
                },
                Prefixes = new[] { "Royal", "Silver", "Golden", "Old", "Grand", "Iron", "High" },
                FactionNameChance = 0.35f, PrefixChance = 0.35f,
                RateMin = 8f, RateMax = 200f
            },
            Industrial = new TierStyle
            {
                Units = new[]
                {
                    "Credit", "Dollar", "Kredit", "Peso", "Yuan", "Franc",
                    "Lira", "Pound", "Ruble", "Krone", "Escudo", "Real"
                },
                Prefixes = new[] { "Federal", "Union", "Central", "National", "Republic", "State" },
                FactionNameChance = 0.30f, PrefixChance = 0.30f,
                RateMin = 5f, RateMax = 260f
            },
            Spacer = new TierStyle
            {
                Units = new[]
                {
                    "Credit", "Unit", "Token", "Share", "Quanta", "Standard",
                    "Script", "Ledger", "Warrant", "Alloy"
                },
                Prefixes = new[]
                {
                    "Arc", "Vega", "Orion", "Helix", "Nova", "Zeta",
                    "Kappa", "Draco", "Lyra", "Atlas", "Cygnus", "Rigel"
                },
                FactionNameChance = 0.25f, PrefixChance = 0.55f,
                RateMin = 5f, RateMax = 260f
            },
            Raider = new TierStyle
            {
                Units = new[]
                {
                    "Scrip", "Tokar", "Chit", "Slug", "Writ", "Flake",
                    "Shard", "Bloodmark", "Tally", "Ration", "Iron", "Spoil"
                },
                Prefixes = new[] { "Blood", "War", "Bone", "Ash", "Rust", "Grim", "Spoil" },
                FactionNameChance = 0.65f, PrefixChance = 0.25f,
                RateMin = 10f, RateMax = 180f
            }
        };

        // ==================== 中文词库 ====================

        private static readonly LangPack ChinesePack = new LangPack
        {
            Tribal = new TierStyle
            {
                Units = new[]
                {
                    "贝壳", "串珠", "海贝", "羽饰", "琥珀", "兽牙", "盐块",
                    "骨片", "绿松石", "红珊瑚", "燧石", "鱼鳞"
                },
                Prefixes = new[] { "大河", "太阳", "月华", "先祖", "秘境", "苍岩", "圣火" },
                FactionNameChance = 0.55f, PrefixChance = 0.20f,
                RateMin = 20f, RateMax = 140f
            },
            Medieval = new TierStyle
            {
                Units = new[]
                {
                    "金币", "银币", "铜钱", "银锭", "金马克", "弗罗林",
                    "格罗特", "先令", "泰勒", "达克特", "王冠币", "第纳尔"
                },
                Prefixes = new[] { "王家", "白银", "黄金", "古老", "大公", "铁铸", "高塔" },
                FactionNameChance = 0.35f, PrefixChance = 0.35f,
                RateMin = 8f, RateMax = 200f
            },
            Industrial = new TierStyle
            {
                Units = new[]
                {
                    "信用点", "元", "马克", "法郎", "里拉", "比索",
                    "卢布", "克朗", "埃斯库多", "雷亚尔", "盾", "镑"
                },
                Prefixes = new[] { "联邦", "联合", "中央", "国家", "共和", "国资" },
                FactionNameChance = 0.30f, PrefixChance = 0.30f,
                RateMin = 5f, RateMax = 260f
            },
            Spacer = new TierStyle
            {
                Units = new[]
                {
                    "信用点", "单位", "代币", "股份", "量子", "标准券",
                    "记账券", "权益券", "合金币", "凭证券"
                },
                Prefixes = new[]
                {
                    "弧光", "织女", "猎户", "螺旋", "新星", "泽塔",
                    "卡帕", "天龙", "天琴", "阿特拉斯", "天鹅", "参宿"
                },
                FactionNameChance = 0.25f, PrefixChance = 0.55f,
                RateMin = 5f, RateMax = 260f
            },
            Raider = new TierStyle
            {
                Units = new[]
                {
                    "票券", "白条", "筹子", "铁片", "战利券", "血印",
                    "配给券", "碎银", "牙账", "人头账", "焦铁", "战旗券"
                },
                Prefixes = new[] { "血旗", "战团", "白骨", "灰烬", "锈刃", "凶牙", "掠夺" },
                FactionNameChance = 0.65f, PrefixChance = 0.25f,
                RateMin = 10f, RateMax = 180f
            }
        };

        /// <summary>
        /// 当前游戏语言是否用中文词库。
        ///
        /// 判据是 <see cref="LoadedLanguage.folderName"/> 里是否含 "Chinese" ——
        /// 这样简中（"ChineseSimplified (简体中文)"）和繁中都能覆盖到，
        /// 也不依赖具体文件夹名的完整拼写。
        /// </summary>
        private static bool UseChinese
        {
            get
            {
                LoadedLanguage lang = LanguageDatabase.activeLanguage;
                if (lang == null) return false;
                string f = lang.folderName;
                return !f.NullOrEmpty()
                    && f.IndexOf("Chinese", System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static LangPack Pack => UseChinese ? ChinesePack : EnglishPack;

        /// <summary>
        /// 名称最大字符数。超过就在组装时退化成更短的形式，
        /// 因为行情表的"货币对"列宽有限，长名字会把 /XAG 后缀挤出去。
        ///
        /// 中文按"字符数"算会偏严（一个汉字比一个拉丁字母宽），
        /// 所以中文下用更小的上限。
        /// </summary>
        private static int MaxNameLength => UseChinese ? 12 : 22;

        public static FxCurrency Generate(Faction faction)
        {
            // 用 (全局名字种子, 派系 loadID) 共同派生种子：
            //   - 同一存档内稳定（只要不重掷）
            //   - 换存档 / 点"重掷"后全部换新
            int seed = NameSeed ^ (faction.loadID * 7919 + 104729);

            string name, symbol;
            float rate;
            // try/finally：原版 Rand 种子栈是全局的，抛异常后不 Pop 会污染后续所有随机。
            Rand.PushState(seed);
            try
            {
                FxVolTier tier = FxCurrency.TierFor(faction.def);
                TierStyle style = Pack.For(tier);

                string factionName = faction.Name.NullOrEmpty() ? "" : faction.Name.Trim();
                name = Compose(style, factionName);

                symbol = MakeSymbol(name, faction.loadID);

                rate = Rand.Range(style.RateMin, style.RateMax);
            }
            finally
            {
                Rand.PopState();
            }

            return Build(faction, name, symbol, rate);
        }

        /// <summary>
        /// 全局名字种子。存在模组设置里，点"重掷"时换一个值，
        /// 于是所有派系的货币名都会重新生成。
        /// </summary>
        private static int NameSeed
        {
            get
            {
                RimForexSettings s = RimForexMod.Settings;
                return s == null ? 0 : s.nameSeed;
            }
        }

        private static FxCurrency Build(Faction faction, string name, string symbol, float rate)
        {
            var currency = new FxCurrency
            {
                factionLoadID = faction.loadID,
                factionName = faction.Name,
                factionDefName = faction.def != null ? faction.def.defName : "",
                name = name,
                symbol = symbol,
                rate = rate,
                previousRate = rate
            };
            // 由派系定义设定档位（tier + tierResolved），避免与外面的局部变量不同步
            currency.SetTier(faction.def);
            return currency;
        }

        /// <summary>
        /// 把当前名字种子应用到<b>所有已存在</b>的货币上，返回改动的数量。
        ///
        /// ── 为什么需要这个 ──
        /// 货币是在首次遇到派系时生成并<b>存进存档</b>的。改生成逻辑或换种子
        /// 只能影响"之后新生成的"派系 —— 已经躺在存档里的货币名不会变，
        /// 玩家点了"重掷"会发现终端里毫无变化，以为按钮坏了。
        /// 所以重掷时必须显式回写。
        ///
        /// 只改 name / symbol，<b>不动</b>汇率、K 线、持仓、交易历史 ——
        /// 改名不应该影响任何经济数据。
        /// </summary>
        public static int ApplyToExisting()
        {
            if (Find.World == null) return 0;
            FxGameComponent fx = Find.World.GetComponent<FxGameComponent>();
            if (fx == null || fx.market == null) return 0;

            int changed = 0;
            var all = new List<FxCurrency>(fx.market.AllCurrencies);
            for (int i = 0; i < all.Count; i++)
            {
                FxCurrency c = all[i];
                if (c == null) continue;

                Faction f = FindFaction(c.factionLoadID);
                if (f == null || f.def == null) continue;

                string oldName = c.name;
                RerollName(c, f);
                if (c.name != oldName) changed++;
            }
            return changed;
        }

        /// <summary>按 loadID 在派系管理器里找回派系。找不到就返回 null（该货币跳过）。</summary>
        private static Faction FindFaction(int loadID)
        {
            var list = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].loadID == loadID) return list[i];
            }
            return null;
        }

        /// <summary>
        /// 给一个<b>已存在</b>的货币重掷名字（保留汇率、K 线、持仓、历史）。
        ///
        /// 行情表里显示的是 currency.name，所以改名后终端会立刻反映出来。
        /// </summary>
        public static void RerollName(FxCurrency c, Faction faction)
        {
            if (c == null || faction == null || faction.def == null) return;
            if (c.factionLoadID != faction.loadID) return;

            int seed = NameSeed ^ (faction.loadID * 7919 + 104729);

            string name;
            Rand.PushState(seed);
            try
            {
                FxVolTier tier = FxCurrency.TierFor(faction.def);
                TierStyle style = Pack.For(tier);
                string factionName = faction.Name.NullOrEmpty() ? "" : faction.Name.Trim();

                name = Compose(style, factionName);
            }
            finally
            {
                Rand.PopState();
            }

            c.name = name;
            c.symbol = MakeSymbol(name, faction.loadID);
        }

        /// <summary>
        /// 按风格组装一个名字。允许的形式（按概率抽）：
        ///     前缀 + 主体      如 "Royal Crown" / "王家金币"
        ///     派系名 + 主体    如 "Vega Credit" / "织女信用点"
        ///     单独主体         如 "Credit" / "信用点"
        /// 若抽到的形式超过长度上限，退化成更短的形式。
        ///
        /// 中文下"派系名 + 主体"也需要一个空格，否则汉字连排会看不清断句。
        /// </summary>
        private static string Compose(TierStyle style, string factionName)
        {
            string unit = Rand.Element(style.Units);
            string prefix = Rand.Element(style.Prefixes);
            string joiner = UseChinese ? "" : " ";

            bool usePrefix = Rand.Chance(style.PrefixChance);
            bool useFaction = !factionName.NullOrEmpty() && Rand.Chance(style.FactionNameChance);

            if (usePrefix)
            {
                string s = prefix + joiner + unit;
                if (s.Length <= MaxNameLength) return s;
            }
            if (useFaction)
            {
                string s = factionName + joiner + unit;
                if (s.Length <= MaxNameLength) return s;
            }
            return unit;
        }

        /// <summary>
        /// 生成货币符号。
        ///
        /// 拉丁名字取前三个字母；中文名没有字母，取不到就会退化成 "FX"，
        /// 那样所有中文货币符号都会撞车，所以给中文名单独做一张缩写表：
        /// 按名字里的关键字取一个汉字作符号（如"信用点"→"信"）。
        /// </summary>
        private static string MakeSymbol(string name, int loadID)
        {
            string letters = "";
            for (int i = 0; i < name.Length && letters.Length < 3; i++)
            {
                char ch = name[i];
                if (ch >= 'A' && ch <= 'Z') { letters += ch; continue; }
                if (ch >= 'a' && ch <= 'z') { letters += char.ToUpperInvariant(ch); continue; }
                // 汉字：取它作为符号的一部分，最多两个
                if (ch >= 0x4E00 && ch <= 0x9FFF && letters.Length < 2) letters += ch;
            }
            if (letters.Length == 0) letters = "FX";

            char tag = (char)('A' + (loadID % 26));
            return letters + tag;
        }

        /// <summary>
        /// 生成一批示例名字，供模组设置里的"测试生成"按钮展示。
        ///
        /// 与真实生成走同一套 Compose 逻辑，所以看到的就是游戏里会出现的风格。
        /// 会覆盖全部五个档位。
        /// </summary>
        public static void SampleNames(List<string> into, int total)
        {
            if (into == null || total <= 0) return;

            string[] sampleFactions = UseChinese
                ? new[] { "织女商会", "沙尘氏族", "自由港", "锈线", "凯什", "铁堡" }
                : new[] { "Vega Combine", "Dust Clan", "Free Ports", "Rustline", "Kesh", "Ironhold" };

            FxVolTier[] order =
            {
                FxVolTier.Spacer, FxVolTier.Standard, FxVolTier.Medieval,
                FxVolTier.Tribal, FxVolTier.Raider
            };

            for (int i = 0; i < total; i++)
            {
                FxVolTier tier = order[i % order.Length];
                string fac = sampleFactions[(i / order.Length) % sampleFactions.Length];

                // 用真实种子体系，所以"重掷"后这里也会跟着变
                int seed = NameSeed ^ ((i + 1000) * 7919 + (int)tier * 31);

                string name, symbol;
                Rand.PushState(seed);
                try
                {
                    TierStyle style = Pack.For(tier);
                    name = Compose(style, fac);
                    symbol = MakeSymbol(name, i * 7);
                }
                finally
                {
                    Rand.PopState();
                }

                into.Add(TierLabel(tier).PadRight(9) + name.PadRight(20) + "[" + symbol + "]");
            }
        }

        private static string TierLabel(FxVolTier tier)
        {
            switch (tier)
            {
                case FxVolTier.Tribal: return UseChinese ? "部落" : "Tribal";
                case FxVolTier.Medieval: return UseChinese ? "中世纪" : "Medieval";
                case FxVolTier.Spacer: return UseChinese ? "太空" : "Spacer";
                case FxVolTier.Raider: return UseChinese ? "袭击者" : "Raider";
                default: return UseChinese ? "工业" : "Standard";
            }
        }
    }

    /// <summary>
    /// 一次"临近爆仓"预警。运行时对象，不需要存档（读档后若仍危险会重新触发）。
    /// </summary>
    public class MarginWarning
    {
        public FxAccount account;
        public FxPosition position;
        // 曾经还有一个 `public Map map;` —— 由 EnqueueWarning 扫 Find.Maps 填进来，
        // 但从来没有消费者（预警窗只用 position / account）。已删除。
    }

    /// <summary>
    /// 世界级组件：持有汇率市场与殖民地的 FX 账户，负责实时估值、爆仓判定与每日世界事件。
    /// 做成 WorldComponent 是因为它随存档持久化。
    /// </summary>
    public class FxGameComponent : WorldComponent
    {
        /// <summary>
        /// 一个游戏日的 tick 数。**必须与原版 GenDate.TicksPerDay 保持一致。**
        ///
        /// ── 这里错过一次大的，值得记下来 ──
        /// 我原本写的是 3600，来源是"24 游戏时 × 150 tick/时"这个自己推的算式。
        /// 但原版根本没有 150 这个数 —— 反编译 RimWorld.GenDate 得到的是：
        ///     TicksPerDay  = 60000
        ///     TicksPerHour = 2500
        ///     HoursPerDay  = 24
        /// 也就是说我整整差了 <b>16.67 倍</b>。
        ///
        /// 后果非常具体：结算的"整日边界判定"写成 `TicksAbs % 3600 == 0`，
        /// 而真实一天有 60000 tick —— 一个真实游戏日里这个条件会命中 16 次，
        /// 于是结算事件在一天中到处乱跳，根本不是午夜。
        ///
        /// 现在直接引用原版常量，原版改了我也跟着改。
        /// </summary>
        public const int TicksPerDay = GenDate.TicksPerDay;      // 60000

        /// <summary>
        /// 一个游戏小时的 tick 数 = 原版 GenDate.TicksPerHour = 2500。
        /// 同样直接引用，不再自己推导。
        /// </summary>
        public const int TicksPerHour = GenDate.TicksPerHour;    // 2500

        /// <summary>
        /// 1x 速度下每现实秒的 tick 数。游戏固定 60 tick/现实秒，
        /// 所以把 tick 除以它可以换算成"现实秒"。
        /// </summary>
        public const int TicksPerRealSecond = 60;

        /// <summary>
        /// 结算发信规则（2026-10-23 改）。
        ///
        /// 旧规则是"当日盈亏绝对值 ≥ max(200, 净值 × 1%)，或者当天动过仓"，
        /// 平淡日再累计 3 天补一封摘要。问题：
        ///   · 门槛跟着账户规模涨，账户一大就天天触发，玩家感受是"怎么一直在结算"；
        ///   · 真正让人想看的其实是"我有没有在动"，不是"今天浮动了多少"；
        ///   · 摘要信还会和空账户叠在一起刷屏（见 AccountFor 的幽灵账户注释）。
        ///
        /// 新规则只有两条：
        ///   ① 当天动过仓（开或平）→ 发；
        ///   ② 收盘时还有持仓 → 发（有仓就有玩家关心的浮动盈亏）。
        /// 空仓又没交易的日子**彻底静默**，摘要信一并取消。
        /// </summary>

        /// <summary>
        /// 平台手续费率：按名义规模收取。开仓和平仓各收一次。
        /// 0.02% —— 比早期版本的 0.002% 明显有感，但仍远小于点差，
        /// 所以玩家的主要摩擦来源依旧是点差，手续费只作为"平台抽成"存在。
        /// </summary>
        public const float FeeRate = 0.0002f;
        /// <summary>单笔手续费下限（白银），对应真实平台的"最低 2 元"。开平各计一次。</summary>
        public const float MinFee = 2f;

        /// <summary>
        /// 唯一的预警线（健康度）：低于此值时强制暂停游戏并弹出预警窗口。
        ///
        /// ── 健康度与"浮亏百分比"的换算（容易搞混，务必看清）──
        /// 健康度 = 净值 / 原始保证金，所以：
        ///     健康度 0.45 ⇔ 浮亏 55%  ← 本阈值（2026-10-20）
        ///     健康度 0.30 ⇔ 浮亏 70%  ← 强平线
        /// 取 0.45 是为了给强平线留出 15 个百分点的缓冲，让玩家还有机会追加保证金。
        ///
        /// ── 为什么从 0.60 / 0.50 一起下调 ──
        /// 作者反馈"强平线太高，没有节目效果"：50% 就砍太早了。
        /// 改成 30% 后，100x 下的爆仓距离从约 4.6 根报价拉长到 7.2 根（+57%），
        /// 仓位能活更久、波动更充分，穿仓（负余额）的概率也随之上升 ——
        /// 这是刻意的，穿仓本身就是这个模组的卖点之一。
        ///
        /// 不变量：<c>CriticalThreshold &gt; StopOutFraction</c>，
        /// 否则强平会先发生、预警永远不触发。`Docs/audit.py` 会强制检查。
        /// </summary>
        public const float CriticalThreshold = 0.45f;  // 浮亏 55%

        /// <summary>
        /// 预警的<b>滞回带</b>宽度（健康度单位）。
        ///
        /// 健康度必须回到 <c>CriticalThreshold + 本值</c>（默认 0.55）以上，
        /// 才算"脱离危险"、重新武装预警。
        ///
        /// ── 为什么必须有它（2026-10-21）──
        /// 原来只判 <c>health &gt; CriticalThreshold</c>，于是健康度在 0.45 附近
        /// 上下摆动时会 "复位 → 提醒 → 复位 → 提醒" 无限循环。
        ///
        /// 修好子步采样（R01）之后，每个报价步的采样次数从 1 次变成 17 次，
        /// 这个循环立刻被放大成刷屏 —— 100x 下盘内桥的摆幅折算到健康度就有
        /// ±2.3 个百分点，几乎每一步都要穿越 0.45。作者实测：预警窗几乎
        /// 每次健康度下跌都弹，并且每次都暂停游戏。
        ///
        /// 取 10 个百分点：远大于 100x 时的盘内摆幅（±2.3），
        /// 又不会大到让玩家错过真正的二次恶化。
        /// </summary>
        public const float WarnHysteresis = 0.10f;

        /// <summary>
        /// 同一仓位两次预警之间的最小间隔（游戏 tick）。默认 1 游戏小时。
        ///
        /// 滞回带能挡住"同一段下跌反复穿越"，但挡不住
        /// "弹一次 → 回到 0.55 以上 → 一分钟内又跌回 0.45 以下"这种来回。
        /// 再加一道时间闸，保证最坏情况下也只有约每游戏小时一次打断。
        ///
        /// 只作用于预警，<b>不影响强平</b> —— 强平仍然按子步实时判定，
        /// 不做任何防抖（作者决定：插针爆仓本身就是这个模组要表达的东西）。
        /// </summary>
        public const int WarnCooldownTicks = TicksPerHour;

        /// <summary>
        /// 强平线（Stop-Out Level）：净值跌到"已用保证金 × 这个比例"时强制平仓，
        /// 即浮亏 70%。
        ///
        /// 真实海外 FX 平台的强平线普遍设在保证金水平的 <b>20% ~ 50%</b>。
        /// 2026-10-20 从 0.5 下调到 0.3（三者里最靠下限的一档）：
        /// 玩家明确要求"别太早死，否则没节目效果"，同时 0.3 仍在真实平台的区间内。
        /// 平仓时剩余的净值会退还账户。
        ///
        /// ⚠️ 改这个值【必须】同时改 <see cref="MaxSafeLeverageFor"/> 里的 byVol 公式。
        /// 那里原本写的是 StopOutFraction / (…)，在 0.5 时恰好等于 1−StopOutFraction，
        /// 所以错误被掩盖了很久。改成 0.3 后两者不再相等，不改公式会让
        /// 高波动品种的最大可用杠杆从 100x 掉到 64x —— **100x 会被静默降档到 50x**。
        /// </summary>
        public const float StopOutFraction = 0.3f;

        /// <summary>
        /// 点差造成的保证金冲击上限（相对保证金的比例）。
        ///
        /// 点差成本按<b>名义仓位</b>计收，而保证金 = 名义仓位 ÷ 杠杆，
        /// 所以点差对保证金的冲击 = <c>点差 × 杠杆</c>，随杠杆<b>线性放大</b>。
        /// 一旦它超过强平线，仓位就会在开仓（或锁定期结束）的瞬间被强平 ——
        /// 玩家什么都没做就爆仓了。所以必须设硬上限。
        ///
        /// 取 0.35：给强平线（0.50）留出余量，玩家仍有约 15% 保证金的操作空间。
        /// 据此反解出每个品种的安全杠杆上限 = 0.35 / 往返点差。
        /// 例：Raider 点差 1.30% → 上限 26x，所以 50x/100x 会被禁用。
        /// </summary>
        public const float MaxSpreadImpactOnMargin = 0.35f;

        /// <summary>
        /// 强平线距离相对"日波动"的覆盖倍数，用于限制杠杆上限：
        /// <c>杠杆 ≤ StopOutFraction / (本值 × 日波动)</c>。
        ///
        /// ── 取 0.28 的含义 ──
        /// 它允许<b>强平距离远小于一天波动</b>：100x 下强平距离固定 0.5%，
        /// 而高波动档一天波动 1.68% —— 也就是 0.3 个日波动。
        /// 结果是这类仓位几乎必然在几小时内被强平。
        ///
        /// 这是刻意的：本模组的核心乐趣就是"梭哈百倍杠杆"，
        /// 所以选择让 100x 对<b>全部档位</b>开放，而不是把它锁死。
        /// 想更保守就调大这个值（1.0 = 一个 1σ 交易日不会强平），
        /// 代价是高波动档立刻无法开 100x。
        ///
        /// 早期取过 1.0 和 0.6，两者都把高波动档压到 20~50x，
        /// 使百倍杠杆形同虚设 —— 那是"真实平台"的思路，不是这个 mod 的初衷。
        /// </summary>
        public const float StopOutVolCoverage = 0.28f;

        /// <summary>
        /// 某品种在给定杠杆下，点差占保证金的比例是否安全（不会开仓即强平）。
        /// </summary>
        public static bool LeverageSafeFor(FxCurrency c, float leverage)
        {
            if (c == null || leverage <= 0f) return false;
            // 约束 1：开仓瞬间不能被点差打进强平
            if (FxMarket.SpreadRateFor(c) * leverage > MaxSpreadImpactOnMargin) return false;
            // 约束 2：强平线距离必须扛得住一天的正常波动
            if (leverage > MaxSafeLeverageFor(c)) return false;
            return true;
        }

        /// <summary>
        /// 某品种能用的最大杠杆，取两个约束中更紧的那个：
        ///
        ///   约束 1（开仓即死）：点差 × 杠杆 ≤ <see cref="MaxSpreadImpactOnMargin"/>
        ///                        → 杠杆 ≤ 0.35 / 点差
        ///   约束 2（被日波动打死）：强平距离 (1 − StopOut)/杠杆 ≥ N × 日波动
        ///                        → 杠杆 ≤ (1 − StopOut) / (N × 日波动)
        ///
        /// 通常约束 2 更紧，因为高波动品种的日波动远大于点差。
        /// 这就是为什么"高波动货币只能配低杠杆"—— 它是波动率的直接后果，
        /// 不是人为限制。真实平台对新兴市场货币也是这个逻辑。
        ///
        /// ── 2026-10-20 修正：分子原来是 StopOutFraction，写错了 ──
        ///
        /// 从开仓到强平，价格还能走的**比例**是 (1 − StopOutFraction) / 杠杆，
        /// 不是 StopOutFraction / 杠杆。举例：StopOut = 0.3 时净值要从 100% 掉到 30%，
        /// 可亏损的比例是 **70%**，不是 30%。
        ///
        /// 因为旧值正好是 0.5，`0.5` 和 `1 − 0.5` 相等，这个错误被完全掩盖了。
        /// 一旦 StopOutFraction 改成 0.3，错误分子会让 byVol 缩小：
        ///     高波动档（vol 1.2）从 149x 掉到 64x → 100x 被**静默降档到 50x**。
        /// 所以这两个常量必须一起改，不能只改一个。
        /// </summary>
        public static float MaxSafeLeverageFor(FxCurrency c)
        {
            if (c == null) return 1f;

            // 约束 1
            float sr = FxMarket.SpreadRateFor(c);
            float bySpread = sr <= 0f ? 100f : MaxSpreadImpactOnMargin / sr;

            // 约束 2：可亏损比例 = 1 − 强平线
            float dailyVol = FxMarket.DailyVol * (c.volatility > 0.01f ? c.volatility : 1f);
            float byVol = dailyVol > 0.0001f
                ? (1f - StopOutFraction) / (StopOutVolCoverage * dailyVol)
                : 100f;

            return System.Math.Min(bySpread, byVol);
        }

        /// <summary>
        /// 在「保证金 + 手续费 ≤ 可用余额」约束下，这个杠杆最多能开多少单位。
        ///
        /// 总花费 = u*rate/leverage + FeeFor(u*rate)，对 u 单调递增，因此用二分查找求精确最大值。
        /// （之前用闭式解 + 保守 floor，实测会比真正可开的数量少 1 单位；梭哈按钮上这个误差很明显。）
        /// </summary>
        public static int MaxAffordableUnits(float balance, float rate, float leverage, int upperBound = 100000000)
        {
            if (balance <= 0f || rate <= 0.01f || leverage <= 0f) return 0;

            float perUnitMargin = rate / leverage;
            if (perUnitMargin <= 0f) return 0;
            if (CostOf(1, perUnitMargin, rate) > balance) return 0;

            int lo = 1;
            int hi = upperBound;
            while (lo < hi)
            {
                // 上取整中点，避免 lo/hi 相邻时死循环
                int mid = lo + (hi - lo + 1) / 2;
                if (CostOf(mid, perUnitMargin, rate) <= balance) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        private static float CostOf(int units, float perUnitMargin, float rate)
        {
            return units * perUnitMargin + FeeFor(units * rate);
        }

        public static float FeeFor(float notional)
        {
            float f = notional * FeeRate;
            return f < MinFee ? MinFee : f;
        }

        public FxMarket market = new FxMarket();
        public Dictionary<int, FxAccount> accounts = new Dictionary<int, FxAccount>();
        public int nextSettlementTick = TicksPerDay;
        public bool initialized;

        // ── 0.3.0 世界事件（定名后不可改）──

        /// <summary>延迟事件队列（商队、报复袭击、收债期限…）。</summary>
        public List<FxPendingEvent> pendingEvents = new List<FxPendingEvent>();
        /// <summary>上一次世界事件的 tick，用于全局冷却。</summary>
        public int lastWorldEventTick = -999999;
        /// <summary>"外汇平台"临时派系的 loadID（0 = 还没创建过）。</summary>
        public int platformFactionLoadID;
        /// <summary>下次尝试 E4/E5 战争新闻的 tick。</summary>
        public int nextNewsTick;
        /// <summary>上次 E3（暴赚）触发的 tick。</summary>
        public int lastWealthEventTick;

        // ── 诊断读数（运行时，不存盘）：用来校准 σ 阈值 ──
        /// <summary>最近一次结算里最大的 |单日收益 / σ|。</summary>
        public float diagMaxAbsZ;
        /// <summary>最近一次结算里最大的单日涨跌幅（%）。</summary>
        public float diagMaxPct;
        /// <summary>累计统计过多少次结算。</summary>
        public int diagSettleDays;
        /// <summary>历史最高的 |z|。</summary>
        public float diagPeakZ;
        public float diagPeakPct;
        /// <summary>当前能派陆路商队的派系数量（诊断）。</summary>
        public int diagCaravanFactions;
        /// <summary>当前能派轨道贸易船的派系数量（诊断）。</summary>
        public int diagOrbitalFactions;
        /// <summary>当前能派帝国征收队的派系数量（诊断）。</summary>
        public int diagTributeFactions;
        /// <summary>待判定的难民（存 thingIDNumber，不存 Pawn 引用）。</summary>
        public List<int> refugeePawnIds = new List<int>();
        /// <summary>E2-B 被削弱的据点。</summary>
        public List<FxWeakenedSettlement> weakenedSettlements = new List<FxWeakenedSettlement>();

        // ── 0.3.0 E6（定名后不可改）──
        /// <summary>正在进行的报复循环。</summary>
        public List<FxRetaliation> retaliations = new List<FxRetaliation>();
        /// <summary>难民加入判定的截止 tick。</summary>
        public int refugeeDeadlineTick;

        // ── 0.3.0 E7（定名后不可改）──
        /// <summary>是否正在收债。</summary>
        public bool debtActive;
        /// <summary>欠债账户所在的 map.Parent.ID。</summary>
        public int debtMapId = -1;
        /// <summary>收债期限。</summary>
        public int debtDeadlineTick;
        /// <summary>收债人的 thingIDNumber。</summary>
        public int debtCollectorId = -1;

        /// <summary>全局事件冷却：两次世界事件之间至少 3 游戏日。</summary>
        public const int GlobalEventCooldown = 3 * TicksPerDay;

        // ── E1 / E3 参数（作者已拍板）──
        /// <summary>
        /// 触发阈值：**2.0 倍**单日 σ。
        ///
        /// 原来定的是 2.5σ，实测（作者 2026-10-25）6 个游戏日一次事件都没出。
        /// σ_day = 0.014 × vol，袭击者档 σ ≈ 0.49%（2.5σ 要 1.2% 的单日涨幅），
        /// 太空档 σ ≈ 1.75%（2.5σ 要 4.4%，实际几乎见不到）。
        /// 2.0σ 后门槛降到 0.98% ~ 3.5%，配合全局冷却下调，频率才合理。
        /// </summary>
        public const float EventSigma = 2.0f;
        /// <summary>每货币的事件冷却。</summary>
        public const int EventCooldownPerCurrency = 20 * TicksPerDay;
        /// <summary>E1 商队延迟。</summary>
        public const int E1CaravanDelay = 2 * TicksPerDay;
        /// <summary>E1-B 袭击掷骰延迟。</summary>
        public const int E1RaidDelay = 3 * TicksPerDay;
        /// <summary>
        /// E1-B 掷骰成功率。
        ///
        /// 0.30 实测太钝（作者 2026-10-25：点半天都不来），调到 0.60。
        /// 敌对派系的"货币暴涨"本来就该是个危险信号，一半以上会兑现比较合理。
        /// </summary>
        public const float E1RaidChance = 0.60f;
        /// <summary>E3 触发下限（绝对值）与占净值的比例。</summary>
        public const float E3MinPnl = 10000f;
        public const float E3EquityFraction = 0.05f;
        /// <summary>E3 好感度增量。</summary>
        public const int E3Goodwill = 6;
        /// <summary>E3 额外来袭的概率。</summary>
        public const float E3RaidChance = 0.35f;
        public const int E3GoodwillCooldown = 15 * TicksPerDay;

        // ── E2 / E4 / E5 参数（作者已拍板）──
        /// <summary>E2-B 弱化据点的生效窗口：10 游戏日。</summary>
        public const int E2WeakenWindow = 10 * TicksPerDay;
        /// <summary>E2-B 削掉的守军比例。</summary>
        public const float E2ThinFraction = 0.5f;
        /// <summary>E2-A 议价加成持续 5 日。</summary>
        public const int E2TradeBonusDays = 5;
        /// <summary>E4/E5 新闻漂移持续 3 日。</summary>
        public const int NewsDriftDays = 3;
        /// <summary>E4/E5 目标货币的漂移幅度（以日 σ 为单位）。</summary>
        public const float NewsDriftTargetSigma = 1.5f;
        /// <summary>E4/E5 对手货币的漂移幅度。</summary>
        public const float NewsDriftOtherSigma = 0.8f;
        /// <summary>E4/E5 新闻兑现的概率。</summary>
        public const float NewsDriftChance = 0.6f;

        // ── E6 参数（作者已拍板）──
        /// <summary>做空名义仓位的绝对下限。</summary>
        public const float E6ShortNotionalMin = 200000f;
        /// <summary>做空名义仓位相对净值的倍数。</summary>
        public const float E6ShortEquityMultiple = 3f;
        /// <summary>空头仓位要持续多少天才算"盯上了"。</summary>
        public const int E6ShortHoldDays = 3;
        /// <summary>报复袭击间隔：2 游戏日一次。</summary>
        public const int E6RaidInterval = 2 * TicksPerDay;
        /// <summary>多少次袭击算一次金融危机。</summary>
        public const int E6RaidsPerCrisis = 10;
        /// <summary>几次危机让派系名存实亡。</summary>
        public const int E6CrisesToDefeat = 5;
        /// <summary>危机时货币永久乘数。</summary>
        public const float E6CrisisFactor = 0.9f;
        /// <summary>危机难民数量范围。</summary>
        public const int E6RefugeeMin = 3;
        public const int E6RefugeeMax = 5;
        /// <summary>难民加入概率（每日）。</summary>
        public const float E6RefugeeJoinChance = 0.35f;
        /// <summary>难民等待天数。</summary>
        public const int E6RefugeeDays = 3;
        /// <summary>
        /// 第五次危机到"派系消亡"的间隔：2 游戏小时。
        ///
        /// 直接弹窗会把第 5 封危机信盖掉（作者 2026-10-25 实测：
        /// 只看到消亡弹窗，红信根本没出现）。先让信落地，两小时后再清算。
        /// </summary>
        public const int E6DefeatDelay = 2 * 2500;

        // ── E7 参数（作者已拍板）──
        /// <summary>负余额持续几天后开始收债。</summary>
        public const int E7NegativeDays = 2;
        /// <summary>收债宽限期：3 天。</summary>
        public const int E7GraceDays = 3;
        /// <summary>攻击收债人后的账户冻结时长（游戏日）。</summary>
        public const int E7FreezeDays = 60;
        /// <summary>攻击收债人招来的机械袭击最低点数。</summary>
        public const float E7MechRaidMinPoints = 6000f;

        // ── 托管费（闲置余额的存放成本）──
        /// <summary>免收额度：20,000 银以下不收。</summary>
        public const float CustodyFree = 20000f;
        /// <summary>
        /// 超出部分每日费率 3%。
        ///
        /// 0.3% 实测形同虚设（作者 2026-10-25：5 万余额一天才扣 100 银），
        /// 调到 3% 后，藏 5 万一天的成本约 900 银，才真正构成"要不要把钱放这儿"的权衡。
        /// </summary>
        public const float CustodyRate = 0.03f;

        /// <summary>
        /// 待处理的"临近爆仓"预警。key = <b>持仓对象本身</b>（引用相等）。
        ///
        /// ── 为什么用引用当键（2026-10-21 修 R04）──
        /// 原来用 <c>pos.openTick</c>。但游戏暂停时 <c>GenTicks.TicksGame</c> 不前进，
        /// 玩家可以在暂停里连开好几笔，它们的 openTick 完全相同 —— 于是：
        ///   · 后入队的预警把先入队的**覆盖**掉
        ///   · 任何一笔同 tick 的**安全**仓位走复位分支时，会把危险仓的预警一起删掉
        ///   · 被覆盖的那笔已经置了 criticalWarned，不会重新入队 → 永久漏报
        ///
        /// FxPosition 是引用类型且没有重写 Equals/GetHashCode，
        /// 字典天然按对象身份区分，不需要额外分配 id。
        /// 本字典**不进存档**，改键类型没有任何兼容成本。
        /// </summary>
        public Dictionary<FxPosition, MarginWarning> pendingWarnings
            = new Dictionary<FxPosition, MarginWarning>();

        public FxGameComponent(World world) : base(world)
        {
        }

        /// <summary>
        /// 某张地图对应殖民地的账户。**非玩家殖民地地图一律返回 null。**
        ///
        /// ── 2026-10-23 修「一次蹦出两三封结算信」──
        ///
        /// 原来这里对**任何**地图都会建账户（key = map.Parent.ID），而代码里有两处
        /// 会无差别地向"当前这张图"索要账户：
        ///   · 悬浮窗每帧向 Find.CurrentMap 索要
        ///   · 心情系统每 120 tick 向"殖民者所在地图"索要
        /// 于是**看一眼任务图、或者远行队走进一张伏击图**，就永久多一个账户。
        /// 而账户从来不删（全代码零处 accounts.Remove），结算又遍历**全部**账户 ——
        /// 空账户的当日盈亏恒为 0，永远进不了发信门槛，于是走"平淡日摘要"分支，
        /// 每 3 天各发一封 **盈亏 0 的结算信**。玩家看到的就是
        /// "结算时同时蹦出两三封，其中几封写着 +0"。
        ///
        /// 实测证据：主存档里有 3 个账户 —— 744（Settlement，正常）、
        /// 920 与 904（余额 / 净值 / 持仓 / 历史全空，且世界里已找不到这两个 ID 的对象）。
        ///
        /// 修法分两层：① 这里不再为非殖民地建账户（堵源头，本条）；
        /// ② DailySettlement 里剪掉空账户、只结算还活着的殖民地（清存量）。
        ///
        /// 要"这个殖民地的钱"时用 <see cref="ColonyAccount"/>：拿不到本地账户会
        /// 回退到第一块殖民地，所以远行队、任务图、营地看到的仍是同一份账。
        /// </summary>
        public FxAccount AccountFor(Map map)
        {
            if (map == null || map.Parent == null) return null;
            if (!map.IsPlayerHome) return null;

            int id = map.Parent.ID;
            FxAccount acc;
            if (!accounts.TryGetValue(id, out acc) || acc == null)
            {
                acc = new FxAccount();
                accounts[id] = acc;
            }
            return acc;
        }

        /// <summary>
        /// 殖民地账户：优先用 <paramref name="preferred"/>（若它本身是玩家殖民地），
        /// 否则回退到第一块玩家殖民地。
        ///
        /// 所有"我要看殖民地的钱"的地方都应该用它，而不是直接
        /// <c>AccountFor(某张图)</c> —— 后者在任务图 / 伏击图 / 营地上会返回 null，
        /// 而且早先会为那些图**建出幽灵账户**（见 <see cref="AccountFor"/>）。
        /// </summary>
        public FxAccount ColonyAccount(Map preferred = null)
        {
            FxAccount acc = AccountFor(preferred);
            if (acc != null) return acc;

            var maps = Find.Maps;
            if (maps != null)
            {
                for (int i = 0; i < maps.Count; i++)
                {
                    acc = AccountFor(maps[i]);
                    if (acc != null) return acc;
                }
            }
            return null;
        }

        /// <summary>确保所有已知派系的货币都已生成。惰性调用，避免开局卡顿。</summary>
        /// <summary>
        /// 让行情里"每个真实派系都有一种货币"，并清掉派系已经消失的残留货币。
        ///
        /// ── 2026-10-20：临时派系曾经会漏进来（作者发现）──
        ///
        /// 原实现只跳过了 `f.def.isPlayer` 和 **`f.def.hidden`**，有两个漏洞：
        ///
        ///   1. 判的是【def 的】hidden，不是【实例的】hidden。
        ///      原版 `Faction.Hidden => hidden ?? def.hidden` —— 实例字段优先。
        ///      任务系统造临时派系时走的是 `NewGeneratedFactionWithRelations(..., hidden: true)`，
        ///      也就是把 **实例** hidden 设成 true，而 def（如 TribeCivil）本身并不 hidden。
        ///      于是 `def.hidden` 为 false，这个临时派系就被当成了正常派系。
        ///
        ///   2. 完全没有判 `f.temporary`。
        ///      boss 团、圣物朝圣者、被黑终端这些都是 `faction.temporary = true`，
        ///      任务结束后原版会把它们从派系表里删掉。但我们生成的货币**永远不会被删**，
        ///      于是行情表里留下一行"所属派系已经不存在的货币"。
        ///
        /// 而且本方法**每天结算都会跑一次**，所以这不是"中途加 mod"才有的问题 ——
        /// 任何一次任务生成临时派系，第二天的结算就会给它造一种货币。
        ///
        /// 现在：判 `f.Hidden`（实例优先）与 `f.temporary`，并且顺手清理孤儿货币。
        /// </summary>
        public void EnsureAllFactionCurrencies()
        {
            if (Find.FactionManager == null || market == null) return;
            var all = Find.FactionManager.AllFactionsListForReading;

            // ── ① 补：只给真实派系建货币 ──
            for (int i = 0; i < all.Count; i++)
            {
                Faction f = all[i];
                if (f == null || f.def == null) continue;
                if (f.def.isPlayer) continue;

                // f.Hidden = 实例 hidden ?? def.hidden，比只看 def.hidden 更准
                if (f.Hidden) continue;

                // 任务临时派系：结论上不是真实交易对手，任务结束就会被原版删除
                if (f.temporary) continue;

                // 已灭亡的派系不再建货币（E6 的"名存实亡"会主动删掉它的货币，
                // 这里不加判断的话下一次结算又会被重新建出来）
                if (f.defeated) continue;

                market.EnsureCurrency(f);
            }

            // ── ② 清：删掉幽灵货币（临时派系的、或派系已不存在的）──
            int blocked;
            PruneGhostCurrencies(all, out blocked);
        }

        /// <summary>
        /// 测试用：手动清理全部幽灵货币（设置页按钮）。
        ///
        /// 为什么需要手动按钮：每日结算已经会自动清，但如果玩家**当前存档里
        /// 已经有**一堆旧版本造出来的幽灵货币，他会想在读档后立刻清干净，
        /// 而不是等到下一个结算日。手动按钮就是给这个场景的。
        ///
        /// 返回清掉的条数；blocked 是因仍有持仓而跳过的条数。
        /// </summary>
        public int DebugClearGhostCurrencies(out int blocked)
        {
            blocked = 0;
            if (Find.FactionManager == null) return 0;
            return PruneGhostCurrencies(Find.FactionManager.AllFactionsListForReading, out blocked);
        }

        /// <summary>
        /// 清理幽灵货币，返回删掉的条数（<paramref name="blocked"/> 是因有持仓而跳过的条数）。
        ///
        /// ── 什么叫"幽灵"（2026-10-20 修正判据）──
        /// **所属派系是临时派系（<c>Faction.temporary</c>），或该派系已不存在。**
        ///
        /// 最初的实现只认"派系不在派系表里"，作者实测**完全没有生效**。
        /// 原因是反编译查出来的一句事实：
        ///
        ///     RimWorld 全程序**没有任何地方调用 FactionManager.RemoveFaction**
        ///
        /// 也就是说临时派系（boss 团、圣物朝圣者、被黑终端……）虽然
        /// <c>temporary = true</c>，但**会永远留在派系表里直到存档结束**。
        /// 于是"派系不存在"这个条件永远为假，清理等于空转。
        ///
        /// 所以判据必须直接看 `temporary`：只要它不是真实交易对手，就算幽灵。
        ///
        /// ── 为什么要求"无持仓"──
        /// 持仓只存 currencyFactionLoadID，货币被删之后那个仓位就永远找不到自己的行情，
        /// 会被静默当成 0 盈亏。宁可留着一行幽灵，也不能让玩家的仓位凭空消失。
        /// 这类会被计入 blocked，等玩家平掉仓位后自动清掉（或手动再点一次按钮）。
        ///
        /// 已平仓的交易记录不受影响 —— <see cref="FxTradeRecord"/> 里存的是
        /// currencyName / pairLabel 字符串，不是引用。
        /// </summary>
        private int PruneGhostCurrencies(List<Faction> all, out int blocked)
        {
            blocked = 0;
            if (market == null || market.currencies.Count == 0) return 0;

            // 真实派系（!temporary）的 loadID。不在这个集合里的都是幽灵。
            pruneReal.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                Faction f = all[i];
                if (f == null) continue;
                if (f.temporary) continue;   // 临时派系永远留在表里，但不算真实派系
                pruneReal.Add(f.loadID);
            }

            pruneKeys.Clear();
            foreach (var kv in market.currencies)
            {
                if (pruneReal.Contains(kv.Key)) continue;   // 真实派系，保留
                if (HasAnyPosition(kv.Key)) { blocked++; continue; }
                pruneKeys.Add(kv.Key);
            }

            for (int i = 0; i < pruneKeys.Count; i++)
            {
                FxCurrency c = market.Get(pruneKeys[i]);
                string name = c != null ? c.name : pruneKeys[i].ToString();
                if (market.RemoveCurrency(pruneKeys[i]))
                {
                    Log.Message("[RimForex] 清理幽灵货币「" + name + "」。");
                }
            }

            return pruneKeys.Count;
        }

        /// <summary>是否还有任何账户持有该货币的仓位。</summary>
        private bool HasAnyPosition(int factionLoadID)
        {
            foreach (var kv in accounts)
            {
                List<FxPosition> list = kv.Value != null ? kv.Value.positions : null;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != null && list[i].currencyFactionLoadID == factionLoadID) return true;
                }
            }
            return false;
        }

        /// <summary>清理用的临时容器（避免每天结算都分配新集合）。</summary>
        private readonly HashSet<int> pruneReal = new HashSet<int>();
        private readonly List<int> pruneKeys = new List<int>();

        // ══════════════════════════════════════════════════════════════════
        //  测试专用板块（模组设置里两个按钮）
        //
        //  为什么需要：
        //    · 心情的叠加与过期要跨游戏日才看得出来，正常玩一次要等十几个小时；
        //    · 穿仓要等极端跳空，日概率约 0.1%，正常要玩约 1000 游戏日。
        //  两者都是"必须验证但正常游玩验不到"的路径，所以给测试入口。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>下次强制跳空的 tick。&lt;= 0 表示没有待触发的事件。</summary>
        private int forceGapAtTick = -1;

        /// <summary>排队中的强制跳空种类（1 = 常规，2 = 极端）。</summary>
        private int scheduledGapKind = 2;

        /// <summary>强制跳空的延迟（游戏时）。留足时间去梭哈百倍杠杆做多。</summary>
        public const int ForceGapDelayHours = 3;

        /// <summary>是否已排队一次强制跳空。</summary>
        public bool ForcedGapPending { get { return forceGapAtTick > 0; } }

        /// <summary>距强制跳空还有多少游戏时（未排队则返回 -1）。</summary>
        public float HoursUntilForcedGap
        {
            get
            {
                if (forceGapAtTick <= 0) return -1f;
                return (forceGapAtTick - GenTicks.TicksGame) / (float)TicksPerHour;
            }
        }

        /// <summary>安排一次强制<b>极端</b>跳空（几游戏时后生效）。</summary>
        public void ScheduleForcedGap()
        {
            ScheduleGap(2);
        }

        /// <summary>安排一次强制<b>常规</b>跳空（几游戏时后生效）。</summary>
        public void ScheduleForcedNormalGap()
        {
            ScheduleGap(1);
        }

        private void ScheduleGap(int kind)
        {
            forceGapAtTick = GenTicks.TicksGame + ForceGapDelayHours * TicksPerHour;
            scheduledGapKind = kind;
        }

        /// <summary>到点就触发。由 WorldComponentTick 每帧调用。</summary>
        private void CheckForcedGap()
        {
            if (forceGapAtTick <= 0 || GenTicks.TicksGame < forceGapAtTick) return;
            forceGapAtTick = -1;

            // 播报必须区分种类（2026-10-21 修）：
            // 原来不管排的是哪一档，都播 "RimForex_MsgForceGap"（写着"极端跳空"），
            // 于是按"常规跳空"按钮也会看到"极端跳空"的提示，自相矛盾。
            bool normal = scheduledGapKind == 1;
            if (normal) market.ForceNormalGap();
            else market.ForceExtremeGap();

            Messages.Message(
                (normal ? "RimForex_MsgForceNormalGap" : "RimForex_MsgForceGap").Translate(),
                normal ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.ThreatBig, false);
        }

        /// <summary>
        /// 测试用：往当前地图的账户里塞一条心情。
        /// 多按几次即可叠层，用来验证叠加上限与跨日过期。
        /// </summary>
        public void DebugAddMood(int tier)
        {
            Map m = Find.CurrentMap;
            if (m == null)
            {
                Messages.Message("RimForex_MsgNoMap".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 走 ColonyAccount：测试按钮在任何地图上都该作用到殖民地账户，
            // 而不是因为站在任务图上就静默失效。
            FxAccount acc = ColonyAccount(m);
            if (acc == null) return;
            acc.RecordMoodTier(tier);
        }

        /// <summary>测试用：当前地图的账户（可能为 null）。</summary>
        public FxAccount DebugCurrentAccount
        {
            get
            {
                Map m = Find.CurrentMap;
                return ColonyAccount(m);
            }
        }

        public override void WorldComponentTick()
        {
            if (!initialized)
            {
                initialized = true;
                EnsureAllFactionCurrencies();

                // 建立初始步预算（见 BeginStepBudget）。必须在任何 AdvanceSubSteps 之前，
                // 否则读档后第一帧就会把价格打到 MinRate。
                market.BeginStepBudget();
                // 用绝对时间对齐到整日边界（0 时），而不是"从开局算起的第 N tick"。
                // TicksGame 的 0 点是开局那一刻（可能是一天中的任意时刻），
                // 拿它取模会让"游戏日"错位到开局时刻上。
                nextSettlementTick = (GenTicks.TicksAbs / TicksPerDay + 1) * TicksPerDay;
                lastSampleTick = -1;
                lastStepTick = -1;

                // 首次进入（新开局/首次读档）把结算基准也钉在未来的日边界上，
                // 避免 lastSettlementAbs 停留在 int.MinValue 导致跨过边界就立刻结算。
                if (lastSettlementAbs == int.MinValue)
                {
                    lastSettlementAbs = nextSettlementTick - TicksPerDay;
                }
                // 同理：把"已结算日"钉到上一日，保证不会在读档瞬间补结算。
                if (lastSettledDay == int.MinValue)
                {
                    lastSettledDay = GenTicks.TicksAbs / TicksPerDay;
                }
            }

            int now = GenTicks.TicksGame;
            // 强制跳空的倒计时（与报价节奏无关，所以放在采样判定之外）
            CheckForcedGap();

            // 0.3.0：延迟事件与贷款利息（都放在采样判定之外，暂停时也走）
            ProcessPendingEvents();
            ProcessLoans();
            ProcessWeakenedSettlements();
            ExpireTradeBonus();
            ProcessWarNews();
            ProcessRetaliation();
            ProcessDebtCase();

            if (now != lastSampleTick)
            {
                lastSampleTick = now;

                // ── 推进汇率：步边界 + 步内子步 ──
                //
                // 分两层：
                //   1) 步边界（每 TicksPerStep）→ TickStep()：抽跳空、重置 subStep。
                //   2) 步内子步（每 TicksPerStep/SubSteps）→ AdvanceSubSteps()：
                //      推进价格并让 K 线记下路径点，影线才有真实的信息量。
                //
                // 两层都用"按进度补齐"而不是 tick 取模：
                // 取模在高速档会漏步（一帧跨过多步时只有恰好落在边界的那一步生效），
                // 而 TicksPerStep 长达 600 tick，漏步会非常明显；低帧率同理会漏子步，
                // 让 K 线出现"空洞"。
                if (lastStepTick < 0) lastStepTick = now;

                // 上限 8 步防止调试用的一帧跳 2000 tick 时卡顿。
                // 子步随步长缩放（8 步 × 16 子步 = 128），不会成为瓶颈。
                int steps = 0;
                while (now - lastStepTick >= FxMarket.TicksPerStep && steps < 8)
                {
                    // 先把本步剩余的 tick 走完，让价格到达真正的步边界，
                    // 这样 K 线的步边界价格才与"单步一次算完"逐点相等。
                    market.AdvanceSubSteps(FxMarket.SubSteps, lastStepTick);

                    market.TickStep(lastStepTick);
                    lastStepTick += FxMarket.TicksPerStep;
                    steps++;
                }

                // ── 万一积压超过 8 步：直接对齐，避免永远追不上 ──
                //
                // 代价是**这一帧的行情被丢弃**：那几步的跳空、波动、K 线全都没发生，
                // 价格直接跳到当前位置。正常情况下 8 步要 ≥4.4 秒的卡顿才会触发，
                // 但 300+ mod 的存档在加载或战斗时完全可能卡这么久。
                //
                // 2026-10-20：原来这里是**完全静默**的，出问题时无法与"模型抖动"区分。
                // 现在至少留一条 Log.Warning，并把丢了多少步写清楚。
                // 用 WarningOnce 是因为警告键固定 —— 同一类事件只刷一条，不淹没日志。
                if (now - lastStepTick >= FxMarket.TicksPerStep)
                {
                    int dropped = (now - lastStepTick) / FxMarket.TicksPerStep;
                    lastStepTick = now;
                    Log.WarningOnce(
                        "[RimForex] 行情积压超过单帧上限，丢弃了约 " + dropped
                        + " 个报价步（该段 K 线与跳空不会出现）。"
                        + "常见原因：游戏卡顿、超高倍速，或 mod 过多导致的低帧率。",
                        0x52465801);
                }

                // 当前步内还欠多少子步
                int doneTicks = now - lastStepTick;
                if (doneTicks < 0) doneTicks = 0;
                if (doneTicks > FxMarket.TicksPerStep) doneTicks = FxMarket.TicksPerStep;
                int wantSub = doneTicks * FxMarket.SubSteps / FxMarket.TicksPerStep;
                market.AdvanceSubSteps(wantSub, lastStepTick);

                // 跳空提示：幅度够大才通知，否则每次跳空都刷屏
                if (steps > 0) AnnounceGaps();

                // 实时估值都挂在每个模拟 tick 上：
                // 持仓在汇率变动的瞬间就重估，浮盈浮亏与爆仓都是实时的，不等每日结算。
                //
                // K 线采样不在这里 —— 它已经随每个子步在 AdvanceSubSteps 内部完成，
                // 这样影线才有步内路径的信息量。
                MarkToMarket();
                CheckMarginCalls();
            }

            // ── 每日结算：只在整日边界（0 时）触发 ──
            //
            // 判据是"当前 TicksAbs 恰好落在整日边界上"（abs % GenDate.TicksPerDay == 0），
            // 而不是"越过了 nextSettlementTick 就补一次"。
            //
            // 早期的 `if (abs < nextSettlementTick) return;` 是"越过即触发"：
            // 只要 nextSettlementTick 因为任何原因落后（读档残留、调试加速、
            // 一帧跨过多个边界），结算就会在<b>非午夜时刻</b>补上一次，
            // 表现就是"事件跳出的时间不对"。
            //
            // 现在改成边界判定 + 按日去重，保证：
            //   1. 结算只可能发生在 0 时（游戏内时钟的午夜）
            //   2. 每个游戏日最多结算一次（lastSettledDay 记录已结算的日子）
            int abs = GenTicks.TicksAbs;
            if (abs % TicksPerDay != 0) return;

            int day = abs / TicksPerDay;
            if (day == lastSettledDay) return;      // 这一天已经结算过

            lastSettledDay = day;
            lastSettlementAbs = abs;
            nextSettlementTick = (day + 1) * TicksPerDay;
            DailySettlement();
        }

        /// <summary>
        /// 已经结算过的游戏日序号（abs / TicksPerDay）。
        /// 用来保证"每个游戏日只在 0 时结算一次" —— 比比较 tick 差更直接。
        /// </summary>
        private int lastSettledDay = int.MinValue;

        private int lastSampleTick = -1;
        /// <summary>上一次汇率推进所在的 tick。-1 = 尚未初始化。</summary>
        private int lastStepTick = -1;

        /// <summary>
        /// 距下次报价还有多少 tick。
        ///
        /// 必须与推进逻辑（<c>now - lastStepTick >= TicksPerStep</c>）同源，
        /// 不能用 <c>now % TicksPerStep</c> —— 那是从 tick 0 起算的相位，
        /// 与 lastStepTick 的相位不一致，会导致 K 线在倒计时归零之前就变化。
        /// </summary>
        public int TicksUntilQuote
        {
            get
            {
                if (lastStepTick < 0) return 0;
                int d = lastStepTick + FxMarket.TicksPerStep - GenTicks.TicksGame;
                return d < 0 ? 0 : d;
            }
        }

        /// <summary>
        /// 某个 tick 落在第几个报价步里（0 = 尚未有过报价）。
        ///
        /// 用于"最小持仓"判定：开仓与平仓只要落在同一个报价步内，
        /// 就必须拒绝 —— 否则玩家可以在一次报价的窗口内做无数次瞬时往返。
        /// 注意这里同样必须基于 lastStepTick 而不是 tick 取模，理由见 TicksUntilQuote。
        /// </summary>
        public int QuoteStepOf(int tick)
        {
            if (lastStepTick < 0) return 0;
            int steps = (tick - lastStepTick) / FxMarket.TicksPerStep;
            return steps < 0 ? 0 : steps;
        }

        /// <summary>当前报价步序号。</summary>
        public int CurrentQuoteStep => QuoteStepOf(GenTicks.TicksGame);

        /// <summary>
        /// 持仓是否已经跨过至少一次报价。未跨过则不允许平仓。
        ///
        /// 判据是<b>开仓时一次性确定的绝对 tick 阈值</b>，而不是"滚动比较"。
        /// 早期用的是 `QuoteStepOf(openTick) &lt; QuoteStepOf(now)`，
        /// 问题在于 QuoteStepOf(openTick) 会随着 lastStepTick 前进而变成负数，
        /// 而 QuoteStepOf(now) 在两次报价之间恒为 0 ——
        /// 于是这个布尔值在每处理一次报价的瞬间都会翻转一次，
        /// 表现为"平仓按钮每隔几秒就重新变灰、过一会儿才又能按"。
        /// 绝对阈值只会在越过时变一次 true，不会来回翻。
        /// </summary>
        public bool CanClose(FxPosition pos)
        {
            if (pos == null) return false;
            // closableAtTick == 0 说明是旧存档里的持仓（该字段尚不存在），直接放行，
            // 免得读档后老仓位被永久锁住。
            if (pos.closableAtTick <= 0) return true;
            return GenTicks.TicksGame >= pos.closableAtTick;
        }

        /// <summary>
        /// 玩家主动平仓。**终端窗口与悬浮窗共用这一套记账**（2026-10-23 下沉）。
        ///
        /// 原来这段逻辑在 `Window_FxTerminal.ClosePosition` 里。悬浮窗要加平仓按钮时，
        /// 如果照抄一份，两处只要有一处改漏（手续费、点差成本、穿仓时不钳零、心情档、
        /// 交易历史字段……）就会出现"从终端平"和"从 HUD 平"结果不一样。
        /// 所以整段搬到这里，窗口那边只剩一行转发。
        /// </summary>
        public void ClosePositionFromUi(FxAccount acc, FxPosition pos)
        {
            if (acc == null || pos == null) return;

            // ---- 同一报价步内开仓又平仓 = 交易作废 ----
            // 覆盖两种情况：
            //   1. 游戏暂停时 TicksGame 不前进，所有操作落在同一个 tick
            //   2. 时间流动但还没跨过下一次报价 —— 价格未变，瞬时往返没有依据
            // 真实平台对超短线往返也有惩罚；这里直接作废并全额退款，
            // 让它不可能成为任何形式刷钱循环的一环。
            if (!CanClose(pos))
            {
                acc.balance += pos.margin + pos.openFee;
                acc.feesSinceSettle -= pos.openFee;   // 全额退款，手续费要回冲
                acc.positions.Remove(pos);
                pendingWarnings.Remove(pos);

                Messages.Message("RimForex_MsgTooSoon".Translate(pos.currencyName),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            float equity = pos.Equity;
            // 平仓成交价：做多卖出吃 bid、做空买回吃 ask，MarkToMarket 已把它写在 markRate 里
            // 平仓即记心情档（按本笔盈亏）。改成实时记录而不是等每日结算，
            // 是因为玩家在意的是"这一笔亏得有多惨"，一天结算一次体现不出来。
            acc.RecordCloseMood(pos.Pnl);

            float exitPrice = pos.markRate > 0f
                ? pos.markRate
                : market.ExitPriceOf(pos.currencyFactionLoadID, pos.isLong);
            float notional = Mathf.Abs(pos.units) * exitPrice;
            float fee = FeeFor(notional);

            acc.balance += equity - fee;
            acc.feesSinceSettle += fee;   // 结算信要把手续费摊开给玩家看
            // 不钳到 0：主动平仓也可能在跳空后产生负余额（穿仓），需要玩家补银填平。
            acc.positions.Remove(pos);
            pendingWarnings.Remove(pos);

            acc.RecordTrade(new FxTradeRecord
            {
                currencyName = pos.currencyName,
                pairLabel = pos.symbol,
                isLong = pos.isLong,
                units = pos.units,
                entryRate = pos.entryRate,
                exitRate = exitPrice,
                // 保证金按"原始投入 + 追加"记账，历史的"保证金"列才有意义
                margin = pos.margin,
                // 必须用 pos.Pnl（已按方向翻转），不能写 equity - margin：
                // equity = margin + Pnl，所以 equity - margin 只在没追加过保证金时等于 Pnl，
                // 追加过就会把 addedMargin 也算进盈亏里（曾经导致"做空涨了反而盈利"）。
                pnl = pos.Pnl,
                totalFee = pos.openFee + fee,
                // 点差成本 = 开仓时记录的每单位点差 × 数量。
                // 不能用 (entry - exit) 算，那里面混着行情波动，是盈亏而不是成本。
                spreadCost = Mathf.Abs(pos.units) * pos.spreadAtOpen,
                spreadRateAtClose = market.SpreadRateFor(pos.currencyFactionLoadID),
                closedTick = GenTicks.TicksGame,
                wasLiquidation = false
            });

            Messages.Message("RimForex_MsgClosed".Translate(
                pos.currencyName, pos.Pnl.ToString("F0"), fee.ToString("F2")),
                MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>
        /// 距离可以平仓还差多少 tick（0 = 现在就能平）。
        /// 用于按钮上的实时倒计时，避免玩家以为按钮坏了。
        /// </summary>
        public int TicksUntilClosable(FxPosition pos)
        {
            if (pos == null) return 0;
            int d = pos.closableAtTick - GenTicks.TicksGame;
            return d < 0 ? 0 : d;
        }

        /// <summary>上一次每日结算发生的绝对 tick。用于保证"每游戏日最多一次"。</summary>
        public int lastSettlementAbs = int.MinValue;

        /// <summary>距下次每日结算还有多少 tick（供 UI 显示，便于验证周期是否正确）。</summary>
        public int TicksUntilSettlement
        {
            get
            {
                int d = nextSettlementTick - GenTicks.TicksAbs;
                return d < 0 ? 0 : d;
            }
        }

        /// <summary>
        /// 距下次结算的整游戏小时数。
        ///
        /// ── 为什么直接用 GenDate.HourOfDay，而不是自己算 ──
        /// 结算点恒在整日边界（TicksAbs 是 GenDate.TicksPerDay 的整数倍），而原版时钟显示的
        /// 就是 TicksAbs 里的"当天第几小时"。所以
        ///     距结算小时数 = 24 - 当前小时
        /// 是**恒等式**，不可能与游戏内时钟脱节。
        ///
        /// 早期版本用 `(nextSettlementTick - TicksAbs) / TicksPerHour` 自己算，
        /// 中间还错用过 TicksPerStep 当小时。虽然那个常量已修，但"自己算"这条路
        /// 本身就脆弱：任何一处 tick 基准理解偏差都会让显示与时钟不一致。
        /// 现在改成完全依赖原版 API。
        /// </summary>
        public int HoursUntilSettlement
        {
            get
            {
                int h = GenDate.HourOfDay(GenTicks.TicksAbs, 0f);
                if (h < 0) h = 0;
                if (h > 23) h = 23;
                return 24 - h;
            }
        }

        /// <summary>
        /// 距下次结算还有多少 tick。同样基于 GenDate，保证与时钟一致。
        /// </summary>
        public int TicksUntilSettlementExact
        {
            get
            {
                int h = GenDate.HourOfDay(GenTicks.TicksAbs, 0f);
                return (24 - h) * TicksPerHour;
            }
        }

        /// <summary>
        /// 实时盯市：把每个持仓的 markRate 同步为"按本仓方向现在平仓能拿到的价"。
        /// 做多看 bid、做空看 ask —— 因为 Pnl/Equity 都是按 markRate 现算的，
        /// 这一步做完后 UI 上的浮盈浮亏、账户净值、爆仓判定全部立即反映当前行情，
        /// 而且<b>已经把点差算进去了</b>（这才是真正能落袋的价格）。
        ///
        /// 顺带把旧存档（点差引入之前、用中间价记录）的持仓折算成新口径。
        /// </summary>
        public void MarkToMarket()
        {
            var keys = new List<int>(accounts.Keys);
            for (int k = 0; k < keys.Count; k++)
            {
                var acc = accounts[keys[k]];
                if (acc == null) continue;

                for (int i = 0; i < acc.positions.Count; i++)
                {
                    var pos = acc.positions[i];
                    var cur = market.Get(pos.currencyFactionLoadID);
                    if (cur == null || cur.rate <= 0f) continue;

                    if (!pos.entryHasSpread) MigrateLegacyPricing(pos, cur);

                    // 锁定期内不加价差：盈亏保持为 0，直到跨过一次报价。
                    //
                    // 为什么需要这个：开仓价是 ask、平仓价是 bid，两者天生差一个点差。
                    // 如果开仓瞬间就按 bid 估值，盈亏会立刻显示 -点差，
                    // 而保证金健康度 = (保证金 - 点差) / 保证金 ≈ 0.94 ——
                    // 这会让"一开仓就报警"，甚至在极高杠杆下触发不必要的强平。
                    // 这与现实也一致：平台给的是 0 点差的入场价，点差成本在建仓完成后才体现。
                    //
                    // 解锁时刻由 closableAtTick 决定（开仓时一次算好），
                    // 所以这段冻结期和"平仓按钮解锁"是同一个时间点，不会互相矛盾。
                    bool locked = pos.closableAtTick > 0 && GenTicks.TicksGame < pos.closableAtTick;
                    pos.markRate = locked
                        ? pos.entryRate
                        : market.ExitPriceOf(pos.currencyFactionLoadID, pos.isLong);
                }
            }
        }

        /// <summary>
        /// 把点差引入之前建立的持仓折算到新口径：
        /// 旧口径下 entryRate 是中间价，现在补上开仓方向那一半点差；
        /// markRate 会在调用处被重算，所以这里只处理开仓价。
        /// </summary>
        private void MigrateLegacyPricing(FxPosition pos, FxCurrency cur)
        {
            float rate = FxMarket.SpreadRateFor(cur);
            float half = rate * 0.5f;
            pos.entryRate = pos.isLong
                ? pos.entryRate * (1f + half)   // 做多当初应付 ask
                : pos.entryRate * (1f - half);  // 做空当初应收 bid
            pos.entrySpreadRate = rate;         // 同时补上点差率，入场成本才算得出来
            if (pos.entryMid <= 0f) pos.entryMid = pos.entryRate;  // 旧存档没有中间价，用成交价近似
            pos.entryHasSpread = true;
        }

        /// <summary>
        /// 播报本步发生的行情跳空。
        ///
        /// ── 阈值必须按 σ 相对量算，不能用固定的 0.5%（2026-10-21 修）──
        /// 原来写死 0.5%，而常规跳空的幅度是 3σ ~ 9σ，σ 随波动率缩放：
        ///
        ///     档位      3σ        9σ        能过 0.5% 吗
        ///     太空      0.081%    0.242%    永远不能
        ///     工业      0.122%    0.367%    永远不能
        ///     中世纪    0.173%    0.519%    只有最顶端
        ///     部落      0.235%    0.706%    部分
        ///     袭击者    0.276%    0.830%    部分
        ///
        /// 于是低波动品种的常规跳空**从来不会被播报**，作者一度以为
        /// "自己从没遇到过普通跳空"。而且滑块只调概率、不调幅度 ——
        /// 拉满也只是更频繁，幅度仍是 3σ~9σ。
        ///
        /// 现在改成：幅度 ≥ 6σ 就播报（下限 0.15% 防止低波动品种刷屏）。
        /// 各档位都能感知到跳空，且不会把消息淹没。
        /// </summary>
        private void AnnounceGaps()
        {
            // 6σ 是"肉眼可辨"与"不刷屏"之间的折中：
            // 常规跳空 3σ~9σ 里约一半能过线，极端跳空 14σ~34σ 必然过线。
            const float AnnounceSigmas = 6f;
            // 下限：低波动品种 6σ 只有 0.16%，再低就会频繁播报
            const float AnnounceFloorPct = 0.15f;

            foreach (FxCurrency c in market.AllCurrencies)
            {
                if (c == null) continue;

                float vol = c.volatility > 0.01f ? c.volatility : 1f;
                float sigmaPct = FxMarket.DailyVol * vol / Mathf.Sqrt(FxMarket.StepsPerDay) * 100f;
                float threshold = Mathf.Max(AnnounceFloorPct, AnnounceSigmas * sigmaPct);

                if (System.Math.Abs(c.lastGapPct) < threshold) continue;

                Messages.Message(
                    "RimForex_MsgGap".Translate(c.PairLabel, c.lastGapPct.ToString("+0.0;-0.0")),
                    MessageTypeDefOf.CautionInput, false);
            }
        }

        /// <summary>
        /// 实时强平检查。行情一变就要查，否则 100 倍杠杆下会等到第二天才处理已经穿仓的仓位。
        /// 返回本次强平的持仓数。
        ///
        /// 强平线遵循真实海外 FX 平台的 Stop-Out Level 规则：
        /// 当<b>净值跌到已用保证金的 StopOutFraction 倍</b>时即强制平仓，
        /// 剩余净值退还账户，而不是等保证金全部亏光。
        /// 详见 FxGameComponent.StopOutFraction 的说明。
        /// </summary>
        public int CheckMarginCalls()
        {
            int closed = 0;
            var keys = new List<int>(accounts.Keys);
            for (int k = 0; k < keys.Count; k++)
            {
                int accKey = keys[k];
                var acc = accounts[accKey];
                if (acc == null) continue;

                for (int i = acc.positions.Count - 1; i >= 0; i--)
                {
                    var pos = acc.positions[i];
                    float eq = pos.Equity;

                    // ── 强平线必须与健康度同一标尺（2026-10-21 修 R05）──
                    // 用【初始保证金】而不是 margin：追加保证金会抬高 margin，
                    // 若拿它当基准，玩家在界面显示"健康度 60%、还没到预警线"时
                    // 就已经被强平了。见 FxPosition.BaseMargin 的说明。
                    // 兜底：万一 addedMargin 被写坏成 ≥ margin，退回用 margin，
                    // 宁可少平一次，也不能让强平线掉到 0 而永远不平仓。
                    float baseM = pos.BaseMargin;
                    if (baseM < 0.01f) baseM = pos.margin;
                    float stopOutLevel = baseM * StopOutFraction;

                    // 还没触及强平线：只有确实处于亏损状态才考虑预警
                    if (eq > stopOutLevel)
                    {
                        MaybeWarn(pos, acc);
                        continue;
                    }

                    // 强平：退还剩余净值。
                    //
                    // ── 穿仓（负余额）──
                    // eq 有可能为负 —— 行情跳空会一步越过强平线，让浮亏直接超过保证金。
                    // 此时不足的部分就是"穿仓亏损"，账户余额会被推成负数。
                    // 这不是 bug，而是本模组刻意保留的真实风险：
                    // 玩家必须往终端里补白银把负余额填平，否则无法开新仓、也无法提款。
                    //
                    // 早期版本在这里把余额钳到 0（平台兜底），现在不再钳。
                    //
                    // ── 强平同样要收平仓手续费（2026-10-16 修）──
                    // 早先这里只写 `refund = eq`、`totalFee = pos.openFee`，
                    // 等于【免掉平仓费】—— 被强平反而比主动平仓更便宜，方向完全反了。
                    // 实测 4 笔强平单的手续费恰好等于开仓费（65.75 / 138.50 / 321.72 / 547.14），
                    // 合计漏收约 1,289 白银，而且 totalFee 混了两种口径、报告不可信。
                    //
                    // 口径与手动平仓（ClosePosition）保持一致：
                    //   平仓价取 markRate（已是按方向的可成交价）
                    //   手续费按下单名义计收，下限 MinFee
                    //   实际到账 = 净值 − 平仓费
                    float exitPrice = pos.markRate > 0f
                        ? pos.markRate
                        : market.ExitPriceOf(pos.currencyFactionLoadID, pos.isLong);
                    float closeFee = FeeFor(Mathf.Abs(pos.units) * exitPrice);
                    float refund = eq - closeFee;
                    acc.balance += refund;
                    acc.feesSinceSettle += closeFee;
                    // 注意：穿仓判据用【扣费后的余额】，因为那才是账户的真实状态
                    bool blewUp = acc.balance < 0f;
                    acc.positions.RemoveAt(i);
                    pendingWarnings.Remove(pos);
                    // 爆仓/穿仓各记各的档。刻意【不】再记一次普通亏损档 ——
                    // 一次事故吃掉两个坑太狠，而且爆仓本身就是那笔亏损。
                    //
                    // 穿仓（余额被推成负数）比爆仓更重，单独一档：它意味着
                    // 亏光保证金之外还倒欠平台钱，心情也更狠（-28 对 -20）。
                    // 记录时会自动顶掉爆仓那条（见 SupersededBy），不会叠加。
                    acc.RecordMoodTier(blewUp
                        ? FxMoodSystem.TierBlowUp
                        : FxMoodSystem.TierMarginCall);
                    closed++;

                    acc.RecordTrade(new FxTradeRecord
                    {
                        currencyName = pos.currencyName,
                        pairLabel = pos.symbol,
                        isLong = pos.isLong,
                        units = pos.units,
                        entryRate = pos.entryRate,
                        exitRate = pos.markRate,
                        margin = pos.margin,
                        // 同样必须用 pos.Pnl。曾经这里是 eq（= margin + Pnl），
                        // 会把整笔保证金当成盈利记进历史。
                        pnl = pos.Pnl,
                        // 开仓费 + 平仓费，与手动平仓同口径
                        totalFee = pos.openFee + closeFee,
                        spreadCost = System.Math.Abs(pos.units) * pos.spreadAtOpen,
                        spreadRateAtClose = market.SpreadRateFor(pos.currencyFactionLoadID),
                        closedTick = GenTicks.TicksGame,
                        wasLiquidation = true,
                        wasBlowUp = blewUp
                    });

                    // 穿仓用单独的信件，语气与普通强平区分开
                    if (blewUp)
                    {
                        Find.LetterStack.ReceiveLetter(
                            "RimForex_LetterBlowUp".Translate(),
                            "RimForex_LetterBlowUpText".Translate(
                                eq.ToString("F0"),
                                acc.balance.ToString("F0"),
                                closeFee.ToString("F0")),
                            LetterDefOf.ThreatBig);
                    }
                    else
                    {
                        Find.LetterStack.ReceiveLetter(
                            "RimForex_LetterMarginCall".Translate(),
                            "RimForex_LetterMarginCallText".Translate(
                                refund.ToString("F0"),
                                closeFee.ToString("F0"),
                                acc.balance.ToString("F0")),
                            LetterDefOf.NegativeEvent);
                    }
                }
            }
            return closed;
        }

        /// <summary>
        /// 单一阈值预警：健康度跌破 <see cref="CriticalThreshold"/>（浮亏 40%）时
        /// 强制暂停游戏并弹出预警窗口，每个持仓只弹一次，直到反弹回安全区才复位。
        ///
        /// 关键点：暂停必须在这里做（WorldComponentTick 内），而不是等到终端窗口绘制时 ——
        /// 否则终端没开就永远不会暂停，等到玩家发现时仓位已经爆了。
        ///
        /// ── 为什么去掉了"多档复燃" ──
        /// 早期版本是这样：0.80 起提醒一次，之后健康度每跌到"上次水位 × 0.6"就再提醒一次。
        /// 结果是 0.80 → 0.76 → 0.75 → … 一路弹七八次，玩家感受就是"掉到 76 弹一次、
        /// 掉到 75 又弹一次"。这属于**用连续机制表达离散意图**的错误 ——
        /// 玩家真正需要的信息只有一条："快被强平了"。所以现在就是一个阈值、一次弹窗。
        /// </summary>
        private void MaybeWarn(FxPosition pos, FxAccount acc)
        {
            float health = pos.MarginHealth;

            // 只在确实处于亏损状态时才预警。
            // （锁定期内盈亏恒为 0，因此开仓阶段不可能触发预警。）
            if (pos.Pnl >= 0f)
            {
                pendingWarnings.Remove(pos);
                pos.ResetWarnState();
                return;
            }

            // ── 复位：必须回到【警告线 + 滞回带】以上（2026-10-21）──
            // 只判 health > CriticalThreshold 的话，健康度在 0.45 附近摆动时
            // 会"复位→提醒→复位→提醒"无限循环。详见 WarnHysteresis 的说明。
            if (health > CriticalThreshold + WarnHysteresis)
            {
                pendingWarnings.Remove(pos);
                pos.ResetWarnState();
                return;
            }

            // 落在滞回带内（0.45 ~ 0.55）：既不算脱离危险，也还没真正跌破。
            // 什么都不做 —— 不复位、也不提醒。
            if (health > CriticalThreshold) return;

            // 已经提醒过就不再弹，直到上面把它复位
            if (pos.criticalWarned) return;

            // 冷却：同一仓位两次预警至少隔一个游戏小时。
            // 与滞回带互补 —— 滞回带管"同一段下跌内的抖动"，
            // 冷却管"来回穿越"（弹一次 → 回到安全区 → 很快又跌回来）。
            if (pos.warnedAtTick > 0
                && GenTicks.TicksGame - pos.warnedAtTick < WarnCooldownTicks)
            {
                return;
            }

            pos.criticalWarned = true;
            pos.warnedHealth = health;
            pos.warnedAtTick = GenTicks.TicksGame;
            EnqueueWarning(pos, acc);
        }

        private void EnqueueWarning(FxPosition pos, FxAccount acc)
        {
            // 这里曾经先用一个循环在 Find.Maps 里找出 accKey 对应的地图、
            // 存进 MarginWarning.map —— 但那个字段**从来没有人读过**：
            // 预警窗只用到 position 与 account，弹窗位置由 WindowStack 决定。
            // 于是每次预警都白跑一遍全地图扫描。字段与循环一并删除。
            pendingWarnings[pos] = new MarginWarning
            {
                account = acc,
                position = pos
            };
        }

        /// <summary>
        /// 把排队的预警变成可见的界面。放在 WorldComponentOnGUI 而不是终端窗口里，
        /// 这样即使玩家没打开交易终端，临近爆仓也会被通知到。
        /// </summary>
        public override void WorldComponentOnGUI()
        {
            if (Current.ProgramState != ProgramState.Playing) return;

            // 屏幕角落的浮动盈亏窗（2026-10-20）。
            // 放在这里而不是终端窗口里，是为了让玩家关掉终端干别的时也能看到。
            // 它自己会处理"终端开着就隐藏"和"没持仓就不画"。
            Map hudMap = Find.CurrentMap;
            if (hudMap != null)
            {
                // ColonyAccount：在任务图 / 伏击图上也能看到殖民地的浮动盈亏，
                // 而不是为那张图新建一个空账户（幽灵账户的源头）。
                FxHud.DrawFloatingPnl(ColonyAccount(hudMap), hudMap);
            }

            if (pendingWarnings.Count == 0) return;
            if (Find.WindowStack == null) return;
            if (Find.WindowStack.IsOpen(typeof(Window_MarginCall))) return;

            FxPosition key = null;
            MarginWarning picked = null;
            foreach (var kv in pendingWarnings)
            {
                var w = kv.Value;
                if (w == null || w.position == null) continue;
                if (picked == null) { key = kv.Key; picked = w; }
            }
            if (picked == null) return;

            Find.WindowStack.Add(new Window_MarginCall(this, key));
        }

        // ══════════════════════════════════════════════════════════════
        //  0.3.0 世界事件：调度器 + 阈值工具
        // ══════════════════════════════════════════════════════════════

        /// <summary>全局冷却是否已过（两次世界事件至少间隔 5 游戏日）。</summary>
        public bool WorldEventAllowed()
        {
            return GenTicks.TicksGame - lastWorldEventTick >= GlobalEventCooldown;
        }

        /// <summary>把一条事件排进队列，延迟 <paramref name="delayTicks"/> 后触发。</summary>
        public void QueueEvent(FxPendingKind kind, int factionID, int currencyFaction,
            int delayTicks, int payload = 0)
        {
            if (delayTicks < 0) delayTicks = 0;
            if (pendingEvents == null) pendingEvents = new List<FxPendingEvent>();
            pendingEvents.Add(new FxPendingEvent
            {
                kind = kind,
                factionLoadID = factionID,
                currencyFaction = currencyFaction,
                dueTick = GenTicks.TicksGame + delayTicks,
                payload = payload
            });
        }

        private void ProcessPendingEvents()
        {
            if (pendingEvents == null || pendingEvents.Count == 0) return;
            int now = GenTicks.TicksGame;
            for (int i = pendingEvents.Count - 1; i >= 0; i--)
            {
                var e = pendingEvents[i];
                if (e == null) { pendingEvents.RemoveAt(i); continue; }
                if (now < e.dueTick) continue;
                pendingEvents.RemoveAt(i);
                DispatchPendingEvent(e);
            }
        }

        /// <summary>
        /// 事件到期后的执行点。
        ///
        /// preview1 阶段这里是空的 —— 调度器先建好，E1/E2/E6/E7 在后续 preview 里
        /// 各自往对应 case 里填实现。
        /// </summary>
        private void DispatchPendingEvent(FxPendingEvent e)
        {
            // 世界事件关掉后，连已经排队的也不执行（作者 2026-10-25 反馈：
            // 关了开关商队仍然到来）。同时清掉队列，避免关闭期间越积越多。
            RimForexSettings cfgEvt = RimForexMod.Settings;
            if (cfgEvt != null && !cfgEvt.enableWorldEvents)
            {
                pendingEvents.Clear();
                return;
            }

            var faction = FactionByID(e.factionLoadID);
            if (faction == null) return;

            switch (e.kind)
            {
                case FxPendingKind.CaravanArrive:
                case FxPendingKind.OrbitalArrive:
                {
                    // 两条路都试：陆路商队 → 轨道贸易船。
                    // 哪条成功就用哪条的文案，绝不出现"信里写商队、结果来了贸易船"。
                    if (!DeliverTradeVisit(faction, e.payload == 1))
                    {
                        Messages.Message("RimForex_EvNoTrader".Translate(faction.Name),
                            MessageTypeDefOf.NeutralEvent, false);
                    }
                    break;
                }

                case FxPendingKind.RaidChance:
                {
                    // E1-B：3 天后掷骰，30% 来一次点数更高的袭击
                    if (Rand.Chance(E1RaidChance))
                    {
                        Map map = Find.AnyPlayerHomeMap;
                        float pts = map != null
                            ? Mathf.Max(StorytellerUtility.DefaultThreatPointsNow(map) * 1.3f, 2000f)
                            : 2000f;
                        if (TryFireRaid(faction, pts))
                        {
                            Find.LetterStack.ReceiveLetter(
                                "RimForex_Ev1bRaidLabel".Translate(faction.Name),
                                "RimForex_Ev1bRaid".Translate(faction.Name),
                                LetterDefOf.ThreatBig);
                            break;
                        }
                        // 掷骰中了却发不出去：明确告诉玩家"他们来了但被打道回府"，
                        // 而不是假装什么都没发生（否则测试时完全看不出区别）。
                        Messages.Message("RimForex_Ev1bRaidBlocked".Translate(faction.Name),
                            MessageTypeDefOf.NeutralEvent, false);
                        break;
                    }
                    Messages.Message("RimForex_Ev1bQuiet".Translate(faction.Name),
                        MessageTypeDefOf.NeutralEvent, false);
                    break;
                }

                case FxPendingKind.CrisisDefeat:
                {
                    DefeatFaction(faction);
                    for (int i = retaliations.Count - 1; i >= 0; i--)
                    {
                        if (retaliations[i] != null && retaliations[i].factionLoadID == faction.loadID)
                        {
                            retaliations.RemoveAt(i);
                        }
                    }
                    Find.WindowStack.Add(new Dialog_MessageBox(
                        "RimForex_Ev6Defeat".Translate(faction.Name)));
                    break;
                }

                case FxPendingKind.TributeArrive:
                {
                    if (TryFireTributeCollector())
                    {
                        Find.LetterStack.ReceiveLetter(
                            "RimForex_EvTributeArrivedLabel".Translate(faction.Name),
                            "RimForex_EvTributeArrived".Translate(faction.Name),
                            LetterDefOf.NeutralEvent);
                    }
                    else
                    {
                        Messages.Message("RimForex_EvTributeNoShow".Translate(faction.Name),
                            MessageTypeDefOf.NeutralEvent, false);
                    }
                    break;
                }

                case FxPendingKind.RetaliationRaid:
                    break;
                case FxPendingKind.DebtDeadline:
                    break;
            }
        }

        /// <summary>按 loadID 找派系（找不到返回 null）。</summary>
        public Faction FactionByID(int loadID)
        {
            var all = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                var f = all[i];
                if (f != null && f.loadID == loadID) return f;
            }
            return null;
        }

        /// <summary>发起一次袭击。成功返回 true。</summary>
        public bool TryFireRaid(Faction f, float points)
        {
            if (f == null) return false;
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return false;

            if (points <= 0f) points = StorytellerUtility.DefaultThreatPointsNow(map);
            var parms = new IncidentParms
            {
                target = map,
                faction = f,
                points = points
            };
            return Find.Storyteller.TryFire(new FiringIncident(IncidentDefOf.RaidEnemy, null, parms));
        }

        /// <summary>
        /// 这个派系现在能不能派商队。
        ///
        /// ── 为什么要先问（2026-10-25 作者实测）──
        /// E2 连点三次都是"商队未能成行"。原因是随机挑到的派系**没有商队可派**：
        /// 已灭亡的派系、或 caravanTraderKinds 为空的派系，原版的
        /// IncidentWorker_TraderCaravanArrival.CanFireNow 一律返回 false：
        ///     !FactionCanBeGroupSource(...) || f.def.caravanTraderKinds.Count == 0
        /// 这种情况下发信说"商队两天后到"就是在骗玩家，所以要在**挑事件时**先筛掉。
        /// </summary>
        public static bool CanSendCaravan(Faction f)
        {
            if (f == null || f.IsPlayer || f.temporary || f.defeated) return false;
            if (f.HostileTo(Faction.OfPlayer)) return false;

            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return false;

            // ── 用原版那道完整的门，而不是自己拼条件 ──
            //
            // IncidentWorker_TraderCaravanArrival.FactionCanBeGroupSource 就是
            // TryResolveParms 实际调用的判据，包含：
            //   · 基础：非玩家 / 未灭亡 / 非临时 / 气温区间 / 地形层白黑名单
            //   · 中性组：非 Hidden、非敌对、有 Trader 组的 pawnGroupMakers、
            //             没有敌意领主占场、neutralArrivalLayer 白黑名单
            //   · 商队：caravanTraderKinds 里至少有一种 commonality > 0，
            //           **而 commonality 里包含「皇家许可」判定**
            //
            // 帝国的 Empire_Caravan_TraderGeneral 要求 permitRequiredForTrading =
            // TradeCaravan —— 玩家没有这个皇家许可时 commonality 恒为 0，
            // 商队永远来不了。这就是作者实测"帝国商队未能成行"的真正原因。
            //
            // 注意：不能用 CanFireNow 代替。CanFireNow 只查"此刻允不允许"
            // （难度开关、冷却、目标合法性），faction 传进去时**不查 FactionCanBeGroupSource**，
            // 所以它对帝国照样返回 true。
            var worker = IncidentDefOf.TraderCaravanArrival.Worker as IncidentWorker_TraderCaravanArrival;
            if (worker == null) return false;

            var parms = new IncidentParms { target = map, faction = f };
            return worker.FactionCanBeGroupSource(f, parms);
        }

        /// <summary>该派系此刻能派出的轨道商人种类（没有则 null）。</summary>
        public static TraderKindDef PickOrbitalKind(Faction f)
        {
            if (f == null || f.IsPlayer || f.temporary || f.defeated) return null;
            if (f.HostileTo(Faction.OfPlayer)) return null;

            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return null;
            if (map.passingShipManager != null && map.passingShipManager.passingShips.Count >= 5) return null;

            var kinds = f.def.orbitalTraderKinds;
            if (kinds == null) return null;

            for (int i = 0; i < kinds.Count; i++)
            {
                var k = kinds[i];
                if (k == null || !k.orbital) continue;
                if (k.faction != null && k.faction != f.def) continue;
                if (k.CalculatedCommonality <= 0f) continue;
                if (!HasTradePermit(f, k, map)) continue;
                return k;
            }
            return null;
        }

        /// <summary>玩家是否有该商人种类要求的皇家许可（无要求视为有）。</summary>
        private static bool HasTradePermit(Faction f, TraderKindDef kind, Map map)
        {
            if (kind.permitRequiredForTrading == null) return true;
            var colonists = map.mapPawns.FreeColonists;
            for (int i = 0; i < colonists.Count; i++)
            {
                var p = colonists[i];
                if (p != null && p.royalty != null
                    && p.royalty.HasPermit(kind.permitRequiredForTrading, f)) return true;
            }
            return false;
        }

        /// <summary>该派系能不能派轨道贸易船。</summary>
        public static bool CanSendOrbitalTrade(Faction f)
        {
            return PickOrbitalKind(f) != null;
        }

        /// <summary>
        /// 发起一艘轨道贸易船。
        ///
        /// ⚠️ 必须**显式指定 traderKind**：
        /// IncidentWorker_OrbitalTraderArrival.TryExecuteWorker 只看 parms.traderKind，
        /// 为 null 时从**全库**里随机挑一个能生成的轨道商人种类，
        /// 完全无视 parms.faction —— 那样挑出来的船可能压根不是这个派系的。
        /// </summary>
        public bool TryFireOrbitalTrader(Faction f)
        {
            var kind = PickOrbitalKind(f);
            if (kind == null) return false;

            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return false;

            var parms = new IncidentParms { target = map, faction = f, traderKind = kind };
            return Find.Storyteller.TryFire(
                new FiringIncident(IncidentDefOf.OrbitalTraderArrival, null, parms));
        }

        /// <summary>
        /// 把一次贸易访问送到玩家殖民地：优先陆路商队，发不出去就改轨道贸易船。
        ///
        /// 两条路都按原版判据预检过（见 CanSendCaravan / PickOrbitalKind），
        /// 所以正常不会出现"信里写了、结果没来"。
        /// 万一还是失败（比如 storyteller 全局冷却），发一条中性提示。
        /// </summary>
        private bool DeliverTradeVisit(Faction faction, bool sellOff)
        {
            if (faction == null) return false;

            bool fired = CanSendCaravan(faction) && TryFireCaravan(faction);
            bool orbital = false;

            if (!fired && CanSendOrbitalTrade(faction))
            {
                orbital = TryFireOrbitalTrader(faction);
                fired = orbital;
            }
            if (!fired) return false;

            if (sellOff)
            {
                ApplyTradeBonus();
            }
            else
            {
                float amount = Mathf.Max(2000f, TotalEquity() * 0.02f);
                TryInjectCaravanSilver(faction, amount);
            }

            string labelKey, textKey;
            if (orbital)
            {
                labelKey = sellOff ? "RimForex_Ev2OrbitalArrivedLabel" : "RimForex_Ev1OrbitalArrivedLabel";
                textKey = sellOff ? "RimForex_Ev2OrbitalArrived" : "RimForex_Ev1OrbitalArrived";
            }
            else
            {
                labelKey = sellOff ? "RimForex_Ev2ArrivedLabel" : "RimForex_Ev1ArrivedLabel";
                textKey = sellOff ? "RimForex_Ev2Arrived" : "RimForex_Ev1Arrived";
            }

            Find.LetterStack.ReceiveLetter(
                labelKey.Translate(faction.Name),
                textKey.Translate(faction.Name, E2TradeBonusDays.ToString()),
                LetterDefOf.PositiveEvent);
            return true;
        }

        /// <summary>
        /// 该派系能不能派**帝国征收队**。
        ///
        /// 只有帝国能派（原版 worker 里直接 `parms.faction = Faction.OfEmpire`）。
        /// 判据用原版那道门：IncidentWorker_CaravanArrivalTributeCollector
        /// 继承自 TraderCaravanArrival，但把 TraderKindCommonality 改成
        /// 直接返回 CalculatedCommonality —— **不查皇家许可**，
        /// 所以帝国在这里是能过的（正常商队那条路反而过不了）。
        /// </summary>
        public static bool CanSendTributeCollector(Faction f)
        {
            if (f == null || f != Faction.OfEmpire) return false;
            if (f.IsPlayer || f.temporary || f.defeated) return false;
            if (f.HostileTo(Faction.OfPlayer)) return false;

            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return false;

            var def = DefDatabase<IncidentDef>.GetNamedSilentFail("CaravanArrivalTributeCollector");
            if (def == null) return false;   // 没装 Royalty

            var worker = def.Worker as IncidentWorker_TraderCaravanArrival;
            if (worker == null) return false;

            var parms = new IncidentParms { target = map, faction = f };
            return worker.FactionCanBeGroupSource(f, parms);
        }

        /// <summary>发起一支帝国征收队。</summary>
        public bool TryFireTributeCollector()
        {
            var def = DefDatabase<IncidentDef>.GetNamedSilentFail("CaravanArrivalTributeCollector");
            if (def == null) return false;

            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return false;

            var parms = new IncidentParms { target = map, faction = Faction.OfEmpire };
            return Find.Storyteller.TryFire(new FiringIncident(def, null, parms));
        }

        /// <summary>发起一次商队。成功返回 true。</summary>
        public bool TryFireCaravan(Faction f)
        {
            if (f == null) return false;
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return false;

            var parms = new IncidentParms { target = map, faction = f };
            return Find.Storyteller.TryFire(
                new FiringIncident(IncidentDefOf.TraderCaravanArrival, null, parms));
        }

        /// <summary>
        /// 给来访商队的驼兽背包塞白银（E1-A 的"带更多白银"）。
        ///
        /// 已验证 `Pawn_TraderTracker.Goods` 在贸易领主下取 **Carrier 角色 pawn 的背包**，
        /// 所以往那儿塞银子就能直接抬高他们的购买力。
        /// 找不到 Carrier 时返回 false，由调用方退化处理。
        /// </summary>
        public bool TryInjectCaravanSilver(Faction f, float amount)
        {
            if (f == null || amount < 1f) return false;
            Map map = Find.AnyPlayerHomeMap;
            if (map == null || map.lordManager == null) return false;

            var lords = map.lordManager.lords;
            for (int i = 0; i < lords.Count; i++)
            {
                var lord = lords[i];
                if (lord == null || lord.faction != f) continue;
                if (!(lord.LordJob is LordJob_TradeWithColony)) continue;

                for (int j = 0; j < lord.ownedPawns.Count; j++)
                {
                    var pawn = lord.ownedPawns[j];
                    if (pawn == null || pawn.inventory == null) continue;
                    if (pawn.GetTraderCaravanRole() != TraderCaravanRole.Carrier) continue;

                    var silver = ThingMaker.MakeThing(ThingDefOf.Silver, null);
                    silver.stackCount = Mathf.RoundToInt(amount);
                    if (pawn.inventory.innerContainer.TryAdd(silver)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 每日结算时的世界事件检测（必须在 AnchorDayOpen 之前调用 —— 那时
        /// `previousRate` 还是昨日收盘）。
        ///
        /// preview2 只接了 E1（暴涨）。E2/E4~E7 在后续 preview 里往这里加。
        /// </summary>
        private void DetectWorldEvents()
        {
            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg != null && !cfg.enableWorldEvents) return;

            int now = GenTicks.TicksGame;
            FxCurrency best = null;
            float bestZ = 0f;

            // ── 诊断：无论触没触发，都记录当天最大波动 ──
            // 用来校准 EventSigma。作者 2026-10-25 反馈 6 天零事件，
            // 需要真实数据判断是"运气"还是"阈值根本够不到"。
            float maxAbsZ = 0f;
            float maxPct = 0f;

            var keys = new List<int>(market.currencies.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                var c = market.currencies[keys[i]];
                if (c == null) continue;
                if (c.factionLoadID == Faction.OfPlayer.loadID) continue;

                float pct = DailyReturnOf(c) * 100f;
                float sigma = SigmaDayOf(c);
                if (sigma <= 0f) continue;
                float z = DailyReturnOf(c) / sigma;

                float absZ = Mathf.Abs(z);
                if (absZ > maxAbsZ) { maxAbsZ = absZ; maxPct = pct; }

                // 冷却中的货币不参与触发，但**参与诊断**（否则峰值会偏小）
                if (now - c.lastEventTick < EventCooldownPerCurrency) continue;
                if (absZ < EventSigma) continue;

                // ⚠️ 必须在**挑选阶段**就筛掉"做不出任何效果"的派系
                // （2026-10-25 实测）：否则一个 2.4σ 的帝国会把机会吃掉 ——
                // 它派不出商队，事件被跳过，而排在后面的合格货币根本没机会。
                var cand = FactionByID(c.factionLoadID);
                if (cand == null || cand.temporary || cand.IsPlayer) continue;
                if (!cand.HostileTo(Faction.OfPlayer)
                    && !CanSendCaravan(cand) && !CanSendOrbitalTrade(cand)
                    && !CanSendTributeCollector(cand)) continue;

                if (absZ > Mathf.Abs(bestZ)) { best = c; bestZ = z; }
            }

            diagMaxAbsZ = maxAbsZ;
            diagMaxPct = maxPct;
            diagSettleDays++;

            // 诊断：到底哪些派系能做贸易
            // （抽成独立方法，设置页可以随时重算 —— 否则刚读档时全是 0，
            //   作者 2026-10-25 就被这个"没刷新"骗了一次）
            RecountTradeFactions();
            if (maxAbsZ > diagPeakZ)
            {
                diagPeakZ = maxAbsZ;
                diagPeakPct = maxPct;
            }

            if (!WorldEventAllowed()) return;
            if (best == null) return;

            var faction = FactionByID(best.factionLoadID);
            if (faction == null || faction.temporary || faction.IsPlayer) return;

            // 非敌对却派不出商队 —— 这个事件对它没有任何可观察效果，跳过。
            if (!faction.HostileTo(Faction.OfPlayer) && !CanSendCaravan(faction)) return;

            best.lastEventTick = now;
            lastWorldEventTick = now;

            float pctMove = DailyReturnOf(best) * 100f;
            if (bestZ > 0f) QueueE1(best, faction, pctMove);
            else QueueE2(best, faction, pctMove);
        }

        /// <summary>E2：货币大跌 → 商队来清库存，或削弱敌方据点（按派系是否敌对分叉）。</summary>
        private void QueueE2(FxCurrency c, Faction faction, float pct)
        {
            bool hostile = faction.HostileTo(Faction.OfPlayer);

            if (hostile)
            {
                var settlement = PickSettlementOf(faction);
                if (settlement != null)
                {
                    if (weakenedSettlements == null) weakenedSettlements = new List<FxWeakenedSettlement>();
                    weakenedSettlements.Add(new FxWeakenedSettlement
                    {
                        settlementID = settlement.ID,
                        factionLoadID = faction.loadID,
                        untilTick = GenTicks.TicksGame + E2WeakenWindow
                    });
                    Find.LetterStack.ReceiveLetter(
                        "RimForex_Ev2bLetterLabel".Translate(settlement.Name),
                        "RimForex_Ev2bLetter".Translate(
                            faction.Name, settlement.Name, pct.ToString("0.0"),
                            (E2WeakenWindow / TicksPerDay).ToString()),
                        LetterDefOf.PositiveEvent,
                        // 直接给"跳转到该据点"，省得玩家在世界地图上手动找
                        new LookTargets(settlement));
                }
                else
                {
                    // 没有据点可削：他们连基地都守不住，只能拿袭击出气
                    QueueEvent(FxPendingKind.RaidChance, faction.loadID, c.factionLoadID, E1RaidDelay);
                    Find.LetterStack.ReceiveLetter(
                        "RimForex_Ev2bNoSettlementLabel".Translate(faction.Name),
                        "RimForex_Ev2bNoSettlement".Translate(faction.Name, pct.ToString("0.0")),
                        LetterDefOf.NegativeEvent);
                }
                return;
            }

            // 非敌对：2 天后商队来清库存（payload=1 表示"甩卖商队"）
            if (CanSendCaravan(faction))
            {
                QueueEvent(FxPendingKind.CaravanArrive, faction.loadID, c.factionLoadID,
                    E1CaravanDelay, 1);
                Find.LetterStack.ReceiveLetter(
                    "RimForex_Ev2LetterLabel".Translate(faction.Name),
                    "RimForex_Ev2Letter".Translate(faction.Name, pct.ToString("0.0")),
                    LetterDefOf.PositiveEvent);
                return;
            }

            if (CanSendOrbitalTrade(faction))
            {
                QueueEvent(FxPendingKind.OrbitalArrive, faction.loadID, c.factionLoadID,
                    E1CaravanDelay, 1);
                Find.LetterStack.ReceiveLetter(
                    "RimForex_Ev2OrbitalLetterLabel".Translate(faction.Name),
                    "RimForex_Ev2OrbitalLetter".Translate(faction.Name, pct.ToString("0.0")),
                    LetterDefOf.PositiveEvent);
                return;
            }

            if (CanSendTributeCollector(faction))
            {
                QueueEvent(FxPendingKind.TributeArrive, faction.loadID, c.factionLoadID, E1CaravanDelay);
                Find.LetterStack.ReceiveLetter(
                    "RimForex_EvTributeLetterLabel".Translate(faction.Name),
                    "RimForex_Ev2TributeLetter".Translate(faction.Name, pct.ToString("0.0")),
                    LetterDefOf.NeutralEvent);
            }
        }

        /// <summary>随便挑该派系一个还活着的据点。</summary>
        private Settlement PickSettlementOf(Faction faction)
        {
            if (faction == null) return null;
            var settlements = Find.WorldObjects.Settlements;
            int count = 0;
            for (int i = 0; i < settlements.Count; i++)
            {
                if (settlements[i] != null && settlements[i].Faction == faction) count++;
            }
            if (count == 0) return null;

            int pick = Rand.Range(0, count);
            for (int i = 0; i < settlements.Count; i++)
            {
                var s = settlements[i];
                if (s == null || s.Faction != faction) continue;
                if (pick-- == 0) return s;
            }
            return null;
        }

        /// <summary>
        /// E2-B：把已经生成的据点地图上的守军削掉一半。
        ///
        /// 原版没有"据点防御"字段，守军统一由 DefaultPawnsPoints(1150~1600) 决定，
        /// 插不进去，只能在**地图生成之后**动手。所以弱化记录会一直挂着，
        /// 直到玩家真的去打那个据点（或 10 天窗口过期）。
        /// </summary>
        private void ProcessWeakenedSettlements()
        {
            if (GenTicks.TicksGame % 250 != 0) return;
            if (weakenedSettlements == null || weakenedSettlements.Count == 0) return;
            int now = GenTicks.TicksGame;

            for (int i = weakenedSettlements.Count - 1; i >= 0; i--)
            {
                var w = weakenedSettlements[i];
                if (w == null) { weakenedSettlements.RemoveAt(i); continue; }
                if (now > w.untilTick) { weakenedSettlements.RemoveAt(i); continue; }

                Map hit = null;
                for (int m = 0; m < Find.Maps.Count; m++)
                {
                    var map = Find.Maps[m];
                    if (map == null || map.Parent == null) continue;
                    var st = map.Parent as Settlement;
                    if (st != null && st.ID == w.settlementID) { hit = map; break; }
                }
                if (hit == null) continue;

                int removed = ThinDefenders(hit, w.factionLoadID);
                weakenedSettlements.RemoveAt(i);
                if (removed > 0)
                {
                    Messages.Message("RimForex_Ev2bThinned".Translate(removed),
                        MessageTypeDefOf.NeutralEvent, false);
                }
            }
        }

        /// <summary>随机销毁地图上属于该派系的一部分 pawn。返回销毁数量。</summary>
        private int ThinDefenders(Map map, int factionLoadID)
        {
            var faction = FactionByID(factionLoadID);
            if (faction == null || map == null) return 0;

            var targets = new List<Pawn>();
            var all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.Destroyed || p.Dead) continue;
                if (p.Faction != faction) continue;
                targets.Add(p);
            }
            if (targets.Count <= 1) return 0;

            int remove = Mathf.RoundToInt(targets.Count * E2ThinFraction);
            if (targets.Count - remove < 1) remove = targets.Count - 1;

            // 洗牌后从头删，避免总是削掉同一批（比如总是削掉带名字的头目）
            for (int i = 0; i < targets.Count; i++)
            {
                int j = Rand.Range(i, targets.Count);
                var tmp = targets[i]; targets[i] = targets[j]; targets[j] = tmp;
            }

            int done = 0;
            for (int i = 0; i < remove; i++)
            {
                var p = targets[i];
                if (p == null || p.Destroyed) continue;
                p.Destroy(DestroyMode.Vanish);
                done++;
            }
            return done;
        }

        /// <summary>
        /// E2-A 的议价加成：给殖民地所有自由殖民者挂一个 5 天的临时 hediff。
        ///
        /// 原版没有直接改交易价的钩子，但 `TradePriceImprovement` 是原版 stat，
        /// hediff 的 statOffsets 可以抬它 —— 这是不改原版代码能做到的等价效果。
        /// </summary>
        private void ApplyTradeBonus()
        {
            var def = DefDatabase<HediffDef>.GetNamedSilentFail("RimForexTradeBonus");
            if (def == null) return;

            for (int m = 0; m < Find.Maps.Count; m++)
            {
                var map = Find.Maps[m];
                if (map == null || !map.IsPlayerHome) continue;
                var pawns = map.mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var pawn = pawns[i];
                    if (pawn == null || pawn.health == null) continue;
                    if (pawn.health.hediffSet.HasHediff(def)) continue;
                    pawn.health.AddHediff(def);
                }
            }
        }

        /// <summary>清理到期的临时 hediff（按 ageTicks 判断，不需要新存档字段）。</summary>
        private void ExpireTradeBonus()
        {
            if (GenTicks.TicksGame % 250 != 0) return;
            var def = DefDatabase<HediffDef>.GetNamedSilentFail("RimForexTradeBonus");
            if (def == null) return;
            int life = E2TradeBonusDays * TicksPerDay;

            // 作者 2026-10-25：这个 buff 只在商队还在图上时存在。
            // 商队走了要立刻消失，否则玩家会以为自己一直有议价优势。
            // 另外保留 5 天上限兜底，防止商队卡住不走。
            bool caravanPresent = false;
            for (int m = 0; m < Find.Maps.Count && !caravanPresent; m++)
            {
                var map = Find.Maps[m];
                if (map == null || !map.IsPlayerHome || map.lordManager == null) continue;
                var lords = map.lordManager.lords;
                for (int i = 0; i < lords.Count; i++)
                {
                    if (lords[i] != null && lords[i].LordJob is LordJob_TradeWithColony)
                    {
                        caravanPresent = true;
                        break;
                    }
                }
            }

            for (int m = 0; m < Find.Maps.Count; m++)
            {
                var map = Find.Maps[m];
                if (map == null || !map.IsPlayerHome) continue;
                var pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var pawn = pawns[i];
                    if (pawn == null || pawn.health == null) continue;
                    var h = pawn.health.hediffSet.GetFirstHediffOfDef(def);
                    if (h == null) continue;
                    if (!caravanPresent || h.ageTicks > life) pawn.health.RemoveHediff(h);
                }
            }
        }

        /// <summary>
        /// E4/E5：战争新闻。
        ///
        /// 每 8~12 日尝试一次：找一个"A 敌对 B"的对，随机决定谁占了上风，
        /// 然后以 60% 概率把预期变成真实的汇率漂移。
        ///
        /// 漂移不直接改价格，而是写进 `newsDriftLog`，由 TickStep 逐步施加 ——
        /// 这样走出来的是一条缓坡，不是跳空。
        /// </summary>
        private void ProcessWarNews()
        {
            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg != null && !cfg.enableWorldEvents) return;
            if (GenTicks.TicksGame < nextNewsTick) return;
            if (!WorldEventAllowed()) return;

            nextNewsTick = GenTicks.TicksGame + Rand.Range(8, 13) * TicksPerDay;

            var all = Find.FactionManager.AllFactionsListForReading;
            Faction a = null, b = null;
            for (int i = 0; i < all.Count && a == null; i++)
            {
                var fa = all[i];
                if (fa == null || fa.IsPlayer || fa.temporary || fa.Hidden) continue;
                if (market.Get(fa.loadID) == null) continue;
                for (int j = 0; j < all.Count; j++)
                {
                    var fb = all[j];
                    if (fb == null || fb == fa || fb.IsPlayer || fb.temporary || fb.Hidden) continue;
                    if (market.Get(fb.loadID) == null) continue;
                    if (!fa.HostileTo(fb)) continue;
                    a = fa; b = fb;
                    break;
                }
            }
            if (a == null || b == null) return;

            if (!Rand.Chance(NewsDriftChance)) return;

            bool aWins = Rand.Chance(0.5f);
            Faction winner = aWins ? a : b;
            Faction loser = aWins ? b : a;

            var winCur = market.Get(winner.loadID);
            var loseCur = market.Get(loser.loadID);
            if (winCur == null || loseCur == null) return;

            int until = GenTicks.TicksGame + NewsDriftDays * TicksPerDay;
            loseCur.newsDriftLog = -NewsDriftTargetSigma * SigmaDayOf(loseCur) * NewsDriftDays;
            loseCur.newsDriftUntilTick = until;
            winCur.newsDriftLog = NewsDriftOtherSigma * SigmaDayOf(winCur) * NewsDriftDays;
            winCur.newsDriftUntilTick = until;

            lastWorldEventTick = GenTicks.TicksGame;

            Find.LetterStack.ReceiveLetter(
                "RimForex_Ev4LetterLabel".Translate(winner.Name, loser.Name),
                "RimForex_Ev4Letter".Translate(
                    winner.Name, loser.Name, (NewsDriftChance * 100f).ToString("0")),
                LetterDefOf.NeutralEvent);
        }

        /// <summary>E1：货币大涨 → 商队或袭击（按派系是否敌对分叉）。</summary>
        private void QueueE1(FxCurrency c, Faction faction, float pct)
        {
            bool hostile = faction.HostileTo(Faction.OfPlayer);

            if (hostile)
            {
                // 敌对派系发不出商队 → 改为 3 天后掷骰袭击
                QueueEvent(FxPendingKind.RaidChance, faction.loadID, c.factionLoadID, E1RaidDelay);
                Find.LetterStack.ReceiveLetter(
                    "RimForex_Ev1bLetterLabel".Translate(faction.Name),
                    "RimForex_Ev1bLetter".Translate(faction.Name, pct.ToString("0.0")),
                    LetterDefOf.NegativeEvent);
                return;
            }

            if (CanSendCaravan(faction))
            {
                QueueEvent(FxPendingKind.CaravanArrive, faction.loadID, c.factionLoadID, E1CaravanDelay);
                Find.LetterStack.ReceiveLetter(
                    "RimForex_Ev1LetterLabel".Translate(faction.Name),
                    "RimForex_Ev1Letter".Translate(faction.Name, pct.ToString("0.0")),
                    LetterDefOf.PositiveEvent);
                return;
            }

            if (CanSendOrbitalTrade(faction))
            {
                QueueEvent(FxPendingKind.OrbitalArrive, faction.loadID, c.factionLoadID, E1CaravanDelay);
                Find.LetterStack.ReceiveLetter(
                    "RimForex_Ev1OrbitalLetterLabel".Translate(faction.Name),
                    "RimForex_Ev1OrbitalLetter".Translate(faction.Name, pct.ToString("0.0")),
                    LetterDefOf.PositiveEvent);
                return;
            }

            if (CanSendTributeCollector(faction))
            {
                QueueEvent(FxPendingKind.TributeArrive, faction.loadID, c.factionLoadID, E1CaravanDelay);
                Find.LetterStack.ReceiveLetter(
                    "RimForex_EvTributeLetterLabel".Translate(faction.Name),
                    "RimForex_Ev1TributeLetter".Translate(faction.Name, pct.ToString("0.0")),
                    LetterDefOf.NeutralEvent);
            }
        }

        /// <summary>
        /// E3：单日暴赚 → 各派系好感上升 + 概率招来袭击（作者已拍板）。
        ///
        /// 为什么"袭击概率上升"要自己掷骰：原版的袭击概率来自 storyteller，改不了，
        /// 但我们可以直接排一次额外来袭，效果等价。
        /// </summary>
        private void MaybeTriggerE3(FxAccount acc, float dayPnl)
        {
            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg != null && !cfg.enableWorldEvents) return;
            if (!WorldEventAllowed()) return;
            if (GenTicks.TicksGame - lastWealthEventTick < E3GoodwillCooldown) return;

            float threshold = Mathf.Max(E3MinPnl,
                Mathf.Max(0f, acc != null ? acc.Equity : 0f) * E3EquityFraction);
            if (dayPnl < threshold) return;

            lastWealthEventTick = GenTicks.TicksGame;
            lastWorldEventTick = GenTicks.TicksGame;

            // ① 所有非敌对派系好感 +6
            var all = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                var f = all[i];
                if (f == null || f.IsPlayer || f.temporary || f.Hidden) continue;
                f.TryAffectGoodwillWith(Faction.OfPlayer, E3Goodwill,
                    canSendMessage: false, canSendHostilityLetter: false);
            }

            // ② 35% 概率招来一次袭击（3 天后）
            bool raid = Rand.Chance(E3RaidChance);
            if (raid)
            {
                Faction target = null;
                for (int i = 0; i < all.Count; i++)
                {
                    var f = all[i];
                    if (f == null || f.IsPlayer || f.temporary || f.Hidden) continue;
                    if (!f.HostileTo(Faction.OfPlayer)) continue;
                    target = f;
                    break;
                }
                if (target != null)
                {
                    QueueEvent(FxPendingKind.RaidChance, target.loadID, -1, E1RaidDelay);
                }
                else
                {
                    raid = false;
                }
            }

            string body = "RimForex_Ev3Letter".Translate(
                dayPnl.ToString("F0"), E3Goodwill.ToString());
            if (raid) body += "\n\n" + "RimForex_Ev3Raid".Translate();

            Find.LetterStack.ReceiveLetter(
                "RimForex_Ev3LetterLabel".Translate(),
                body,
                LetterDefOf.PositiveEvent);
        }

        // ══════════════════════════════════════════════════════════════
        //  0.3.0 调试入口（设置页的测试按钮）
        // ══════════════════════════════════════════════════════════════

        /// <summary>测试：强制触发一次 E1（随机挑一个非玩家货币）。</summary>
        public bool DebugForceSurge()
        {
            EnsureAllFactionCurrencies();
            var keys = new List<int>(market.currencies.Keys);
            if (keys.Count == 0) return false;

            // 从随机位置开始扫，挑第一个可用的
            int start = Rand.Range(0, keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                var c = market.currencies[keys[(start + i) % keys.Count]];
                if (c == null) continue;
                if (c.factionLoadID == Faction.OfPlayer.loadID) continue;
                var f = FactionByID(c.factionLoadID);
                if (f == null || f.temporary || f.IsPlayer) continue;
                if (!f.HostileTo(Faction.OfPlayer)
                    && !CanSendCaravan(f) && !CanSendOrbitalTrade(f)
                    && !CanSendTributeCollector(f)) continue;

                c.lastEventTick = GenTicks.TicksGame;
                lastWorldEventTick = GenTicks.TicksGame;
                QueueE1(c, f, EventSigma * SigmaDayOf(c) * 100f);
                return true;
            }
            return false;
        }

        /// <summary>测试：强制触发一次 E3（用当前账户）。</summary>
        public bool DebugForceProdigy()
        {
            Map map = Find.AnyPlayerHomeMap;
            var acc = map != null ? ColonyAccount(map) : null;
            if (acc == null) return false;

            // 绕过两个冷却，直接喂一个刚过门槛的盈亏
            lastWealthEventTick = -999999;
            lastWorldEventTick = -999999;
            float fake = Mathf.Max(E3MinPnl,
                Mathf.Max(0f, acc.Equity) * E3EquityFraction) + 1f;
            MaybeTriggerE3(acc, fake);
            return true;
        }

        /// <summary>测试：强制触发一次 E2（随机挑一个非玩家货币）。</summary>
        public bool DebugForceCrash()
        {
            EnsureAllFactionCurrencies();
            var keys = new List<int>(market.currencies.Keys);
            if (keys.Count == 0) return false;

            int start = Rand.Range(0, keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                var c = market.currencies[keys[(start + i) % keys.Count]];
                if (c == null) continue;
                if (c.factionLoadID == Faction.OfPlayer.loadID) continue;
                var f = FactionByID(c.factionLoadID);
                if (f == null || f.temporary || f.IsPlayer) continue;
                if (!f.HostileTo(Faction.OfPlayer)
                    && !CanSendCaravan(f) && !CanSendOrbitalTrade(f)
                    && !CanSendTributeCollector(f)) continue;

                c.lastEventTick = GenTicks.TicksGame;
                lastWorldEventTick = GenTicks.TicksGame;
                QueueE2(c, f, -EventSigma * SigmaDayOf(c) * 100f);
                return true;
            }
            return false;
        }

        /// <summary>测试：立刻触发一次 E4/E5 战争新闻。</summary>
        public bool DebugForceNews()
        {
            nextNewsTick = 0;
            lastWorldEventTick = -999999;
            ProcessWarNews();
            // 成功时 ProcessWarNews 会把 lastWorldEventTick 写成当前时间
            return lastWorldEventTick >= 0;
        }

        /// <summary>测试：立刻制造一笔欠款并启动收债流程。</summary>
        public bool DebugForceDebt()
        {
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return false;
            var acc = ColonyAccount(map);
            if (acc == null) return false;

            acc.balance = -Mathf.Max(1000f, Mathf.Abs(acc.Equity) * 0.05f);
            acc.negativeSinceTick = GenTicks.TicksGame - (E7NegativeDays + 1) * TicksPerDay;
            debtActive = false;
            MaybeStartDebtCollection();
            return debtActive;
        }

        /// <summary>测试：跳过门槛直接启动一次 E6 报复循环。</summary>
        public bool DebugForceRetaliation()
        {
            EnsureAllFactionCurrencies();
            var keys = new List<int>(market.currencies.Keys);
            if (keys.Count == 0) return false;

            int start = Rand.Range(0, keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                var c = market.currencies[keys[(start + i) % keys.Count]];
                if (c == null) continue;
                if (c.factionLoadID == Faction.OfPlayer.loadID) continue;
                var f = FactionByID(c.factionLoadID);
                if (f == null || f.temporary || f.IsPlayer || f.defeated) continue;

                // 把敞口和计时都伪装成"已经持续做空"，
                // 否则 UpdateShortExposure 下次结算就会发现空头是 0 并中止。
                c.shortNotional = E6ShortNotionalMin * 2f;
                c.shortSinceTick = GenTicks.TicksGame - (E6ShortHoldDays + 1) * TicksPerDay;
                StartRetaliation(c, c.shortNotional);
                return true;
            }
            return false;
        }

        /// <summary>测试：立刻让当前报复循环进入一次危机。</summary>
        public bool DebugForceCrisis()
        {
            if (retaliations == null || retaliations.Count == 0) return false;
            var r = retaliations[0];
            if (r == null) return false;
            var cur = market.Get(r.currencyFaction);
            if (cur == null) return false;
            TriggerCrisis(r, cur);
            return true;
        }

        /// <summary>测试：直接对一个敌对派系发起袭击（绕过掷骰）。</summary>
        public bool DebugForceRaid()
        {
            var all = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                var f = all[i];
                if (f == null || f.IsPlayer || f.temporary || f.Hidden) continue;
                if (!f.HostileTo(Faction.OfPlayer)) continue;
                Map map = Find.AnyPlayerHomeMap;
                float pts = map != null
                    ? Mathf.Max(StorytellerUtility.DefaultThreatPointsNow(map) * 1.3f, 2000f)
                    : 2000f;
                return TryFireRaid(f, pts);
            }
            return false;
        }

        /// <summary>测试：把所有排队事件立刻催熟（省去等待 2~3 天）。</summary>
        public int DebugRipenPending()
        {
            if (pendingEvents == null) return 0;
            int n2 = pendingEvents.Count;
            for (int i = 0; i < pendingEvents.Count; i++)
            {
                if (pendingEvents[i] != null) pendingEvents[i].dueTick = GenTicks.TicksGame;
            }
            return n2;
        }

        // ══════════════════════════════════════════════════════════════
        //  0.3.0 E6：大量做空 → 派系报复 → 危机 → 名存实亡
        // ══════════════════════════════════════════════════════════════

        /// <summary>每日结算时刷新各货币的空头敞口，并判断是否触发报复。</summary>
        private void UpdateShortExposure()
        {
            var keys = new List<int>(market.currencies.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                var c = market.currencies[keys[i]];
                if (c != null) c.shortNotional = 0f;
            }

            var accKeys = new List<int>(accounts.Keys);
            for (int i = 0; i < accKeys.Count; i++)
            {
                var acc = accounts[accKeys[i]];
                if (acc == null || acc.positions == null) continue;
                for (int j = 0; j < acc.positions.Count; j++)
                {
                    var pos = acc.positions[j];
                    if (pos == null || pos.isLong) continue;
                    var cur = market.Get(pos.currencyFactionLoadID);
                    if (cur == null) continue;
                    float px = pos.markRate > 0f ? pos.markRate : pos.entryRate;
                    cur.shortNotional += Mathf.Abs(pos.units) * px;
                }
            }

            int now = GenTicks.TicksGame;
            float threshold = Mathf.Max(E6ShortNotionalMin,
                TotalEquity() * E6ShortEquityMultiple);

            for (int i = 0; i < keys.Count; i++)
            {
                var c = market.currencies[keys[i]];
                if (c == null) continue;

                if (c.shortNotional < threshold)
                {
                    c.shortSinceTick = 0;
                    continue;
                }

                if (c.shortSinceTick == 0) c.shortSinceTick = now;
                if (now - c.shortSinceTick < E6ShortHoldDays * TicksPerDay) continue;

                bool already = false;
                if (retaliations != null)
                {
                    for (int r = 0; r < retaliations.Count; r++)
                    {
                        if (retaliations[r] != null && retaliations[r].currencyFaction == c.factionLoadID)
                        {
                            already = true;
                            break;
                        }
                    }
                }
                if (already) continue;

                StartRetaliation(c, c.shortNotional);
            }
        }

        /// <summary>
        /// 五档危机文案的键。
        ///
        /// 写成字面量数组而不是运行时拼接：audit 是逐字面量扫源码的，
        /// 拼接出来的键它认不出，会把五档文案当成"XML 里残留、代码里没用"。
        /// </summary>
        private static readonly string[] CrisisTextKeys =
        {
            "RimForex_Ev6Crisis1", "RimForex_Ev6Crisis2", "RimForex_Ev6Crisis3",
            "RimForex_Ev6Crisis4", "RimForex_Ev6Crisis5"
        };

        /// <summary>
        /// 把某个派系强制打成敌对，并把好感压到 **-100**。
        ///
        /// 三步走，因为原版 API 各有坑：
        ///   ① TryAffectGoodwillWith 传的是**变化量**，所以要传 -100 - 当前值
        ///   ② 它内部还会过 CalculateAdjustedGoodwillChange（难度系数），
        ///      负向变化可能被缩放，所以算完还要验一次是否真的敌对
        ///   ③ 没敌对就再补一次"够到敌对线"的差额（GoodwillToMakeHostile），
        ///      多降 10 点留余量
        ///
        /// 不能用 SetRelationDirect —— 它对使用好感度的派系会直接 Log.Error 并返回。
        /// </summary>
        private void ForceHostile(Faction f)
        {
            if (f == null || f.IsPlayer || f.temporary || f.defeated) return;
            if (f.HostileTo(Faction.OfPlayer)) return;

            int current = f.GoodwillWith(Faction.OfPlayer);
            int delta = -100 - current;
            if (delta != 0)
            {
                f.TryAffectGoodwillWith(Faction.OfPlayer, delta,
                    canSendMessage: false, canSendHostilityLetter: false);
            }

            if (!f.HostileTo(Faction.OfPlayer))
            {
                int delta2 = Faction.OfPlayer.GoodwillToMakeHostile(f) - 10;
                if (delta2 != 0)
                {
                    f.TryAffectGoodwillWith(Faction.OfPlayer, delta2,
                        canSendMessage: false, canSendHostilityLetter: false);
                }
            }
        }

        /// <summary>实时统计某个货币的空头名义仓位（不依赖每日结算的缓存）。</summary>
        private float ShortNotionalFor(int factionLoadID)
        {
            if (accounts == null) return 0f;
            float sum = 0f;
            var accKeys = new List<int>(accounts.Keys);
            for (int i = 0; i < accKeys.Count; i++)
            {
                var acc = accounts[accKeys[i]];
                if (acc == null || acc.positions == null) continue;
                for (int j = 0; j < acc.positions.Count; j++)
                {
                    var pos = acc.positions[j];
                    if (pos == null || pos.isLong) continue;
                    if (pos.currencyFactionLoadID != factionLoadID) continue;
                    float px = pos.markRate > 0f ? pos.markRate : pos.entryRate;
                    sum += Mathf.Abs(pos.units) * px;
                }
            }
            return sum;
        }

        private void StartRetaliation(FxCurrency c, float notional)
        {
            var f = FactionByID(c.factionLoadID);
            if (f == null || f.IsPlayer || f.temporary || f.defeated) return;

            if (retaliations == null) retaliations = new List<FxRetaliation>();
            retaliations.Add(new FxRetaliation
            {
                factionLoadID = f.loadID,
                currencyFaction = c.factionLoadID,
                nextRaidTick = GenTicks.TicksGame
            });

            // ⚠️ 不能只写 -100（2026-10-25 作者实测的 bug）：
            // TryAffectGoodwillWith 的语义是"**变化量**"，不是"设成"。
            // 原本 +56 的盟友被扣 100 之后是 -44，而敌对线是 -75，
            // 结果它仍然是中立 —— RaidEnemy 根本发不出来，
            // storyteller 只好换一个敌对派系来打，玩家看到的"报复"来自别人。
            ForceHostile(f);

            Find.LetterStack.ReceiveLetter(
                "RimForex_Ev6LetterLabel".Translate(f.Name),
                "RimForex_Ev6Letter".Translate(f.Name, notional.ToString("F0")),
                LetterDefOf.ThreatBig);
        }

        /// <summary>报复循环：每 2 日一次袭击，满 10 次进一次危机。</summary>
        private void ProcessRetaliation()
        {
            if (GenTicks.TicksGame % 250 != 0) return;
            if (retaliations == null || retaliations.Count == 0) return;

            int now = GenTicks.TicksGame;
            for (int i = retaliations.Count - 1; i >= 0; i--)
            {
                var r = retaliations[i];
                if (r == null) { retaliations.RemoveAt(i); continue; }

                var f = FactionByID(r.factionLoadID);
                if (f == null || f.defeated) { retaliations.RemoveAt(i); continue; }

                var cur = market.Get(r.currencyFaction);

                // 放弃操盘：空头几乎清零 → 停止报复。
                // ⚠️ 这里**实时重算**，不能读 cur.shortNotional ——
                // 那是每日结算时刷新的缓存。玩家在两次结算之间平掉空单，
                // 缓存还是旧值，循环会多打一次袭击。
                float liveShort = ShortNotionalFor(r.currencyFaction);
                if (cur == null || liveShort < E6ShortNotionalMin * 0.25f)
                {
                    retaliations.RemoveAt(i);
                    Find.LetterStack.ReceiveLetter(
                        "RimForex_Ev6StopLabel".Translate(f.Name),
                        "RimForex_Ev6Stop".Translate(f.Name),
                        LetterDefOf.NeutralEvent);
                    continue;
                }

                if (now < r.nextRaidTick) continue;
                r.nextRaidTick = now + E6RaidInterval;

                // 玩家可能在这两天里靠赠礼把好感拉回中立线上。
                // 报复期间不允许"花钱消灾" —— 开打前把关系重新压回去。
                ForceHostile(f);

                if (TryFireRaid(f, 0f)) r.raidCount++;

                if (r.raidCount >= E6RaidsPerCrisis) TriggerCrisis(r, cur);
            }
        }

        private void TriggerCrisis(FxRetaliation r, FxCurrency cur)
        {
            r.raidCount = 0;
            r.crisisCount++;

            // 货币永久贬值。
            //
            // ⚠️ 必须同时平移 stepLogStart / stepLogTarget（2026-10-25 修）：
            // 价格是 `Exp(在 stepLogStart→stepLogTarget 之间插值)` 算出来的。
            // 只乘 rate 的话，下一个子步会按**旧的**对数预算重新插值，
            // 价格当场弹回去 —— 作者实测就是"暂停时掉下去、恢复速度后弹回来"。
            // 平移这两个端点，整条路径才真的下移。
            float delta = Mathf.Log(E6CrisisFactor);
            cur.rate *= E6CrisisFactor;
            cur.previousRate *= E6CrisisFactor;
            cur.stepLogStart += delta;
            cur.stepLogTarget += delta;

            int count = Rand.Range(E6RefugeeMin, E6RefugeeMax + 1);
            SpawnRefugees(count);

            var f = FactionByID(r.factionLoadID);
            string fname = f != null ? f.Name : "?";

            // 五次危机的文案各写各的，一次比一次绝望（作者 2026-10-25 要求）
            int tier = Mathf.Clamp(r.crisisCount, 1, E6CrisesToDefeat);
            Find.LetterStack.ReceiveLetter(
                "RimForex_Ev6CrisisLabel".Translate(fname, tier.ToString()),
                CrisisTextKeys[tier - 1].Translate(fname, count.ToString()),
                LetterDefOf.ThreatBig);

            // 先让第 5 封信落地，两小时后再宣布名存实亡。
            // 立刻弹窗会把信盖掉（作者实测只看到弹窗）。
            if (f != null && r.crisisCount >= E6CrisesToDefeat)
            {
                QueueEvent(FxPendingKind.CrisisDefeat, f.loadID, r.currencyFaction, E6DefeatDelay);
                retaliations.Remove(r);   // 立刻停火，别在这两小时里又打一次
            }
        }

        /// <summary>
        /// 名存实亡：不再参与政治、不再袭击或派商队，但据点保留。
        ///
        /// 同时把该派系的货币**下架**（作者 2026-10-25 要求）：
        /// 一个名存实亡的派系，其货币不该还能继续交易。
        /// 先按市价强制平掉所有相关持仓（净值退还给玩家），
        /// 再把货币从市场里删掉；EnsureAllFactionCurrencies 之后也不会再建回来
        /// （它现在会跳过 defeated 派系）。
        /// </summary>
        private void DefeatFaction(Faction f)
        {
            if (f == null) return;

            f.defeated = true;
            f.deactivated = true;

            ForceCloseAllOn(f);

            if (market != null)
            {
                market.RemoveCurrency(f.loadID);
            }
        }

        /// <summary>按当前市价强制平掉某个派系货币上的全部持仓。</summary>
        private void ForceCloseAllOn(Faction f)
        {
            if (f == null || accounts == null) return;

            var accKeys = new List<int>(accounts.Keys);
            for (int k = 0; k < accKeys.Count; k++)
            {
                var acc = accounts[accKeys[k]];
                if (acc == null || acc.positions == null) continue;

                for (int i = acc.positions.Count - 1; i >= 0; i--)
                {
                    var pos = acc.positions[i];
                    if (pos == null || pos.currencyFactionLoadID != f.loadID) continue;

                    // 口径必须与正常平仓一致（2026-10-25 作者发现点差显示 0）：
                    // 点差成本是**开仓时真的付过的**，不能因为是被动强平就抹成 0；
                    // 手续费也照常收 —— 否则"派系消亡"等于给了玩家一次免费平仓。
                    float equity = pos.Equity;
                    float exitPrice = pos.markRate > 0f
                        ? pos.markRate
                        : market.ExitPriceOf(pos.currencyFactionLoadID, pos.isLong);
                    float notional = Mathf.Abs(pos.units) * exitPrice;
                    float fee = FeeFor(notional);

                    acc.balance += equity - fee;
                    acc.feesSinceSettle += fee;
                    acc.positions.RemoveAt(i);

                    acc.RecordTrade(new FxTradeRecord
                    {
                        currencyName = pos.currencyName,
                        pairLabel = pos.currencyName + "/" + FxCurrency.QuoteCode,
                        isLong = pos.isLong,
                        units = pos.units,
                        entryRate = pos.entryRate,
                        exitRate = exitPrice,
                        margin = pos.margin,
                        pnl = pos.Pnl,
                        totalFee = pos.openFee + fee,
                        spreadCost = Mathf.Abs(pos.units) * pos.spreadAtOpen,
                        spreadRateAtClose = market.SpreadRateFor(pos.currencyFactionLoadID),
                        closedTick = GenTicks.TicksGame,
                        wasLiquidation = true
                    });
                }
            }
        }

        /// <summary>
        /// 危机难民：无派系的 SpaceRefugee，倒地，落在殖民地边缘。
        ///
        /// 刻意**不用**原版的 RefugeePodCrash（会随机挂派系），
        /// 也不用 Hospitality_Refugee（要 Ideology）。
        /// 无派系正好命中原版"救他，他可能自愿加入"的那一档。
        /// </summary>
        private void SpawnRefugees(int count)
        {
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return;
            if (refugeePawnIds == null) refugeePawnIds = new List<int>();

            for (int i = 0; i < count; i++)
            {
                var req = new PawnGenerationRequest(
                    PawnKindDefOf.SpaceRefugee, null,
                    PawnGenerationContext.NonPlayer, map.Tile);
                Pawn pawn = PawnGenerator.GeneratePawn(req);
                if (pawn == null) continue;

                HealthUtility.DamageUntilDowned(pawn);

                IntVec3 cell;
                if (!CellFinder.TryFindRandomEdgeCellWith(
                        c => c.Standable(map) && !map.roofGrid.Roofed(c),
                        map, CellFinder.EdgeRoadChance_Ignore, out cell))
                {
                    cell = CellFinder.RandomEdgeCell(map);
                }

                GenSpawn.Spawn(pawn, cell, map);
                refugeePawnIds.Add(pawn.thingIDNumber);
            }

            refugeeDeadlineTick = GenTicks.TicksGame + E6RefugeeDays * TicksPerDay;
        }

        /// <summary>每日结算时判定难民：被救起则 35%/日加入，超期则离开。</summary>
        private void ProcessRefugees()
        {
            if (refugeePawnIds == null || refugeePawnIds.Count == 0) return;
            int now = GenTicks.TicksGame;

            for (int i = refugeePawnIds.Count - 1; i >= 0; i--)
            {
                int id = refugeePawnIds[i];

                Pawn pawn = null;
                for (int m = 0; m < Find.Maps.Count && pawn == null; m++)
                {
                    var map = Find.Maps[m];
                    if (map == null || !map.IsPlayerHome) continue;
                    var all = map.mapPawns.AllPawnsSpawned;
                    for (int k = 0; k < all.Count; k++)
                    {
                        if (all[k] != null && all[k].thingIDNumber == id) { pawn = all[k]; break; }
                    }
                }

                if (pawn == null) { refugeePawnIds.RemoveAt(i); continue; }
                if (pawn.Dead || pawn.Faction == Faction.OfPlayer) { refugeePawnIds.RemoveAt(i); continue; }

                bool rescued = pawn.GuestStatus == GuestStatus.Guest;
                if (rescued && !pawn.Downed && Rand.Chance(E6RefugeeJoinChance))
                {
                    pawn.SetFaction(Faction.OfPlayer);
                    Find.LetterStack.ReceiveLetter(
                        "RimForex_Ev6JoinLabel".Translate(pawn.LabelShortCap),
                        "RimForex_Ev6Join".Translate(pawn.LabelShortCap),
                        LetterDefOf.PositiveEvent, new LookTargets(pawn));
                    refugeePawnIds.RemoveAt(i);
                    continue;
                }

                if (now >= refugeeDeadlineTick && !pawn.Downed)
                {
                    pawn.DeSpawn();
                    Find.LetterStack.ReceiveLetter(
                        "RimForex_Ev6LeaveLabel".Translate(pawn.LabelShortCap),
                        "RimForex_Ev6Leave".Translate(pawn.LabelShortCap),
                        LetterDefOf.NeutralEvent);
                    refugeePawnIds.RemoveAt(i);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  0.3.0 E7：穿仓收债
        // ══════════════════════════════════════════════════════════════

        /// <summary>每日结算时检查：负余额连续两天就开始收债。</summary>
        private void MaybeStartDebtCollection()
        {
            if (debtActive) return;
            int now = GenTicks.TicksGame;

            var keys = new List<int>(accounts.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                var acc = accounts[keys[i]];
                if (acc == null || acc.balance >= 0f) continue;
                if (acc.negativeSinceTick <= 0) continue;
                if (now - acc.negativeSinceTick < E7NegativeDays * TicksPerDay) continue;

                StartDebtCase(acc);
                return;
            }
        }

        private void StartDebtCase(FxAccount acc)
        {
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return;

            Pawn collector = SpawnCollector(map);
            debtActive = true;
            debtMapId = map.Parent != null ? map.Parent.ID : -1;
            debtDeadlineTick = GenTicks.TicksGame + E7GraceDays * TicksPerDay;
            debtCollectorId = collector != null ? collector.thingIDNumber : -1;

            Find.LetterStack.ReceiveLetter(
                "RimForex_Ev7LetterLabel".Translate(),
                "RimForex_Ev7Letter".Translate(
                    (-acc.balance).ToString("F0"), E7GraceDays.ToString()),
                LetterDefOf.ThreatBig,
                collector != null ? new LookTargets(collector) : null);

            Find.WindowStack.Add(new Window_FxDebt(this, acc));
        }

        /// <summary>
        /// 收债人所属的"平台"派系：只建一次，之后复用。
        ///
        /// 必须只建一次 —— RimWorld 全程序没有任何地方调用
        /// FactionManager.RemoveFaction，每建一个就永久残留在存档里。
        /// temporary = true 让 HasGoodwill 返回 false，不参与好感度记账。
        /// </summary>
        private Faction EnsurePlatformFaction()
        {
            if (platformFactionLoadID != 0)
            {
                var existing = FactionByID(platformFactionLoadID);
                if (existing != null) return existing;
            }

            var f = FactionGenerator.NewGeneratedFaction(
                new FactionGeneratorParms(FactionDefOf.OutlanderCivil));
            if (f == null) return null;

            f.temporary = true;
            f.Name = "RimForex_PlatformName".Translate();
            Find.FactionManager.Add(f);
            platformFactionLoadID = f.loadID;
            return f;
        }

        private Pawn SpawnCollector(Map map)
        {
            var faction = EnsurePlatformFaction();
            var req = new PawnGenerationRequest(
                PawnKindDefOf.Villager, faction,
                PawnGenerationContext.NonPlayer, map.Tile);
            Pawn pawn = PawnGenerator.GeneratePawn(req);
            if (pawn == null) return null;

            IntVec3 cell;
            if (!CellFinder.TryFindRandomEdgeCellWith(
                    c => c.Standable(map) && !map.roofGrid.Roofed(c),
                    map, CellFinder.EdgeRoadChance_Ignore, out cell))
            {
                cell = CellFinder.RandomEdgeCell(map);
            }
            GenSpawn.Spawn(pawn, cell, map);
            return pawn;
        }

        /// <summary>每 250 tick 检查收债进度。</summary>
        private void ProcessDebtCase()
        {
            if (!debtActive) return;
            if (GenTicks.TicksGame % 250 != 0) return;

            FxAccount acc = null;
            var keys = new List<int>(accounts.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] == debtMapId) { acc = accounts[keys[i]]; break; }
            }
            if (acc == null) { ResolveDebtCase(null, "gone"); return; }

            Pawn collector = FindPawnById(debtCollectorId);
            bool attacked = collector != null
                            && (collector.Dead || collector.Destroyed || collector.Downed);

            if (attacked) { TriggerCollectorRetaliation(acc); return; }

            if (acc.balance >= 0f) { ResolveDebtCase(collector, "paid"); return; }

            if (GenTicks.TicksGame >= debtDeadlineTick)
            {
                TriggerCollectorRetaliation(acc);
            }
        }

        /// <summary>攻击/逾期：招来机械袭击 + 冻结账户一年。</summary>
        private void TriggerCollectorRetaliation(FxAccount acc)
        {
            Map map = Find.AnyPlayerHomeMap;
            Faction mech = Faction.OfMechanoids;
            if (mech == null)
            {
                mech = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Mechanoid);
            }

            if (mech != null && map != null)
            {
                float pts = Mathf.Max(E7MechRaidMinPoints,
                    StorytellerUtility.DefaultThreatPointsNow(map));
                TryFireRaid(mech, pts);
            }

            if (acc != null)
            {
                acc.frozenUntilTick = GenTicks.TicksGame + E7FreezeDays * TicksPerDay;
            }

            Find.LetterStack.ReceiveLetter(
                "RimForex_Ev7AttackLabel".Translate(),
                "RimForex_Ev7Attack".Translate(E7FreezeDays.ToString()),
                LetterDefOf.ThreatBig);

            ResolveDebtCase(null, "attacked");
        }

        /// <summary>用殖民者抵债。累计市值 ≥ 债务才算数。</summary>
        public bool PayWithHostages(FxAccount acc, List<Pawn> pawns)
        {
            if (acc == null || pawns == null || pawns.Count == 0) return false;
            float debt = -acc.balance;
            if (debt <= 0.01f) return false;

            float sum = 0f;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (pawns[i] != null) sum += pawns[i].MarketValue;
            }
            if (sum < debt) return false;

            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p == null) continue;
                if (p.Spawned) p.DeSpawn();
                else p.Destroy(DestroyMode.Vanish);
            }

            acc.balance = 0f;
            acc.negativeSinceTick = 0;

            ResolveDebtCase(FindPawnById(debtCollectorId), "hostage");
            return true;
        }

        /// <summary>收债窗口里点「拒绝」时调用：直接按攻击处理。</summary>
        public void RefuseDebt(FxAccount acc)
        {
            TriggerCollectorRetaliation(acc);
        }

        /// <summary>收债窗口里点「付清」时调用。</summary>
        public bool TrySettleDebtByPayment(FxAccount acc)
        {
            if (acc == null || acc.balance < 0f) return false;
            acc.negativeSinceTick = 0;
            ResolveDebtCase(FindPawnById(debtCollectorId), "paid");
            return true;
        }

        private void ResolveDebtCase(Pawn collector, string reason)
        {
            if (!debtActive) return;
            debtActive = false;
            debtCollectorId = -1;

            if (collector != null && collector.Spawned && !collector.Destroyed && !collector.Dead)
            {
                collector.DeSpawn();
                Find.WorldPawns.PassToWorld(collector, PawnDiscardDecideMode.Discard);
            }

            if (reason == "paid" || reason == "hostage")
            {
                Find.LetterStack.ReceiveLetter(
                    "RimForex_Ev7DoneLabel".Translate(),
                    (reason == "paid" ? "RimForex_Ev7Paid" : "RimForex_Ev7Hostage").Translate(),
                    LetterDefOf.PositiveEvent);
            }
        }

        private Pawn FindPawnById(int id)
        {
            if (id < 0) return null;
            for (int m = 0; m < Find.Maps.Count; m++)
            {
                var map = Find.Maps[m];
                if (map == null) continue;
                var all = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] != null && all[i].thingIDNumber == id) return all[i];
                }
            }
            return null;
        }

        /// <summary>重新统计能做贸易的派系数量（诊断用，UI 随时可调）。</summary>
        public void RecountTradeFactions()
        {
            int caravanCount = 0, orbitalCount = 0, tributeCount = 0;
            var factions = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < factions.Count; i++)
            {
                var ff = factions[i];
                if (ff == null || ff.IsPlayer || ff.temporary) continue;
                if (CanSendCaravan(ff)) caravanCount++;
                else if (CanSendOrbitalTrade(ff)) orbitalCount++;
                else if (CanSendTributeCollector(ff)) tributeCount++;
            }
            diagCaravanFactions = caravanCount;
            diagOrbitalFactions = orbitalCount;
            diagTributeFactions = tributeCount;
        }

        /// <summary>该货币的"一日波动"标准差（对数收益）。</summary>
        public static float SigmaDayOf(FxCurrency c)
        {
            float vol = c != null && c.volatility > 0.01f ? c.volatility : 1f;
            return FxMarket.DailyVol * vol;
        }

        /// <summary>当日对数收益（结算前调用；结算时 <c>previousRate</c> 还是昨日收盘）。</summary>
        public static float DailyReturnOf(FxCurrency c)
        {
            if (c == null || c.previousRate <= 0.001f || c.rate <= 0.001f) return 0f;
            return c.rate / c.previousRate - 1f;
        }

        // ══════════════════════════════════════════════════════════════
        //  0.3.0 隔夜利息（swap）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 默认隔夜利息率：按名义仓位每天 0.2%。
        ///
        /// 0.02% 实测太温和（作者 2026-10-25 反馈），调到 0.2%。
        /// 换算：1x = 12%/游戏年，20x = 4%/日保证金，100x = 20%/日保证金。
        /// </summary>
        public const float DefaultCarryRate = 0.002f;

        /// <summary>
        /// 每日结算时收取隔夜利息：<c>Σ |units| × 现价 × 费率</c>。
        ///
        /// ── 为什么需要它（2026-10-23 分析）──
        /// 没有持仓成本时，低杠杆"死扛等回本"是免费期权：
        /// 实测 1x 下 93~97% 能在 120 日内等到 +0.35%，而输的那 3~5%
        /// 永远不会被强制平仓。加了利息，"等"才开始有代价。
        ///
        /// 作用在**名义仓位**上，所以杠杆越高、每日成本占保证金的比例越大：
        ///   1x → 0.02%/日，20x → 0.4%/日，100x → 2%/日。
        /// </summary>
        private void ChargeCarry(FxAccount acc)
        {
            if (acc == null || acc.positions == null || acc.positions.Count == 0) return;

            // 费率固定，不提供设置（作者 2026-10-25：平衡项不给开关）
            float rate = DefaultCarryRate;
            if (rate <= 0f) return;

            float total = 0f;
            for (int i = 0; i < acc.positions.Count; i++)
            {
                var p = acc.positions[i];
                if (p == null) continue;
                float px = p.markRate > 0f ? p.markRate : p.entryRate;
                total += Mathf.Abs(p.units) * px * rate;
            }
            if (total < 0.01f) return;

            acc.balance -= total;
            acc.carrySinceSettle += total;
            acc.RecordFee("RimForex_FeeCarry".Translate(), total);
        }

        /// <summary>所有账户的权益之和（用于 E1 商队白银量的量级）。</summary>
        public float TotalEquity()
        {
            if (accounts == null || accounts.Count == 0) return 0f;
            float sum = 0f;
            var keys = new List<int>(accounts.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                var acc = accounts[keys[i]];
                if (acc != null) sum += Mathf.Max(0f, acc.Equity);
            }
            return sum;
        }

        /// <summary>
        /// 每日托管费：对**闲置余额**收取，堵"把白银存进账户躲袭击"。
        ///
        /// 免收额度 20,000：正常玩家把账户当钱包用几乎不会触发。
        /// 超出部分按 0.3%/日 —— 藏 10 万一年（60 日）约 14.4% 的成本，
        /// 足以让"为了躲袭击而囤钱"变成一笔需要权衡的买卖。
        /// </summary>
        private void ChargeCustody(FxAccount acc)
        {
            if (acc == null) return;
            float excess = acc.balance - CustodyFree;
            if (excess <= 0f) return;

            float fee = excess * CustodyRate;
            if (fee < 0.01f) return;

            acc.balance -= fee;
            acc.custodySinceSettle += fee;
            acc.RecordFee("RimForex_FeeCustody".Translate(), fee);
        }

        // ══════════════════════════════════════════════════════════════
        //  0.3.0 借贷体系
        // ══════════════════════════════════════════════════════════════

        /// <summary>四家借贷方的固定上限（银）。</summary>
        public static readonly float[] LenderCap = { 2000f, 8000f, 10000f, 20000f };
        /// <summary>
        /// 四家借贷方的**季**利息率（单利，按本金）。
        /// 只是名义档位；实际按**日**累计，见 <see cref="LenderDailyRate"/>。
        /// </summary>
        public static readonly float[] LenderRate = { 0.05f, 0.10f, 0.20f, 0.30f };

        /// <summary>
        /// 日利率 = 季利息 ÷ 15 日。
        ///
        /// ── 为什么改成按日（2026-10-25）──
        /// 期限从"季"改成"日"（3/6/10 日）之后，如果还按季扣息，
        /// 3 天的贷款根本等不到第一次扣息就到期了 —— 利息形同虚设。
        /// 所以计息周期跟着期限一起缩到"日"，季利率只作为展示档位。
        /// </summary>
        public static float LenderDailyRate(int lender)
        {
            if (lender < 0 || lender >= LenderCount) return 0f;
            return LenderRate[lender] / 15f;
        }
        /// <summary>
        /// 四家借贷方的期限（**游戏日**）。0 = 无期限。
        ///
        /// ── 为什么从"季"改成"天"（2026-10-25 作者反馈）──
        /// 一季 = 15 日，对"周转"来说太长：原版玩法里 15 天足够凑齐还款，
        /// 期限就失去了约束力。改成 3/6/10 日后，"借了要还"才有紧迫感。
        /// 利息仍然按季（15 日）结算，期限与计息周期是两件事，不要混。
        /// </summary>
        public static readonly int[] LenderTermDays = { 3, 6, 10, 0 };
        public const int LenderCount = 4;

        /// <summary>
        /// 某家的实际可借上限：<c>min(固定 × 3, 固定 + 净值 × 0.25)</c>。
        ///
        /// 固定值保底（前期够用），净值加成让后期仍有意义，×3 硬顶防失控。
        /// </summary>
        /// <summary>
        /// 四家借贷方的名称与简介键。
        ///
        /// 写成字面量数组而不是运行时拼接键名：
        /// audit 是逐字面量扫源码的，拼接出来的键它认不出，
        /// 会把这些键当成"XML 里残留、代码里没用"直接判死。
        /// </summary>
        public static readonly string[] LenderNameKeys =
        {
            "RimForex_Loan0Name", "RimForex_Loan1Name", "RimForex_Loan2Name", "RimForex_Loan3Name"
        };
        public static readonly string[] LenderDescKeys =
        {
            "RimForex_Loan0Desc", "RimForex_Loan1Desc", "RimForex_Loan2Desc", "RimForex_Loan3Desc"
        };

        /// <summary>借贷方名称。</summary>
        public static string LenderName(int lender)
        {
            return LenderNameKeys[Mathf.Clamp(lender, 0, LenderCount - 1)].Translate();
        }

        public float LenderLimit(int lender, FxAccount acc)
        {
            if (lender < 0 || lender >= LenderCount) return 0f;
            float cap = LenderCap[lender];
            // ⚠️ 净值必须**扣掉已欠的钱**（2026-10-25 修正反馈）
            //
            // 原来直接用 Equity：借 1000 → 余额 +1000 → 净值 +1000 → 上限 +250
            // → 又能借更多 → 上限再涨……实测连点 8 次，借款额从 1 涨到 14123。
            // 扣掉负债之后，"借"只是把自有权益换成负债，净值不变，上限也不变。
            float net = acc != null ? acc.Equity - TotalOwed(acc) : 0f;
            float equity = Mathf.Max(0f, net);
            return Mathf.Min(cap * 3f, cap + equity * 0.25f);
        }

        /// <summary>该家当前已借本金。</summary>
        public float LenderBorrowed(int lender, FxAccount acc)
        {
            if (acc == null || acc.loans == null) return 0f;
            float sum = 0f;
            for (int i = 0; i < acc.loans.Count; i++)
            {
                var l = acc.loans[i];
                if (l != null && l.lender == lender) sum += l.principal;
            }
            return sum;
        }

        /// <summary>账户的总欠款（本金 + 未付利息）。</summary>
        public float TotalOwed(FxAccount acc)
        {
            if (acc == null || acc.loans == null) return 0f;
            float sum = 0f;
            for (int i = 0; i < acc.loans.Count; i++)
            {
                var l = acc.loans[i];
                if (l != null) sum += l.Owed;
            }
            return sum;
        }

        /// <summary>借款。成功返回 true，失败时 <paramref name="error"/> 是翻译键。</summary>
        public bool TryBorrow(FxAccount acc, int lender, float amount, out string error)
        {
            error = null;
            if (acc == null) { error = "RimForex_LoanNoAccount"; return false; }
            if (lender < 0 || lender >= LenderCount) { error = "RimForex_LoanNoAccount"; return false; }
            if (acc.balance < 0f) { error = "RimForex_LoanNegative"; return false; }

            float available = LenderLimit(lender, acc) - LenderBorrowed(lender, acc);
            if (available < 1f) { error = "RimForex_LoanLimitReached"; return false; }
            amount = Mathf.Floor(amount);
            if (amount < 1f) { error = "RimForex_LoanTooSmall"; return false; }
            if (amount > available) amount = Mathf.Floor(available);

            // ── 借款是现金流，不是盈利 ──
            // 不记进 cashFlowSinceSettle 的话，当日结算会把借来的钱算成"今天赚的"。
            acc.balance += amount;
            acc.cashFlowSinceSettle += amount;

            acc.loans.Add(new FxLoan
            {
                lender = lender,
                principal = amount,
                unpaidInterest = 0f,
                startTick = GenTicks.TicksGame,
                nextInterestTick = GenTicks.TicksGame + TicksPerDay,
                termDays = LenderTermDays[lender],
                unpaidDays = 0,
                // 第一季利息在整整一季后到期
                // （nextDueTick 用 startTick + 一季，见 ProcessLoans）
            });
            // 立刻补上首次到期时间
            acc.loans[acc.loans.Count - 1].startTick = GenTicks.TicksGame;
            return true;
        }

        /// <summary>还款：先冲未付利息，再冲本金。</summary>
        public void RepayLoan(FxAccount acc, FxLoan loan, float amount)
        {
            if (acc == null || loan == null) return;
            amount = Mathf.Min(amount, acc.balance);
            amount = Mathf.Min(amount, loan.Owed);
            if (amount < 0.01f) return;

            acc.balance -= amount;
            acc.cashFlowSinceSettle -= amount;

            float toInterest = Mathf.Min(amount, loan.unpaidInterest);
            loan.unpaidInterest -= toInterest;
            loan.principal -= (amount - toInterest);

            if (loan.Owed <= 0.01f) acc.loans.Remove(loan);
        }

        /// <summary>逐笔检查到期利息。每 250 tick 调一次即可。</summary>
        private void ProcessLoans()
        {
            if (GenTicks.TicksGame % 250 != 0) return;
            if (accounts == null || accounts.Count == 0) return;

            var keys = new List<int>(accounts.Keys);
            for (int k = 0; k < keys.Count; k++)
            {
                var acc = accounts[keys[k]];
                if (acc == null || acc.loans == null || acc.loans.Count == 0) continue;

                for (int i = acc.loans.Count - 1; i >= 0; i--)
                {
                    var loan = acc.loans[i];
                    if (loan == null) { acc.loans.RemoveAt(i); continue; }

                    // ── ① 先补计息（按日）──
                    //
                    // 用 while 而不是 if：低帧率、加速、或读档后可能一次跨过好几天，
                    // 必须把欠下的每一天都补上，否则利息会被悄悄吞掉。
                    // 上限 60 次防止极端存档把主线程卡死（60 天足够任何一笔贷款违约）。
                    int guard = 0;
                    if (loan.nextInterestTick <= 0) loan.nextInterestTick = loan.startTick + TicksPerDay;
                    while (GenTicks.TicksGame >= loan.nextInterestTick && guard++ < 60)
                    {
                        float interest = loan.principal * LenderDailyRate(loan.lender);
                        loan.unpaidInterest += interest;

                        float pay = Mathf.Min(acc.balance, interest);
                        if (pay > 0.01f)
                        {
                            acc.balance -= pay;
                            loan.unpaidInterest -= pay;
                            acc.loanInterestSinceSettle += pay;
                            acc.RecordFee("RimForex_FeeLoan".Translate(), pay);
                            loan.unpaidDays = 0;
                        }
                        else
                        {
                            loan.unpaidDays++;
                        }

                        loan.nextInterestTick += TicksPerDay;
                    }

                    // ── ② 再判违约 ──
                    //
                    // ⚠️ 这一步**不能**放进"利息到期才检查"的分支里（preview1 的 bug）：
                    // 期限是 3 日、计息是 15 日时，3 天的贷款永远等不到检查点，
                    // 结果就是作者实测到的"借了 3 天没有违约、也没扣款"。
                    bool overdue = loan.termDays > 0
                                   && GenTicks.TicksGame - loan.startTick >= loan.termDays * TicksPerDay
                                   && loan.Owed > 0.01f;
                    bool broke = loan.unpaidDays >= 3;

                    if (overdue || broke)
                    {
                        DefaultLoan(acc, loan);
                    }
                }
            }
        }


        /// <summary>
        /// 违约：把欠款一次性打进余额（可能转负），删掉贷款记录。
        ///
        /// 余额转负之后，E7 的收债流程会自然接管 —— 这就是"借钱不还"的下场。
        /// </summary>
        private void DefaultLoan(FxAccount acc, FxLoan loan)
        {
            if (acc == null || loan == null) return;
            float owed = loan.Owed;
            acc.balance -= owed;
            acc.loans.Remove(loan);
            // 用信件而不是角落消息：违约是"钱被一次性划走"的重大事件，
            // 一闪而过的提示玩家根本注意不到（作者 2026-10-25 反馈）。
            Find.LetterStack.ReceiveLetter(
                "RimForex_LoanDefaultLabel".Translate(LenderName(loan.lender)),
                "RimForex_LoanDefault".Translate(LenderName(loan.lender), owed.ToString("F0")),
                LetterDefOf.NegativeEvent);
        }

        /// <summary>
        /// 每日结算：只负责"世界事件"层面的事 —— 统计当日盈亏、发结算信件、给全体殖民者挂心情。
        /// 交易本身是实时的，不依赖这里。
        /// </summary>
        public void DailySettlement()
        {
            EnsureAllFactionCurrencies();

            // ⚠️ 事件检测必须在 AnchorDayOpen 之前 —— 它会把 previousRate 覆盖成当前价，
            // 之后就算不出"今日涨跌"了。
            DetectWorldEvents();

            market.AnchorDayOpen();
            MarkToMarket();

            // E6：更新空头敞口、判定报复；难民按日判定
            UpdateShortExposure();
            ProcessRefugees();

            // E7：负余额连续两天 → 开始收债
            MaybeStartDebtCollection();

            // 还活着的玩家殖民地（key = map.Parent.ID）。
            //
            // 已经不存在的地图**不再结算**：否则废弃殖民地 / 临时地图留下的账户
            // 会跟着存档过一辈子，每天参与结算。
            var liveIds = new HashSet<int>();
            var maps = Find.Maps;
            if (maps != null)
            {
                for (int i = 0; i < maps.Count; i++)
                {
                    var m = maps[i];
                    if (m != null && m.Parent != null && m.IsPlayerHome) liveIds.Add(m.Parent.ID);
                }
            }

            var keys = new List<int>(accounts.Keys);
            for (int k = 0; k < keys.Count; k++)
            {
                var acc = accounts[keys[k]];
                if (acc == null)
                {
                    accounts.Remove(keys[k]);
                    continue;
                }

                // ① 完全空的账户直接剪掉 —— 历史遗留的幽灵账户走这条。
                //    它们以前每 3 天会各发一封「盈亏 0」的结算信。
                if (IsEmptyAccount(acc))
                {
                    accounts.Remove(keys[k]);
                    continue;
                }

                // ② 只结算还活着的殖民地。非空但地图已消失的账户保留数据，
                //    但不再发信、不再改状态（玩家哪天回来还能看到历史）。
                if (!liveIds.Contains(keys[k])) continue;

                // ③ 隔夜利息（0.3.0）：按名义仓位每日收取
                ChargeCarry(acc);

                // ④ 托管费（0.3.0）：对闲置余额收取
                ChargeCustody(acc);

                // ④ 负余额计时（E7 收债用；preview1 只记录，不触发）
                if (acc.balance < 0f)
                {
                    if (acc.negativeSinceTick <= 0) acc.negativeSinceTick = GenTicks.TicksGame;
                }
                else
                {
                    acc.negativeSinceTick = 0;
                }

                ReportDaily(acc);
            }
        }

        /// <summary>
        /// 账户是否"什么都没有"：没余额、没持仓、没交易记录、也没结算基线。
        ///
        /// 这种账户只可能来自旧版本为每张看过 / 停留过的地图建的幽灵账户。
        /// 剪掉它们不影响任何玩家资产 —— 上面任意一条非零即视为非空，一律保留。
        /// </summary>
        private static bool IsEmptyAccount(FxAccount acc)
        {
            return acc.balance < 0.01f
                && acc.positions.Count == 0
                && acc.history.Count == 0
                && System.Math.Abs(acc.equityAtLastSettle) < 0.01f;
        }

        private void ReportDaily(FxAccount acc)
        {
            int openCount = acc.positions.Count;

            // ── 当日盈亏 = 权益的日变化 ──
            //
            // 早先这里写的是 `dayPnl = acc.FloatingPnl`，也就是【此刻持仓】的浮动盈亏，
            // 而且 positions.Count == 0 时直接清零返回。后果：
            //   白天做多赚 5000 并已平仓 → 落袋的钱完全不计；
            //   午夜还挂着一笔浮亏 2000 的仓 → 结算只报「今天亏了 2000」。
            //   若午夜恰好空仓，则无论白天赚亏多少，一律报 0。
            //
            // 权益差一次就涵盖两个口径：
            //   已实现盈亏在平仓时已计入 balance，
            //   浮动盈亏在 FloatingPnl 里，
            //   两者相加即 AccountEquity，其日变化就是"今天到底赚亏多少"。
            // 而且不需要分别累加两个累加器 —— 那种做法容易在爆仓/追加保证金等
            // 路径上漏加或重复加，权益差是天然自洽的。
            //
            // ── 2026-10-17 修正：口径漏了"锁住的保证金" ──
            //
            // 原来这里用的是一个叫 AccountEquity 的属性，定义为 balance + FloatingPnl。
            // 但开仓时是 `account.balance -= margin + fee`（见 OpenPosition），
            // 也就是说 balance 是【可用】资金，**不含已锁定的保证金**。
            // 于是 AccountEquity 少算了整笔占用保证金：
            //     空仓时 balance 6400 → AccountEquity 6400
            //     开仓占用 5831、浮盈 472 后 → AccountEquity = 453 + 472 = 925
            //     结算算出的当日盈亏 = 925 − 6400 = −5475，而真实是 +356
            // 100 倍杠杆下这个误差等于整笔保证金，结算信会报出一个完全不存在的巨亏。
            //
            // `Equity`（balance + MarginUsed + FloatingPnl）才是真正的总权益，
            // 与 UI 顶部"净值"、交易历史导出用的都是同一个口径。
            // 那个错误的 AccountEquity 已删除，避免再被误用。
            float equityNow = acc.Equity;

            // 是否已有基线，由独立标记回答 —— 不能再看数值是否为 0，
            // 因为权益本来就可能真的是 0（满额提取、亏光）。见 settleBaselineSet 的说明。
            if (!acc.settleBaselineSet)
            {
                // 旧存档或首次结算：没有基线，今天记为 0 并从这里开始计。
                acc.equityAtLastSettle = equityNow;
                acc.cashFlowSinceSettle = 0f;
                acc.settleBaselineSet = true;
            }

            // ── 必须扣掉期间净外部现金流（2026-10-18 修）──
            //
            // 权益里含 balance，而存入 / 提取直接增删 balance ——
            // 那是玩家自己搬钱，不是交易赚亏。不扣除的话：
            //     盈利平仓后把余额全部提取 → 权益掉到 0 → 结算报出假亏损
            //     （实测：真实 +56，报出 −1000，误差恰好等于提取额）
            //     存入则相反，会被报成等额的假盈利（存 500、没做任何交易 → 报 +500）
            //
            // 净值变化 = 投资收益 + 外部现金流，所以收益 = 净值变化 − 外部现金流。
            // 这两个字段是一对：snapshot 记"上次结算时的权益"，cashFlow 记
            // "这期间玩家自己进出了多少"，两者都在结算时归零。
            float dayPnl = equityNow - acc.equityAtLastSettle - acc.cashFlowSinceSettle;
            acc.equityAtLastSettle = equityNow;
            acc.cashFlowSinceSettle = 0f;
            acc.lastDayPnl = dayPnl;
            acc.lastDayPositions = openCount;

            // E3：单日暴赚 → 好感 + 概率袭击（preview2）
            MaybeTriggerE3(acc, dayPnl);

            // 结算不再挂心情。心情已改为"按单笔平仓"实时记录（见 RecordCloseMood），
            // 每天结算只负责发一封事件通告信。

            // ── 发信条件（2026-10-23 改，只看"有没有在动"）──
            //
            //   ① 当天动过仓（开或平）      → 发
            //   ② 收盘时还有持仓            → 发（有仓就有玩家关心的浮盈亏）
            //
            // 空仓又没交易的日子彻底静默。原来那种"平淡日累计 3 天补一封摘要"
            // 也删掉了 —— 否则平淡日照样每 3 天冒一封，等于没改。
            //
            // 旧条件是 `|当日盈亏| >= max(200, 净值×1%) || 动过仓`，门槛跟着账户规模涨，
            // 账户一大就天天触发，玩家的感受是"怎么一直在结算"。
            bool tradedToday = acc.activityToday > 0;
            acc.activityToday = 0;

            // ③ 今天被扣过钱（隔夜利息 / 托管费 / 贷款利息）→ 也发
            //
            // 2026-10-25 补：加了托管费之后，空仓又不交易的日子照样会被扣钱，
            // 而原来"没交易不发信"会让玩家看着余额变少却没有任何通知。
            // 费用凭证在交易历史里能查到，但那需要主动去翻。
            float feesToday = acc.carrySinceSettle + acc.custodySinceSettle
                            + acc.loanInterestSinceSettle;
            bool chargedToday = feesToday >= 0.5f;

            if (tradedToday || openCount > 0 || chargedToday)
            {
                PostSettlementLetter(acc, dayPnl, false, openCount);
            }

            // 手续费按结算周期归零。必须放在所有 PostSettlementLetter 调用之后 ——
            // 信里那一行"扣除手续费前 +xxx"要读这个字段。
            acc.feesSinceSettle = 0f;
            acc.carrySinceSettle = 0f;
            acc.loanInterestSinceSettle = 0f;
            acc.custodySinceSettle = 0f;
        }


        /// <summary>每日结算信件。爆仓另有实时信件（见 CheckMarginCalls），不走这里。</summary>
        private static void PostSettlementLetter(FxAccount acc, float pnl, bool marginCall, int openCount)
        {
            string label;
            string text;

            // 结算时刻一律来自 GenDate，和游戏内时钟是同一个值。
            // 写进信件是为了能当场核对：结算必须发生在 0 时。
            int abs = GenTicks.TicksAbs;
            int day = abs / TicksPerDay;
            int hour = GenDate.HourOfDay(abs, 0f);
            string stamp = "RimForex_LetterStamp".Translate(
                (day % 60).ToString(), hour.ToString());

            // 空仓时不能用"持仓中盈亏"那套文案 —— 改完之后空仓也可能有当日盈亏
            // （白天赚了钱、午夜前全平掉），此时再说"X 个持仓"就自相矛盾了。
            bool flat = openCount == 0;
            string pnlStr = System.Math.Abs(pnl).ToString("F0");
            string balStr = acc.balance.ToString("F0");

            // ── 把口径摊开（2026-10-18）──
            // 信里的"今日盈亏"是净额（已扣手续费），而交易历史那列"盈亏"是毛利。
            // 只写一个净额时，玩家会拿它去和 +830 / +42 对照，然后认为算错了。
            // 这里补一行"手续费 + 扣除前金额"，让两个口径当场能对上。
            // 手续费为 0 的日子不印这一行，免得噪音。
            string feeLine = "";
            if (acc.feesSinceSettle >= 0.5f)
            {
                feeLine = "\n\n" + "RimForex_LetterFeeBreakdown".Translate(
                    acc.feesSinceSettle.ToString("F0"),
                    (pnl + acc.feesSinceSettle).ToString("+0;-0;0"));
            }

            // 隔夜利息单独一行：它是"持仓成本"，和开平手续费不是一回事。
            // 混在一起玩家会算不明白"为什么今天什么都没做也在亏"。
            if (acc.carrySinceSettle >= 0.5f)
            {
                feeLine += "\n" + "RimForex_LetterCarry".Translate(
                    acc.carrySinceSettle.ToString("F0"));
            }
            if (acc.loanInterestSinceSettle >= 0.5f)
            {
                feeLine += "\n" + "RimForex_LetterLoanInterest".Translate(
                    acc.loanInterestSinceSettle.ToString("F0"));
            }
            if (acc.custodySinceSettle >= 0.5f)
            {
                feeLine += "\n" + "RimForex_LetterCustody".Translate(
                    acc.custodySinceSettle.ToString("F0"));
            }

            if (pnl >= 0f)
            {
                label = "RimForex_LetterProfit".Translate();
                text = (flat
                    ? "RimForex_LetterProfitTextFlat".Translate(pnlStr, balStr)
                    : "RimForex_LetterProfitText".Translate(pnlStr, openCount, balStr))
                    + feeLine + stamp;
            }
            else
            {
                label = "RimForex_LetterLoss".Translate();
                text = (flat
                    ? "RimForex_LetterLossTextFlat".Translate(pnlStr, balStr)
                    : "RimForex_LetterLossText".Translate(pnlStr, openCount, balStr))
                    + feeLine + stamp;
            }

            LetterDef def = pnl >= 0f ? LetterDefOf.PositiveEvent : LetterDefOf.NeutralEvent;
            Find.LetterStack.ReceiveLetter(label, text, def);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref market, "market");
            Scribe_Collections.Look(ref pendingEvents, "pendingEvents", LookMode.Deep);
            Scribe_Values.Look(ref lastWorldEventTick, "lastWorldEventTick", -999999);
            Scribe_Values.Look(ref platformFactionLoadID, "platformFactionLoadID", 0);
            Scribe_Values.Look(ref nextNewsTick, "nextNewsTick", 0);
            Scribe_Values.Look(ref lastWealthEventTick, "lastWealthEventTick", 0);
            Scribe_Collections.Look(ref refugeePawnIds, "refugeePawnIds", LookMode.Value);
            Scribe_Collections.Look(ref weakenedSettlements, "weakenedSettlements", LookMode.Deep);
            Scribe_Collections.Look(ref retaliations, "retaliations", LookMode.Deep);
            Scribe_Values.Look(ref refugeeDeadlineTick, "refugeeDeadlineTick", 0);
            Scribe_Values.Look(ref debtActive, "debtActive", false);
            Scribe_Values.Look(ref debtMapId, "debtMapId", -1);
            Scribe_Values.Look(ref debtDeadlineTick, "debtDeadlineTick", 0);
            Scribe_Values.Look(ref debtCollectorId, "debtCollectorId", -1);
            if (retaliations == null) retaliations = new List<FxRetaliation>();
            if (pendingEvents == null) pendingEvents = new List<FxPendingEvent>();
            if (refugeePawnIds == null) refugeePawnIds = new List<int>();
            if (weakenedSettlements == null) weakenedSettlements = new List<FxWeakenedSettlement>();
            Scribe_Collections.Look(ref accounts, "accounts", LookMode.Value, LookMode.Deep);
            Scribe_Values.Look(ref nextSettlementTick, "nextSettlementTick", TicksPerDay);
            Scribe_Values.Look(ref lastSettledDay, "lastSettledDay", int.MinValue);
            Scribe_Values.Look(ref lastSettlementAbs, "lastSettlementAbs", int.MinValue);
            Scribe_Values.Look(ref initialized, "initialized", false);

            if (market == null) market = new FxMarket();
            if (accounts == null) accounts = new Dictionary<int, FxAccount>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                initialized = false; // 读档后重新确保货币齐备
            }
        }
    }
}
