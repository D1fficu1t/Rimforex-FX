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
            Rand.PushState(derived);
            float v;
            switch (tier)
            {
                case FxVolTier.Raider: v = Rand.Range(1.15f, 1.25f); break;
                case FxVolTier.Tribal: v = Rand.Range(0.95f, 1.10f); break;
                case FxVolTier.Spacer: v = Rand.Range(0.30f, 0.40f); break;
                case FxVolTier.Medieval: v = Rand.Range(0.65f, 0.85f); break;
                default: v = Rand.Range(0.45f, 0.60f); break;
            }
            Rand.PopState();
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

        /// <summary>行情回到安全区时复位告警状态，将来再恶化会重新提醒。</summary>
        public void ResetWarnState()
        {
            warnedHealth = -1f;
            criticalWarned = false;
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
        /// 保证金健康度：净值 / 原始保证金。1.0 = 打平，0 = 爆仓。
        /// 用 (margin - addedMargin) 作分母，追加保证金不会篡改这个比例的含义。
        /// </summary>
        public float MarginHealth
        {
            get
            {
                float baseMargin = margin - addedMargin;
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
            Scribe_Values.Look(ref criticalWarned, "criticalWarned", false);
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

        public void ExposeData()
        {
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
        /// <summary>累计已付手续费（开仓 + 平仓），仅用于展示成本。</summary>
        public float lifetimeFees;

        /// <summary>
        /// 上一个结算时点的账户权益（余额 + 浮动盈亏）。
        ///
        /// 用来算"当日盈亏"：<b>当日盈亏 = 本次权益 − 上次权益</b>。
        /// 这样一次就涵盖了「白天已平仓的已实现盈亏」与「此刻还挂着的浮动盈亏」，
        /// 不需要分别累加两个口径 —— 分别累加容易漂移，而权益差是天然自洽的。
        ///
        /// 0 表示旧存档还没有这个字段，此时在第一次结算时补记基线。
        /// </summary>
        public float equityAtLastSettle;

        /// <summary>账户权益：可用余额 + 所有持仓的浮动盈亏。</summary>
        public float AccountEquity
        {
            get { return balance + FloatingPnl; }
        }

        /// <summary>已平仓交易记录，最新的在末尾。用于"交易历史"页面。</summary>
        public List<FxTradeRecord> history = new List<FxTradeRecord>();
        /// <summary>连续平淡日计数，用于决定何时补发一封摘要信。</summary>
        public int quietDays;
        /// <summary>历史记录上限，避免长存档无限增长。</summary>
        public const int MaxHistory = 200;

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
        /// 按<b>档位</b>记一笔。爆仓走这里 —— 它的盈亏金额可能落进任意亏损档，
        /// 但玩家该看到的是"爆仓"，不是"亏了若干"。
        /// </summary>
        /// <summary>
        /// 会被本档挤掉的对立档。
        ///
        /// 穿仓与爆仓是同一件事的两个严重程度（穿仓 = 保证金亏光之外还倒欠），
        /// 同一账户不可能同时"爆仓"又"穿仓"。若两条都挂，一次事故吃两份心情
        /// （-20 与 -28 相加 = -48），那不是更真实，只是更狠。
        /// 所以穿仓出现时把爆仓那条顶掉。
        /// </summary>
        private static int SupersededBy(int tier)
        {
            return tier == FxMoodSystem.TierBlowUp ? FxMoodSystem.TierMarginCall : -1;
        }

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

        public float Equity => balance + MarginUsed + FloatingPnl;

        public void ExposeData()
        {
            Scribe_Values.Look(ref balance, "balance", 0f);
            Scribe_Collections.Look(ref positions, "positions", LookMode.Deep);
            Scribe_Values.Look(ref equityAtLastSettle, "equityAtLastSettle", 0f);
            Scribe_Collections.Look(ref moodEntries, "moodEntries", LookMode.Deep);
            Scribe_Values.Look(ref lastDayPnl, "lastDayPnl", 0f);
            Scribe_Values.Look(ref lastDayPositions, "lastDayPositions", 0);
            Scribe_Values.Look(ref activityToday, "activityToday", 0);
            Scribe_Values.Look(ref lifetimeFees, "lifetimeFees", 0f);
            Scribe_Collections.Look(ref history, "history", LookMode.Deep);
            Scribe_Values.Look(ref quietDays, "quietDays", 0);
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

                    // (2) 点差成本：旧版公式对做空恒为 0、对做多会混进价格变动，
                    //     所以凡是没存下点差率的记录一律按"无点差"处理，不再沿用错值。
                    //     新记录（带 spreadRateAtClose）用 units × mid × 点差率 精确复原。
                    t.spreadCost = RecomputeSpreadCost(t);
                }
            }
        }

        /// <summary>
        /// 由 (units, exitRate, spreadRateAtClose, isLong) 复原一笔交易的点差成本。
        ///
        /// 推导：exitRate = mid × (1 ∓ 半价差)，而点差成本 = units × 一个往返点差。
        /// 把 mid 写成 exitRate / (1 ∓ 半价差) 代进去就能消掉方向差异，
        /// 得到统一形式：<c>units × mid × 点差率</c>。
        /// 实测对做多做空都等于 units × (ask − bid)。
        ///
        /// spreadRateAtClose &lt;= 0 表示旧存档没有这个字段 —— 返回 0，
        /// 而不是沿用那个已知错误的旧值。
        /// </summary>
        private static float RecomputeSpreadCost(FxTradeRecord t)
        {
            if (t.spreadRateAtClose <= 0f || t.units == 0 || t.exitRate <= 0f) return 0f;
            float half = t.spreadRateAtClose * 0.5f;
            float denom = t.isLong ? (1f - half) : (1f + half);
            if (denom <= 0.0001f) return 0f;
            float mid = t.exitRate / denom;
            return System.Math.Abs(t.units) * mid * t.spreadRateAtClose;
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
        ///     回本需移动 / 强平距离 = 点差 / StopOutFraction = 点差 / 0.5
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

        // ── 测试专用：下一次 TickStep 强制一次极端跳空 ──
        // 极端跳空的日概率约 0.1%，正常要玩约 1000 游戏日才碰上一次；
        // 而"穿仓"正是本模组最重要的风险展示，靠正常游玩验不到。
        // 这个开关让测试按钮把事件提前引发。
        private bool forceExtremeGap;

        /// <summary>安排下一次报价步发生极端跳空（对所有货币生效）。</summary>
        public void ForceExtremeGap()
        {
            forceExtremeGap = true;
        }

        public void TickStep(int stepStartTick)
        {
            keyBuffer.Clear();

            // 一次性事件：本步对所有货币生效，取走后就清掉，避免连续跳空
            bool forcedGap = forceExtremeGap;
            forceExtremeGap = false;
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
                if (forcedGap)
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
                    + mult * sigma;
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

                float rateBefore = c.rate;
                for (int k = 0; k < SubSteps; k++) AdvanceSubStep(c);
                c.rate = Clamp(Exp(c.stepLogTarget), MinRate, MaxRate);

                // 记下跳空的实际幅度（相对本步开始时的价格），供 UI 提示用
                c.lastGapPct = mult > 0f && rateBefore > 0.001f
                    ? (c.rate / rateBefore - 1f) * 100f
                    : 0f;
            }
        }

        /// <summary>
        /// 推进<b>一个子步</b>的价格，并把新价累加进 K 线。
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
        /// </summary>
        public List<FxCandle> GetCandles(int factionLoadID, int want, FxChartSpan span = FxChartSpan.Minute)
        {
            var list = new List<FxCandle>();
            FxRateSeries s;
            if (!series.TryGetValue(factionLoadID, out s) || s == null) return list;

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
            if (buf == null) return list;

            int from = buf.Count - want;
            if (from < 0) from = 0;
            for (int i = from; i < buf.Count; i++) list.Add(buf[i]);

            // 正在形成中的当前根，让图表实时跟着走
            if (curOpen > 0f)
            {
                float last = currencies.TryGetValue(factionLoadID, out var cur) && cur != null
                    ? cur.rate : curOpen;
                list.Add(new FxCandle(curOpen, curHigh, curLow, last));
            }
            return list;
        }

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
            Rand.PushState(seed);

            FxVolTier tier = FxCurrency.TierFor(faction.def);
            TierStyle style = Pack.For(tier);

            string factionName = faction.Name.NullOrEmpty() ? "" : faction.Name.Trim();
            string name = Compose(style, factionName);

            string symbol = MakeSymbol(name, faction.loadID);

            float rate = Rand.Range(style.RateMin, style.RateMax);

            Rand.PopState();

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
            Rand.PushState(seed);

            FxVolTier tier = FxCurrency.TierFor(faction.def);
            TierStyle style = Pack.For(tier);
            string factionName = faction.Name.NullOrEmpty() ? "" : faction.Name.Trim();

            string name = Compose(style, factionName);
            Rand.PopState();

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
                Rand.PushState(seed);

                TierStyle style = Pack.For(tier);
                string name = Compose(style, fac);
                string symbol = MakeSymbol(name, i * 7);

                Rand.PopState();

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
        public Map map;
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
        /// 结算信件的绝对下限（白银）。当日盈亏的绝对值低于此值就算"平淡日"，不发信。
        ///
        /// 来历说明：Alpha 首版这里是一个裸的 40（`pnlLetterThreshold = 40f`），
        /// 当时账户余额只有一万出头，40 大概是"看得见"的量级 —— 纯粹拍的，没有依据，
        /// 更没随账户规模调整。后来账户涨到 16 万，一天的正常波动轻易几百银，
        /// 于是几乎天天触发发信，玩起来像"怎么一直在结算"。
        /// 现在改成"绝对下限 + 相对比例"取大者，避免再出现固定值跟不上规模的问题。
        /// </summary>
        private const float pnlLetterQuietFloor = 200f;
        /// <summary>
        /// 相对门槛：当日盈亏超过净值的这个比例才算重要日。
        /// 16 万净值下约为 1600 白银，与一天 1.4% 的波动量级匹配。
        /// </summary>
        private const float pnlLetterEquityFraction = 0.01f;
        /// <summary>连续多少个平淡日之后补发一封摘要信，避免完全没反馈。</summary>
        private const int quietDaysBeforeDigest = 3;

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
        ///     健康度 0.70 ⇔ 浮亏 30%
        ///     健康度 0.60 ⇔ 浮亏 40%  ← 本阈值
        ///     健康度 0.50 ⇔ 浮亏 50%  ← 强平线
        /// 取 0.60 是为了给强平线留出 10 个百分点的缓冲，让玩家还有机会追加保证金。
        ///
        /// 不变量：<c>CriticalThreshold &gt; StopOutFraction</c>，
        /// 否则强平会先发生、预警永远不触发。`Docs/audit.py` 会强制检查。
        /// </summary>
        public const float CriticalThreshold = 0.60f;  // 浮亏 40%

        /// <summary>
        /// 强平线（Stop-Out Level）：净值跌到"已用保证金 × 这个比例"时强制平仓，
        /// 即浮亏 50%。
        ///
        /// 真实海外 FX 平台的强平线普遍设在保证金水平的 <b>20% ~ 50%</b>，
        /// 而不是 0%。参照：
        ///   - Axi      https://help.axi.com/zh-TW/axiv2--axicorp-prod/article/auEr1HqT-
        ///   - Pepperstone   https://pepperstone.com/
        ///   - IC Markets    https://www.icmarkets.com.au/
        /// 取 50%（三者中最宽松的一档）：对玩家最友好，同时保留"不会归零"的真实性。
        /// 平仓时剩余的净值会退还账户，损失的只是 50% 保证金。
        /// </summary>
        public const float StopOutFraction = 0.5f;

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
        ///   约束 2（被日波动打死）：强平距离 0.5/杠杆 ≥ N × 日波动
        ///                        → 杠杆 ≤ 0.5 / (N × 日波动)
        ///
        /// 通常约束 2 更紧，因为高波动品种的日波动远大于点差。
        /// 这就是为什么"高波动货币只能配低杠杆"—— 它是波动率的直接后果，
        /// 不是人为限制。真实平台对新兴市场货币也是这个逻辑。
        /// </summary>
        public static float MaxSafeLeverageFor(FxCurrency c)
        {
            if (c == null) return 1f;

            // 约束 1
            float sr = FxMarket.SpreadRateFor(c);
            float bySpread = sr <= 0f ? 100f : MaxSpreadImpactOnMargin / sr;

            // 约束 2
            float dailyVol = FxMarket.DailyVol * (c.volatility > 0.01f ? c.volatility : 1f);
            float byVol = dailyVol > 0.0001f
                ? StopOutFraction / (StopOutVolCoverage * dailyVol)
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

        /// <summary>待处理的"临近爆仓"预警。key = 持仓的 openTick（同一存档内唯一）。</summary>
        public Dictionary<int, MarginWarning> pendingWarnings = new Dictionary<int, MarginWarning>();

        public FxGameComponent(World world) : base(world)
        {
        }

        public FxAccount AccountFor(Map map)
        {
            int id = (map != null && map.Parent != null) ? map.Parent.ID : 0;
            FxAccount acc;
            if (!accounts.TryGetValue(id, out acc))
            {
                acc = new FxAccount();
                accounts[id] = acc;
            }
            return acc;
        }

        /// <summary>确保所有已知派系的货币都已生成。惰性调用，避免开局卡顿。</summary>
        public void EnsureAllFactionCurrencies()
        {
            var all = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                var f = all[i];
                if (f == null || f.def == null) continue;
                if (f.def.isPlayer) continue;
                if (f.def.hidden) continue;
                market.EnsureCurrency(f);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  测试专用板块（模组设置里两个按钮）
        //
        //  为什么需要：
        //    · 心情的叠加与过期要跨游戏日才看得出来，正常玩一次要等十几个小时；
        //    · 穿仓要等极端跳空，日概率约 0.1%，正常要玩约 1000 游戏日。
        //  两者都是"必须验证但正常游玩验不到"的路径，所以给测试入口。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>下次强制极端跳空的 tick。&lt;= 0 表示没有待触发的事件。</summary>
        private int forceGapAtTick = -1;

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

        /// <summary>安排一次强制极端跳空（几游戏时后生效）。</summary>
        public void ScheduleForcedGap()
        {
            forceGapAtTick = GenTicks.TicksGame + ForceGapDelayHours * TicksPerHour;
        }

        /// <summary>到点就触发。由 WorldComponentTick 每帧调用。</summary>
        private void CheckForcedGap()
        {
            if (forceGapAtTick <= 0 || GenTicks.TicksGame < forceGapAtTick) return;
            forceGapAtTick = -1;
            market.ForceExtremeGap();
            Messages.Message("RimForex_MsgForceGap".Translate(),
                MessageTypeDefOf.ThreatBig, false);
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
            FxAccount acc = AccountFor(m);
            if (acc == null) return;
            acc.RecordMoodTier(tier);
        }

        /// <summary>测试用：当前地图的账户（可能为 null）。</summary>
        public FxAccount DebugCurrentAccount
        {
            get
            {
                Map m = Find.CurrentMap;
                return m != null ? AccountFor(m) : null;
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
                // 万一积压超过 8 步，直接对齐，避免永远追不上
                if (now - lastStepTick >= FxMarket.TicksPerStep) lastStepTick = now;

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
        /// 只有幅度达到阈值才提示 —— 跳空每游戏日约 5.8% 概率，
        /// 若每次都刷消息会淹没其他信息。阈值取 0.5%，
        /// 低波动品种的小跳空会被过滤掉，高波动品种的大跳空才会出现。
        /// </summary>
        private void AnnounceGaps()
        {
            const float AnnounceThresholdPct = 0.5f;
            foreach (FxCurrency c in market.AllCurrencies)
            {
                if (c == null) continue;
                if (System.Math.Abs(c.lastGapPct) < AnnounceThresholdPct) continue;

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
                    float stopOutLevel = pos.margin * StopOutFraction;

                    // 还没触及强平线：只有确实处于亏损状态才考虑预警
                    if (eq > stopOutLevel)
                    {
                        MaybeWarn(pos, acc, accKey);
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
                    acc.lifetimeFees += closeFee;
                    // 注意：穿仓判据用【扣费后的余额】，因为那才是账户的真实状态
                    bool blewUp = acc.balance < 0f;
                    acc.positions.RemoveAt(i);
                    pendingWarnings.Remove(pos.openTick);
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
        private void MaybeWarn(FxPosition pos, FxAccount acc, int accKey)
        {
            float health = pos.MarginHealth;

            // 只在确实处于亏损状态时才预警。
            // （锁定期内盈亏恒为 0，因此开仓阶段不可能触发预警。）
            if (pos.Pnl >= 0f)
            {
                pendingWarnings.Remove(pos.openTick);
                pos.ResetWarnState();
                return;
            }

            // 反弹回安全区：复位，将来再恶化会重新提醒一次
            if (health > CriticalThreshold)
            {
                pendingWarnings.Remove(pos.openTick);
                pos.ResetWarnState();
                return;
            }

            // 已经提醒过就不再弹，直到上面把它复位
            if (pos.criticalWarned) return;

            pos.criticalWarned = true;
            pos.warnedHealth = health;
            EnqueueWarning(pos, acc, accKey);
        }

        private void EnqueueWarning(FxPosition pos, FxAccount acc, int accKey)
        {
            Map map = null;
            var maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                if (maps[m] != null && maps[m].Parent != null && maps[m].Parent.ID == accKey)
                {
                    map = maps[m];
                    break;
                }
            }

            pendingWarnings[pos.openTick] = new MarginWarning
            {
                account = acc,
                position = pos,
                map = map
            };
        }

        /// <summary>
        /// 把排队的预警变成可见的界面。放在 WorldComponentOnGUI 而不是终端窗口里，
        /// 这样即使玩家没打开交易终端，临近爆仓也会被通知到。
        /// </summary>
        public override void WorldComponentOnGUI()
        {
            if (pendingWarnings.Count == 0) return;
            if (Find.WindowStack == null) return;
            if (Find.WindowStack.IsOpen(typeof(Window_MarginCall))) return;
            if (Current.ProgramState != ProgramState.Playing) return;

            int key = -1;
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

        /// <summary>
        /// 每日结算：只负责"世界事件"层面的事 —— 统计当日盈亏、发结算信件、给全体殖民者挂心情。
        /// 交易本身是实时的，不依赖这里。
        /// </summary>
        public void DailySettlement()
        {
            EnsureAllFactionCurrencies();
            market.AnchorDayOpen();
            MarkToMarket();

            var keys = new List<int>(accounts.Keys);
            for (int k = 0; k < keys.Count; k++)
            {
                var acc = accounts[keys[k]];
                if (acc == null) continue;
                ReportDaily(acc);
            }
        }

        private void ReportDaily(FxAccount acc)
        {
            int openCount = acc.positions.Count;
            float equityNow = acc.AccountEquity;

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
            if (acc.equityAtLastSettle == 0f)
            {
                // 旧存档或首次结算：没有基线，只能把今天记为 0 并从这里开始计。
                acc.equityAtLastSettle = equityNow;
            }

            float dayPnl = equityNow - acc.equityAtLastSettle;
            acc.equityAtLastSettle = equityNow;
            acc.lastDayPnl = dayPnl;
            acc.lastDayPositions = openCount;

            // 结算不再挂心情。心情已改为"按单笔平仓"实时记录（见 RecordCloseMood），
            // 每天结算只负责发一封事件通告信。

            // 发信门槛：只有"真正值得看一眼"的日才算重要日。
            // 早期用 |盈亏| >= 40，在 20 万级别的账户上几乎天天触发，
            // 玩家的感受就是"怎么一直在结算" —— 其实结算确实每天一次，是信件太吵。
            // 现在改成按账户规模取相对阈值，并且平淡日累计到一定天数才补一封。
            float threshold = System.Math.Max(pnlLetterQuietFloor, acc.Equity * pnlLetterEquityFraction);

            // 取走并清零当天的动作计数（结算每天只跑一次，这里读的就是"昨天那一段"）
            bool tradedToday = acc.activityToday > 0;
            acc.activityToday = 0;

            // 发信条件：盈亏过了门槛，或者当天确实动过仓。
            // 后者很重要 —— 玩家平了一笔小赚，若因为没过门槛就不吭声，会以为结算坏了。
            bool notable = System.Math.Abs(dayPnl) >= threshold || tradedToday;

            if (notable)
            {
                PostSettlementLetter(acc, dayPnl, false, openCount);
                acc.quietDays = 0;
            }
            else
            {
                acc.quietDays++;
                if (acc.quietDays >= quietDaysBeforeDigest)
                {
                    PostSettlementLetter(acc, dayPnl, false, openCount);
                    acc.quietDays = 0;
                }
            }
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

            if (pnl >= 0f)
            {
                label = "RimForex_LetterProfit".Translate();
                text = (flat
                    ? "RimForex_LetterProfitTextFlat".Translate(pnlStr, balStr)
                    : "RimForex_LetterProfitText".Translate(pnlStr, openCount, balStr)) + stamp;
            }
            else
            {
                label = "RimForex_LetterLoss".Translate();
                text = (flat
                    ? "RimForex_LetterLossTextFlat".Translate(pnlStr, balStr)
                    : "RimForex_LetterLossText".Translate(pnlStr, openCount, balStr)) + stamp;
            }

            LetterDef def = pnl >= 0f ? LetterDefOf.PositiveEvent : LetterDefOf.NeutralEvent;
            Find.LetterStack.ReceiveLetter(label, text, def);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref market, "market");
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
