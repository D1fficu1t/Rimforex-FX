using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 模组设置。
    ///
    /// 分两类：
    ///   1. <b>界面提示开关</b> —— 只控制文字显示，不影响任何数值。
    ///   2. <b>玩法参数</b> —— 真正改变行情节奏与风险，见下半部分。
    /// </summary>
    public class RimForexSettings : ModSettings
    {
        // ================= 界面提示开关（不影响数值） =================

        /// <summary>K 线区：报价节奏说明。</summary>
        public bool showQuotePacingHint = true;
        /// <summary>K 线区：持仓线图例（说明虚线是开仓成交价、为什么与中间价差半个点差）。</summary>
        public bool showChartEntryLegend = true;
        /// <summary>K 线区：盈亏平衡线（"回本 xxx"）。仓位多、线杂时可以关掉。</summary>
        public bool showBreakevenLine = true;
        /// <summary>K 线区：强平线（"爆仓 xxx"）。显示当前持仓会在哪个价位被强制平仓。</summary>
        public bool showStopOutLine = true;
        /// <summary>下单区：逐行成本明细。</summary>
        public bool showCostBreakdown = true;
        /// <summary>底部时间条：游戏速度说明。</summary>
        public bool showTimeSpeedHint = true;
        /// <summary>账户区：结算倒计时。</summary>
        public bool showSettlementCountdown = true;
        /// <summary>账户区：时间诊断行（排查时间问题用）。</summary>
        public bool showTimeDebug = false;

        // ================= 悬浮盈亏窗（HUD） =================

        /// <summary>
        /// 是否显示悬浮盈亏窗。默认开。
        ///
        /// 用途：关掉交易终端去干别的时，仍能在屏幕角落看到当前持仓的浮动盈亏。
        /// 交易终端本身自带"浮动盈亏"，但它只在窗口打开时可见 ——
        /// 而玩家真正需要盯盘的时刻，恰恰是在基地里忙别的事的时候。
        /// </summary>
        public bool showFloatingPnl = true;

        /// <summary>
        /// 悬浮窗左上角位置（屏幕坐标）。可拖动，位置记在这里。
        ///
        /// 用 -1 表示"还没被拖过"，此时用默认位置（右上角）。
        /// 之所以不做成固定位置：这类 HUD 元素必然会和别的 mod 的 HUD 撞，
        /// 而玩家各自装的 mod 又完全不同 —— 只有可拖动才能一次解决。
        /// </summary>
        public float floatPnlX = -1f;
        public float floatPnlY = -1f;

        // ================= 玩法参数（会改变行情与经济） =================

        /// <summary>
        /// 报价间隔（现实秒，1x 速度下）。1 ~ 10，默认 3。
        ///
        /// 这是<b>节奏</b>旋钮：越短行情越活跃。
        /// 注意它不影响日波动率 —— 波动按 1/√StepsPerDay 缩放，
        /// 一天累计下来始终等于 DailyVol。改的只是"多少次跳动"。
        /// </summary>
        public float secondsPerQuote = 3f;

        /// <summary>
        /// 多少次报价合成一根 K 线。3 ~ 10，默认 6。
        ///
        /// 只影响图表可读性，不影响行情本身。
        /// </summary>
        public int quotesPerCandle = 6;

        /// <summary>
        /// 跳空强度 0 ~ 1。0 = 偏真实（跳空罕见），1 = 偏赌狗（跳空频繁且凶狠）。
        ///
        /// 这是<b>风险</b>旋钮，映射方式见 <see cref="FxMarket.GapChancePerDay"/>。
        /// 注意：即使拉到最赌狗，churn 的期望仍为负 ——
        /// 跳空放大的是双向尾部风险，不是让机械交易变赚钱。
        /// </summary>
        public float gapIntensity = 0.5f;

        /// <summary>
        /// 货币名字的全局种子。
        ///
        /// 派系货币名 = f(本值, 派系 loadID)，所以：
        ///   - 同一次游戏里所有派系的名字稳定不变
        ///   - 点"重掷货币名"换一个本值，所有货币名一起换新
        /// 默认 0，所以老存档第一次升级上来时名字会保持与旧逻辑不同的新风格。
        /// </summary>
        public int nameSeed;

        /// <summary>
        /// 慢周期行情强度（对数空间）。0 = 关闭（退回纯均值回归）。
        ///
        /// 锚价按 <c>amp × sin(2πt/T + φ)</c> 缓慢摆动，让汇率走出跨越多个 K 线的趋势。
        /// 取 0.02 ≈ 半个周期内摆动约 2%；0.035 约 3.5%。
        ///
        /// ⚠️ 调大它会让可视窗内的总幅度被放大（0.02 约 2 倍、0.035 约 3.7 倍），
        /// 而强平距离是固定的 0.5%，所以<b>幅度越大越容易被强平</b>。
        /// </summary>
        public float trendAmp = 0.02f;

        /// <summary>慢周期行情的周期，单位游戏日。6 日 ≈ 半个周期 3 日的单边行情。</summary>
        public float trendPeriodDays = 6f;
        // ================= 平仓心情强度 =================
        // 基准值固定在 FxMoodSystem.TierMood：
        //   巨亏 -15 / 大亏 -10 / 中亏 -7 / 小亏 -5
        //   小赚 +3 / 中赚 +5 / 大赚 +8 / 巨赚 +12 / 爆仓 -20
        // 这里只给整体倍率，不必逐档调 9 个滑块。
        // 注意叠层会翻倍：巨亏三层满 = -45，爆仓三层满 = -60。
        // 觉得太狠就往下调；调到 0 等于关掉该类心情。

        /// <summary>亏损档心情倍率（1 = 基准，0 = 关闭亏损心情）。</summary>
        public float lossMoodScale = 1f;
        /// <summary>盈利档心情倍率。</summary>
        public float gainMoodScale = 1f;
        /// <summary>爆仓心情倍率。单独一档，因为它最狠。</summary>
        public float marginCallMoodScale = 1f;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref showQuotePacingHint, "showQuotePacingHint", true);
            Scribe_Values.Look(ref showChartEntryLegend, "showChartEntryLegend", true);
            Scribe_Values.Look(ref showBreakevenLine, "showBreakevenLine", true);
            Scribe_Values.Look(ref showStopOutLine, "showStopOutLine", true);
            Scribe_Values.Look(ref lossMoodScale, "lossMoodScale", 1f);
            Scribe_Values.Look(ref gainMoodScale, "gainMoodScale", 1f);
            Scribe_Values.Look(ref marginCallMoodScale, "marginCallMoodScale", 1f);
            Scribe_Values.Look(ref showCostBreakdown, "showCostBreakdown", true);
            Scribe_Values.Look(ref showTimeSpeedHint, "showTimeSpeedHint", true);
            Scribe_Values.Look(ref showSettlementCountdown, "showSettlementCountdown", true);
            Scribe_Values.Look(ref showTimeDebug, "showTimeDebug", false);

            // 悬浮盈亏窗（2026-10-20）
            Scribe_Values.Look(ref showFloatingPnl, "showFloatingPnl", true);
            Scribe_Values.Look(ref floatPnlX, "floatPnlX", -1f);
            Scribe_Values.Look(ref floatPnlY, "floatPnlY", -1f);

            Scribe_Values.Look(ref secondsPerQuote, "secondsPerQuote", 3f);
            Scribe_Values.Look(ref quotesPerCandle, "quotesPerCandle", 6);
            Scribe_Values.Look(ref gapIntensity, "gapIntensity", 0.5f);
            Scribe_Values.Look(ref nameSeed, "nameSeed", 0);
            Scribe_Values.Look(ref trendAmp, "trendAmp", 0.02f);
            Scribe_Values.Look(ref trendPeriodDays, "trendPeriodDays", 6f);

            ClampAll();
        }

        /// <summary>
        /// 把三个旋钮夹回合法范围。
        /// 读档后必须调用 —— 手改过配置文件、或旧版本遗留的值都可能越界，
        /// 而越界的 secondsPerQuote 会让 TicksPerStep 变成 0 并直接除零崩溃。
        /// </summary>
        /// <summary>心情倍率的取值范围。下限 0 = 完全关掉该类心情。</summary>
    public const float MinMoodScale = 0f;
    public const float MaxMoodScale = 2f;

    public void ClampAll()
        {
            if (secondsPerQuote < MinSecondsPerQuote) secondsPerQuote = MinSecondsPerQuote;
            if (secondsPerQuote > MaxSecondsPerQuote) secondsPerQuote = MaxSecondsPerQuote;
            if (quotesPerCandle < MinQuotesPerCandle) quotesPerCandle = MinQuotesPerCandle;
            if (quotesPerCandle > MaxQuotesPerCandle) quotesPerCandle = MaxQuotesPerCandle;
            if (gapIntensity < 0f) gapIntensity = 0f;
            if (gapIntensity > 1f) gapIntensity = 1f;

            // 慢周期：0 是合法的（= 关闭）；上限防止手改配置把幅度推到荒谬水平
            if (trendAmp < 0f) trendAmp = 0f;
            if (trendAmp > MaxTrendAmp) trendAmp = MaxTrendAmp;
            if (trendPeriodDays < MinTrendPeriodDays) trendPeriodDays = MinTrendPeriodDays;
            if (trendPeriodDays > MaxTrendPeriodDays) trendPeriodDays = MaxTrendPeriodDays;

            // 0 是合法的：等于把该类心情整个关掉
            if (lossMoodScale < MinMoodScale) lossMoodScale = MinMoodScale;
            if (lossMoodScale > MaxMoodScale) lossMoodScale = MaxMoodScale;
            if (gainMoodScale < MinMoodScale) gainMoodScale = MinMoodScale;
            if (gainMoodScale > MaxMoodScale) gainMoodScale = MaxMoodScale;
            if (marginCallMoodScale < MinMoodScale) marginCallMoodScale = MinMoodScale;
            if (marginCallMoodScale > MaxMoodScale) marginCallMoodScale = MaxMoodScale;
        }

        public const float MinSecondsPerQuote = 1f;
        public const float MaxSecondsPerQuote = 10f;
        public const int MinQuotesPerCandle = 3;
        public const int MaxQuotesPerCandle = 10;

        /// <summary>慢周期强度上限。0.08 已经会让可视幅度放大到约 8 倍，再多就失控了。</summary>
        public const float MaxTrendAmp = 0.08f;
        /// <summary>慢周期下限（游戏日）。太短会让行情变成快速来回摆动，而不是趋势。</summary>
        public const float MinTrendPeriodDays = 1f;
        /// <summary>慢周期上限（游戏日）。</summary>
        public const float MaxTrendPeriodDays = 60f;

        public void ResetToDefaults()
        {
            showQuotePacingHint = true;
            showChartEntryLegend = true;
            showBreakevenLine = true;
            showStopOutLine = true;
            lossMoodScale = 1f;
            gainMoodScale = 1f;
            marginCallMoodScale = 1f;
            showCostBreakdown = true;
            showTimeSpeedHint = true;
            showSettlementCountdown = true;
            showTimeDebug = false;

            showFloatingPnl = true;
            floatPnlX = -1f;
            floatPnlY = -1f;

            secondsPerQuote = 3f;
            quotesPerCandle = 6;
            gapIntensity = 0.5f;
            nameSeed = 0;
            trendAmp = 0.02f;
            trendPeriodDays = 6f;
        }
    }

    /// <summary>在模组列表里提供设置界面。</summary>
    public class RimForexMod : Mod
    {
        public static RimForexSettings Settings;

        /// <summary>设置界面里"生成一批"按钮的最近结果，纯展示用，不持久化。</summary>
        /// <summary>设置页的滚动位置。设置页会被重建，所以存成静态。</summary>
        private static Vector2 settingsScroll;
        /// <summary>滚动内容区的预留高度。画完后会按实测值自动上调，不需要手算。</summary>
        private static float settingsViewH = 1500f;

        private static readonly System.Collections.Generic.List<string> NameSamples
            = new System.Collections.Generic.List<string>();

        /// <summary>
        /// 本模组的 Mod 实例（2026-10-23 加）。
        /// 供"从交易终端直接打开模组设置"用 —— `Dialog_ModSettings` 的构造函数要一个 Mod。
        /// 原版不提供静态入口，只能在构造时自己存一份。
        /// </summary>
        public static RimForexMod Instance;

        public RimForexMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimForexSettings>();
            Instance = this;
        }

        public override string SettingsCategory()
        {
            return "RimForex";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // ── 滚动单列 ──
            //
            // 这里踩过两次坑，记下来免得再犯：
            //
            // ① 最初直接 `l.Begin(inRect)`。Listing 默认把 ColumnWidth 设成整个宽度，
            //    而内容一旦超过可用高度就会自动 NewColumn()，位置变成
            //    `curX += ColumnWidth + 17` —— 从 2×宽度 处开始画，整列都在窗口右侧之外。
            //    现象就是"设置页不能滑动，下面的选项全不见了"。
            // ② 于是改成"显式设列宽 + 硬分两列"。那只是把溢出点往后推了一格 ——
            //    **再加设置照样会溢出**，治标不治本。
            //
            // 正确做法：套一层 ScrollView，列高给足，永远单列往下排。
            // 这样加任何数量的设置项都不会被挤出页面，只是需要往下滚。
            //
            // ③ 但"给足"这个说法骗了我一次：原来写死 `ViewH = 1500f`，
            //    靠手算内容高度来保证不超。手算一旦偏低，Listing 就会触发
            //    `NewColumnIfNeeded` 拐到第二列，或者内容被 ScrollView 裁掉 ——
            //    玩家滚到底也找不到后面的选项（测试按钮就这么"消失"过）。
            //
            //    现在两道保险：
            //      · `maxOneColumn = true` 彻底禁掉自动分列（双保险，见下）
            //      · 画完后拿 `l.CurHeight` 实测内容高度，超了就扩容并在同帧重画一次。
            //        于是高度自动跟随内容，不需要任何人再去估算。
            if (settingsViewH < 400f) settingsViewH = 400f;   // 兜底：首次进入时给个下限

            for (int pass = 0; pass < 2; pass++)
            {
                float viewH = pass == 0 ? settingsViewH : settingsViewH + 400f;
                Rect view = new Rect(0f, 0f, inRect.width - 20f, viewH);

                Widgets.BeginScrollView(inRect, ref settingsScroll, view);
                var l = new Listing_Standard();
                l.ColumnWidth = view.width - 8f;
                // 绝不允许自动分列 —— 分列会把后半段画到窗口右侧之外
                l.maxOneColumn = true;
                l.Begin(new Rect(0f, 0f, view.width, viewH));

                DrawSettingsBody(l);

                // 实测内容高度：超出预留就扩大并重画一次。
                // 用 MaxColumnHeightSeen 而不是 CurHeight —— 后者就是 curY，
                // 万一哪里真的重置了列，curY 会回退，而 Max 不会。
                float used = l.MaxColumnHeightSeen + 24f;
                l.End();
                Widgets.EndScrollView();

                if (used > viewH && pass == 0)
                {
                    settingsViewH = used;
                    continue;      // 同帧重画，玩家察觉不到
                }
                break;
            }
        }

        /// <summary>设置页正文。拆出来是为了能在同一帧里重画一次（见调用处的说明）。</summary>
        private void DrawSettingsBody(Listing_Standard l)
        {
            // ---- 版本号（放最上面，玩家截图就能带上）----
            // 订阅者报 bug 时最常见的一轮往返就是"你装的是哪一版"。
            // 显示在这里、不用滚动就能看到，能省掉那一轮。
            // 完整值（含 40 位提交哈希）仍然写在 Player.log 的加载行里。
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            l.Label("RimForex_SettingsVersion".Translate(RimForexBootstrap.VersionLabel));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // ---- 测试专用（放最上面，因为它就是要频繁按的）----
            // 这两个按钮覆盖的是"必须验证但正常游玩验不到"的路径：
            // 心情叠加/过期要跨游戏日，穿仓要等约 1000 游戏日一次。
            // 详见 Core.cs 里测试板块的说明。
            //
            // ⚠️ 曾经放在页面最底部，结果测试时每次都要滚过整页才找得到；
            // 更糟的是当时内容高度是写死的，超出的部分被 ScrollView 裁掉，
            // 于是按钮**根本看不到**。高度现已改成画完实测、自动扩容。
            l.Gap(12f);
            Text.Font = GameFont.Medium;
            l.Label("RimForex_SettingsTest".Translate());
            Text.Font = GameFont.Small;
            l.GapLine();
            l.Label("RimForex_SettingsTestDesc".Translate());
            l.Gap(6f);

            if (l.ButtonText("RimForex_TestMood".Translate()))
            {
                FxGameComponent fx = Find.World != null
                    ? Find.World.GetComponent<FxGameComponent>() : null;
                if (fx != null)
                {
                    // 固定塞"小亏"档：它是叠加上限与过期最容易观察的一档（时长 0.75 天）
                    fx.DebugAddMood(FxMoodSystem.TierSmallLoss);
                    Messages.Message("RimForex_MsgTestMood".Translate(),
                        MessageTypeDefOf.TaskCompletion, false);
                }
            }
            Hint(l, "RimForex_TestMoodTip".Translate());

            l.Gap(6f);
            if (l.ButtonText("RimForex_TestBlowUp".Translate()))
            {
                FxGameComponent fx = Find.World != null
                    ? Find.World.GetComponent<FxGameComponent>() : null;
                if (fx != null)
                {
                    fx.ScheduleForcedGap();
                    Messages.Message(
                        "RimForex_MsgTestBlowUp".Translate(FxGameComponent.ForceGapDelayHours),
                        MessageTypeDefOf.ThreatBig, false);
                }
            }
            Hint(l, "RimForex_TestBlowUpTip".Translate(FxGameComponent.ForceGapDelayHours));

            // ---- 常规跳空（验证播报，不会穿仓）----
            // 上一版只有一个"极端跳空"按钮，普通跳空靠等——而普通跳空恰恰是
            // 2026-10-21 那次阈值重设计的对象，必须有办法主动验证。
            l.Gap(6f);
            if (l.ButtonText("RimForex_TestNormalGap".Translate()))
            {
                FxGameComponent fx = Find.World != null
                    ? Find.World.GetComponent<FxGameComponent>() : null;
                if (fx != null)
                {
                    fx.ScheduleForcedNormalGap();
                    Messages.Message(
                        "RimForex_MsgTestNormalGap".Translate(FxGameComponent.ForceGapDelayHours),
                        MessageTypeDefOf.NeutralEvent, false);
                }
            }
            Hint(l, "RimForex_TestNormalGapTip".Translate(FxGameComponent.ForceGapDelayHours));

            // ---- 清理幽灵货币 ----
            // 临时派系（boss 团、圣物朝圣者……）永远留在派系表里，
            // 旧版本把它们也造成了货币。每日结算会自动清，但这个按钮
            // 让玩家读完旧档后立刻清干净，不用等到下一个结算日。
            l.Gap(6f);
            if (l.ButtonText("RimForex_TestClearGhost".Translate()))
            {
                FxGameComponent fx = Find.World != null
                    ? Find.World.GetComponent<FxGameComponent>() : null;
                if (fx != null)
                {
                    int blocked;
                    int n = fx.DebugClearGhostCurrencies(out blocked);
                    if (n > 0)
                    {
                        Messages.Message("RimForex_MsgGhostCleared".Translate(n, blocked),
                            MessageTypeDefOf.TaskCompletion, false);
                    }
                    else if (blocked > 0)
                    {
                        // 有幽灵，但都被未平仓的仓位占着 —— 说清楚为什么没清
                        Messages.Message("RimForex_MsgGhostBlocked".Translate(blocked),
                            MessageTypeDefOf.RejectInput, false);
                    }
                    else
                    {
                        Messages.Message("RimForex_MsgGhostNone".Translate(),
                            MessageTypeDefOf.RejectInput, false);
                    }
                }
            }
            Hint(l, "RimForex_TestClearGhostTip".Translate());

            // ---- 玩法参数 ----
            Text.Font = GameFont.Medium;
            l.Label("RimForex_SettingsGameplay".Translate());
            Text.Font = GameFont.Small;
            l.GapLine();
            l.Label("RimForex_SettingsGameplayDesc".Translate());
            l.Gap(6f);

            // 报价间隔：滑块用整数百分比避免浮点抖动
            float sec = Settings.secondsPerQuote;
            l.Label("RimForex_Set_QuoteInterval".Translate(sec.ToString("0.0")));
            sec = l.Slider(sec, RimForexSettings.MinSecondsPerQuote, RimForexSettings.MaxSecondsPerQuote);
            Settings.secondsPerQuote = Mathf.Round(sec * 10f) / 10f;
            Hint(l, "RimForex_Set_QuoteIntervalTip".Translate(
                FxMarket.TicksPerStep.ToString(),
                FxMarket.StepsPerDay.ToString(),
                FxMarket.SubSteps.ToString()));

            // 每根 K 线几次报价
            l.Gap(4f);
            l.Label("RimForex_Set_QuotesPerCandle".Translate(Settings.quotesPerCandle.ToString()));
            float qpc = l.Slider(Settings.quotesPerCandle,
                RimForexSettings.MinQuotesPerCandle, RimForexSettings.MaxQuotesPerCandle);
            Settings.quotesPerCandle = Mathf.RoundToInt(qpc);
            Hint(l, "RimForex_Set_QuotesPerCandleTip".Translate(
                (FxMarket.TicksPerCandle / (float)FxGameComponent.TicksPerRealSecond).ToString("0.#")));

            // 跳空强度
            l.Gap(4f);
            string gapLabel = Settings.gapIntensity < 0.34f
                ? "RimForex_GapRealistic".Translate().ToString()
                : Settings.gapIntensity < 0.67f
                    ? "RimForex_GapBalanced".Translate().ToString()
                    : "RimForex_GapDegenerate".Translate().ToString();
            l.Label("RimForex_Set_GapIntensity".Translate(
                (Settings.gapIntensity * 100f).ToString("0"), gapLabel));
            Settings.gapIntensity = l.Slider(Settings.gapIntensity, 0f, 1f);
            Hint(l, "RimForex_Set_GapIntensityTip".Translate(
                (FxMarket.GapChancePerDay * 100f).ToString("0.0"),
                (FxMarket.ExtremeGapChancePerDay * 100f).ToString("0.00")));

            // ── 慢周期行情（锚价正弦摆动）──
            // 决定"图上能不能看出趋势"。设为 0 就是关闭，退回原来的纯均值回归。
            l.Gap(10f);
            l.Label("RimForex_Set_TrendAmp".Translate(
                (Settings.trendAmp * 100f).ToString("0.0"),
                Settings.trendAmp <= 0.0001f
                    ? "RimForex_TrendOff".Translate().ToString()
                    : "RimForex_TrendOn".Translate().ToString()));
            Settings.trendAmp = l.Slider(Settings.trendAmp, 0f, RimForexSettings.MaxTrendAmp);
            Hint(l, "RimForex_Set_TrendAmpTip".Translate(TrendSwingPct().ToString("0.0")));

            l.Gap(4f);
            l.Label("RimForex_Set_TrendPeriod".Translate(Settings.trendPeriodDays.ToString("0.#")));
            Settings.trendPeriodDays = l.Slider(Settings.trendPeriodDays,
                RimForexSettings.MinTrendPeriodDays, RimForexSettings.MaxTrendPeriodDays);
            Hint(l, "RimForex_Set_TrendPeriodTip".Translate(
                (Settings.trendPeriodDays / 2f).ToString("0.#")));

            // ── 平仓心情强度 ──
            l.Gap(10f);
            l.Label("RimForex_Set_MoodHeader".Translate());

            l.Label("RimForex_Set_LossMoodScale".Translate(Settings.lossMoodScale.ToString("0.00")));
            Settings.lossMoodScale = l.Slider(Settings.lossMoodScale,
                RimForexSettings.MinMoodScale, RimForexSettings.MaxMoodScale);
            l.Label("RimForex_Set_LossMoodScaleHint".Translate());

            l.Label("RimForex_Set_GainMoodScale".Translate(Settings.gainMoodScale.ToString("0.00")));
            Settings.gainMoodScale = l.Slider(Settings.gainMoodScale,
                RimForexSettings.MinMoodScale, RimForexSettings.MaxMoodScale);
            l.Label("RimForex_Set_GainMoodScaleHint".Translate());

            l.Label("RimForex_Set_MarginCallMoodScale".Translate(Settings.marginCallMoodScale.ToString("0.00")));
            Settings.marginCallMoodScale = l.Slider(Settings.marginCallMoodScale,
                RimForexSettings.MinMoodScale, RimForexSettings.MaxMoodScale);
            l.Label("RimForex_Set_MarginCallMoodScaleHint".Translate());

            Settings.ClampAll();

            // ---- 货币名字 ----
            l.Gap(12f);
            Text.Font = GameFont.Medium;
            l.Label("RimForex_SettingsNameTest".Translate());
            Text.Font = GameFont.Small;
            l.GapLine();
            l.Label("RimForex_Set_NameDesc".Translate());
            l.Gap(4f);

            // 重掷：换全局种子，并把新名字写回所有已存在的货币。
            // 这是关键 —— 光换种子只影响"之后新生成的"派系，
            // 已经在终端里的货币不会变，玩家会以为按钮没生效。
            if (l.ButtonText("RimForex_Set_RerollNames".Translate()))
            {
                Settings.nameSeed = Rand.Int;
                int changed = FxCurrencyGenerator.ApplyToExisting();
                NameSamples.Clear();
                FxCurrencyGenerator.SampleNames(NameSamples, 12);
                Messages.Message("RimForex_MsgNamesRerolled".Translate(changed),
                    MessageTypeDefOf.TaskCompletion, false);
            }
            TooltipHandler.TipRegion(l.GetRect(24f), "RimForex_Set_RerollNamesTip".Translate());

            l.Gap(4f);
            if (l.ButtonText("RimForex_Set_GenNames".Translate()))
            {
                NameSamples.Clear();
                FxCurrencyGenerator.SampleNames(NameSamples, 12);
            }

            if (NameSamples.Count > 0)
            {
                Text.Font = GameFont.Tiny;
                for (int i = 0; i < NameSamples.Count; i++)
                {
                    l.Label("  " + NameSamples[i]);
                }
                Text.Font = GameFont.Small;
            }
            else
            {
                Hint(l, "RimForex_Set_GenNamesHint".Translate());
            }

            // ---- 图表维护 ----
            Text.Font = GameFont.Medium;
            l.Label("RimForex_SettingsChartTools".Translate());
            Text.Font = GameFont.Small;
            l.GapLine();
            l.Label("RimForex_Set_ClearDesc".Translate());
            l.Gap(4f);

            // 清空 K 线：唯一放在设置里而不是终端里的功能按钮。
            //
            // 为什么不在终端：
            //   1. 图表区第一行已经排满（倒计时 + 时K + 日K + 重置视图），
            //      再放按钮会把标题挤到约 80px，长货币名必被截断；
            //   2. 试过放第二行，结果**被 K 线图的背景盖住**，肉眼找不到。
            // 而它本来就是"调参后才用一次"的工具，放设置里反而更顺手。
            if (l.ButtonText("RimForex_ClearCandles".Translate()))
            {
                int cleared = ClearCandlesNow();
                Messages.Message("RimForex_MsgCandlesCleared".Translate(cleared),
                    MessageTypeDefOf.TaskCompletion, false);
            }
            Hint(l, "RimForex_ClearCandlesTip".Translate());

            l.Gap(12f);
            // ---- 界面提示开关 ----
            Text.Font = GameFont.Medium;
            l.Label("RimForex_SettingsHints".Translate());
            Text.Font = GameFont.Small;
            l.GapLine();
            l.Label("RimForex_SettingsHintsDesc".Translate());
            l.Gap(6f);

            l.CheckboxLabeled("RimForex_Set_QuotePacing".Translate(), ref Settings.showQuotePacingHint,
                "RimForex_Set_QuotePacingTip".Translate());
            l.CheckboxLabeled("RimForex_Set_ChartLegend".Translate(), ref Settings.showChartEntryLegend,
                "RimForex_Set_ChartLegendTip".Translate());
            l.CheckboxLabeled("RimForex_Set_BreakevenLine".Translate(), ref Settings.showBreakevenLine,
                "RimForex_Set_BreakevenLineTip".Translate());
            l.CheckboxLabeled("RimForex_Set_StopOutLine".Translate(), ref Settings.showStopOutLine,
                "RimForex_Set_StopOutLineTip".Translate());
            l.CheckboxLabeled("RimForex_Set_SettleCountdown".Translate(), ref Settings.showSettlementCountdown,
                "RimForex_Set_SettleCountdownTip".Translate());
            l.CheckboxLabeled("RimForex_Set_TimeDebug".Translate(), ref Settings.showTimeDebug,
                "RimForex_Set_TimeDebugTip".Translate());
            l.CheckboxLabeled("RimForex_Set_CostBreakdown".Translate(), ref Settings.showCostBreakdown,
                "RimForex_Set_CostBreakdownTip".Translate());
            l.CheckboxLabeled("RimForex_Set_TimeSpeed".Translate(), ref Settings.showTimeSpeedHint,
                "RimForex_Set_TimeSpeedTip".Translate());

            // 悬浮盈亏窗（2026-10-20）：关掉终端时也能看到浮动盈亏
            l.CheckboxLabeled("RimForex_Set_FloatingPnl".Translate(), ref Settings.showFloatingPnl,
                "RimForex_Set_FloatingPnlTip".Translate());

            l.Gap(10f);
            if (l.ButtonText("RimForex_Set_Reset".Translate()))
            {
                Settings.ResetToDefaults();
            }

            // 注意：这里【不】调用 l.End() / EndScrollView()。
            // 它们由调用方 DrawSettingsWindowContents 统一负责 ——
            // 因为那边要在 l.End() 之后读 CurHeight 来实测内容高度。
        }

        /// <summary>滑块下方的灰色小字说明。</summary>
        private static void Hint(Listing_Standard l, string text)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            l.Label(text);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        /// <summary>
        /// 清空所有货币的 K 线，返回清掉的缓冲条数（0 表示当前不在游戏里）。
        ///
        /// 从模组设置调用，所以要自己找 <see cref="FxGameComponent"/> ——
        /// 设置窗口不属于任何 Map/World 上下文。
        /// </summary>
        private static int ClearCandlesNow()
        {
            if (Find.World == null) return 0;
            FxGameComponent fx = Find.World.GetComponent<FxGameComponent>();
            if (fx == null || fx.market == null) return 0;

            int n = 0;
            foreach (FxCurrency c in fx.market.AllCurrencies)
            {
                if (c != null) n++;
            }
            fx.market.ClearAllCandles();
            return n;
        }

        /// <summary>
        /// 趋势级贡献的价格摆动幅度（百分比），给玩家一个直观量级。
        ///
        /// 正弦锚时代的公式是 <c>exp(amp)−1</c>，换成多尺度 OU 之后不再适用 ——
        /// 现在趋势来自最慢那一级 OU，它的稳态标准差是
        ///     σ_trend = σ_slow / √(1 − a²),   a = exp(−1/τ)
        /// 单边摆动取 2σ（约 95% 区间）。
        ///
        /// 这样滑块拉大时提示数字会真的跟着涨，玩家能看出它在起作用。
        /// </summary>
        private static float TrendSwingPct()
        {
            if (Settings.trendAmp <= 0.0001f) return 0f;
            try
            {
                int steps = FxMarket.StepsPerDay;
                double sigmaStep = FxMarket.DailyVol / System.Math.Sqrt(steps);
                double[] tau;
                double[] sig = FxMarket.AllocateOscBudget(sigmaStep, steps, out tau);
                int k = sig.Length - 1;
                double a = System.Math.Exp(-1.0 / tau[k]);
                double statSd = sig[k] / System.Math.Sqrt(1.0 - a * a);
                return (float)(System.Math.Exp(2.0 * statSd) - 1.0) * 100f;
            }
            catch
            {
                return 0f;   // 设置窗口不该因为算个提示数字而报错
            }
        }
    }
}
