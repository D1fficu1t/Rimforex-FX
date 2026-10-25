using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// FX 交易终端界面。全部使用原版 Widgets 控件与配色。
    ///
    /// 布局：账户区 / 左列行情表 / 右列K线 / 下单区 / 底部时间控制条，每块严格裁剪到自己的 Rect。
    /// 时间控制用原版 TimeControls.DoTimeControlsGUI 内嵌，因此在本窗口内可直接改游戏速度。
    /// </summary>
    public class Window_FxTerminal : Window
    {
        private readonly Building_FXTradingTerminal terminal;
        private readonly FxAccount account;

        private Vector2 currencyScroll;
        private Vector2 positionScroll;

        // 下单表单状态 —— 输入的是【保证金金额】，不是数量
        private int selectedFactionId = -1;
        /// <summary>玩家希望投入的保证金金额（白银）。0 = 尚未初始化，首次绘制时夹到合法下限。</summary>
        private int orderMargin;
        /// <summary>
        /// 保证金是否还停在「最小值」上（2026-10-23 加）。
        ///
        /// 解决的问题：在 1x 点「最小值」得到 80，再拖到 100x，输入框仍停在 80 ——
        /// 而 100x 下开 1 个单位只要 3。玩家按的是「最小值」，却开出了 100 倍规模。
        ///
        /// 现在记住这个意图：只要没手动改过金额，改杠杆时**双向重算**最小值；
        /// 一旦玩家自己输入或点了加减档 / 梭哈，标记清除，恢复「只托底不干涉」。
        /// </summary>
        private bool marginAtMinimum;
        private float orderLeverage = 1f;
        private string marginBuffer = "";

        /// <summary>
        /// 杠杆档位。滑块在这些值之间跳，不是连续取值。
        ///
        /// 为什么不用 1~100 连续：线性滑轨上 1~10 只占 9%，而玩家最常用的就是低倍区，
        /// 连续滑到 3x 或 5x 基本靠运气。改成离散档位后每一档都点得准，
        /// 高倍区（50/75/100）也只需要往右拖到底。
        ///
        /// 与 <see cref="FxGameComponent.MaxSafeLeverageFor"/> 配合：
        /// 滑块上限会被夹到该品种撑得住的最大档，所以不会滑出"解锁即强平"的档位。
        /// </summary>
        private static readonly float[] LeverageLadder =
            { 1f, 2f, 3f, 5f, 8f, 10f, 15f, 20f, 30f, 50f, 75f, 100f };

        /// <summary>把任意杠杆值映射到不超过它的最高档位索引（找不到就返回 0）。</summary>
        private static int LadderIndexOf(float lev)
        {
            int best = 0;
            for (int i = 0; i < LeverageLadder.Length; i++)
                if (LeverageLadder[i] <= lev + 0.001f) best = i;
            return best;
        }

        /// <summary>该安全杠杆上限允许到达的最大档位索引。</summary>
        private static int MaxLadderIndex(float cap)
        {
            int best = 0;
            for (int i = 0; i < LeverageLadder.Length; i++)
                if (LeverageLadder[i] <= cap + 0.001f) best = i;
            return best;
        }

        // 存取款输入（直接填数字，不再靠"禁用"）
        private int depositAmount = 0;
        private string depositBuffer = "0";
        private bool depositBufferDirty = true;
        private int withdrawAmount = 0;
        private string withdrawBuffer = "0";
        private bool withdrawBufferDirty = true;

        // K 线视图状态：滚轮缩放
        /// <summary>当前图表周期（分/时/日）。三级各自有独立缓冲，见 FxChartSpan。</summary>
        private FxChartSpan chartSpan = FxChartSpan.Minute;
        /// <summary>可见 K 线根数。越小越放大。</summary>
        private int chartCandles = 60;

        private const int ChartZoomMin = 10;
        /// <summary>放大上限。与 FxMarket.MaxCandles 同宽 —— 再大也没有数据可看。</summary>
        private const int ChartZoomMax = 100;
        private const int ChartZoomStep = 6;

        // 布局常量
        // 持仓面板右侧列的宽度。列头与数据行【必须】共用这几个值 ——
        // 曾经各写一套字面量，导致列头比数据格宽，而且最右两列被「交易历史」按钮压住。
        private const float ColPnlW = 104f;    // 容纳 "+12345 盈亏"
        private const float ColCostW = 66f;    // 容纳 "1234 成本"
        private const float ColActW = 58f;     // 容纳 "平仓" 或 "2.9s"
        private const float ColGap = 8f;

        private const float Pad = 8f;
        private const float Gap = 8f;
        private const float AccountH = 154f;
        private const float StatRowH = 46f;
        private const float BtnH = 28f;
        private const float MarketRowH = 28f;
        private const float OrderH = 296f;
        private const float TimeBarH = 32f;
        private const float TitleH = 34f;
        /// <summary>底部留白，避免与主按钮栏 / 原版时间控制重叠。</summary>
        private const float BottomOffset = 78f;

        public override Vector2 InitialSize =>
            new Vector2(980f, Mathf.Min(838f, UI.screenHeight - BottomOffset - 16f));

        public Window_FxTerminal(Building_FXTradingTerminal terminal, FxAccount account)
        {
            this.terminal = terminal;
            this.account = account;
            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = true;
            onlyOneOfTypeAllowed = true;
        }

        /// <summary>
        /// 基类 PreOpen() 会调用本方法，因此不能在构造函数里改 windowRect（会被覆盖）。
        /// 这里把窗口整体上移，避免压住主按钮栏和原版时间控制。
        /// </summary>
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect.y = Mathf.Max(8f, windowRect.y - BottomOffset * 0.5f);
        }

        private FxGameComponent Fx => Find.World.GetComponent<FxGameComponent>();

        public override void DoWindowContents(Rect inRect)
        {
            // ── 终端失效就自动关窗（2026-10-21 修 R11）──
            // 原来只在 gizmo 上检查通电状态，窗口一旦打开就再也不校验，
            // 于是断电或拆掉终端之后，窗口还能照常下单 —— 250W 的门槛形同虚设。
            if (terminal == null || !terminal.Usable)
            {
                Close();
                return;
            }

            FxGameComponent fx = Fx;
            if (fx == null || account == null)
            {
                Widgets.Label(inRect, "RimForex_NoMarket".Translate());
                return;
            }

            List<FxCurrency> currencies = fx.market.AllSorted();
            if (selectedFactionId < 0 && currencies.Count > 0)
            {
                selectedFactionId = currencies[0].factionLoadID;
            }

            Rect content = new Rect(inRect.x, inRect.y + TitleH, inRect.width, inRect.height - TitleH);

            // 账户区（顶部，固定高）
            Rect accountRect = new Rect(content.x, content.y, content.width, AccountH);
            DrawAccountZone(accountRect, fx);

            // 时间控制条（底部，固定高）
            Rect timeRect = new Rect(content.x, content.yMax - TimeBarH, content.width, TimeBarH);
            DrawTimeBar(timeRect);

            // 下单区（时间条之上，固定高）
            float orderY = timeRect.y - Gap - OrderH;
            Rect orderRect = new Rect(content.x, orderY, content.width, OrderH);
            DrawOrderZone(orderRect, fx);

            // 中间区：左表格 + 右K线
            float midY = accountRect.yMax + Gap;
            float midH = orderY - Gap - midY;
            if (midH < 120f) midH = 120f;

            float leftW = Mathf.Clamp(content.width * 0.54f, 380f, content.width - 260f);
            Rect leftRect = new Rect(content.x, midY, leftW, midH);
            Rect rightRect = new Rect(content.x + leftW + Gap, midY, content.width - leftW - Gap, midH);

            DrawMarketZone(leftRect, currencies);
            DrawChartZone(rightRect, fx);
        }

        // ============================ 时间控制 ============================
        private void DrawTimeBar(Rect zone)
        {
            // 原版 DoTimeControlsGUI 用 BeginGroup 相对传入 rect 绘制，可安全内嵌。
            // 它自带快捷键 tooltip，会自动读取玩家的按键绑定。
            Rect buttons = new Rect(zone.x, zone.y + (zone.height - TimeControls.TimeButSize.y) * 0.5f,
                                    TimeControls.TimeButSize.x * 4f, TimeControls.TimeButSize.y);
            TimeControls.DoTimeControlsGUI(buttons);

            // 右下角：新手教程 + 模组设置（并排）。
            // 放在时间条上而不是账户区 —— 那里已经被标题、状态行、结算倒计时占满。
            const float SettingsBtnW = 96f;
            Rect settingsBtn = new Rect(zone.xMax - SettingsBtnW, zone.y + (zone.height - 24f) * 0.5f,
                                        SettingsBtnW, 24f);

            Rect guideBtn = new Rect(settingsBtn.x - SettingsBtnW - Gap, settingsBtn.y,
                                     SettingsBtnW, 24f);
            if (Widgets.ButtonText(guideBtn, "RimForex_GuideButton".Translate()))
            {
                if (!Find.WindowStack.IsOpen(typeof(Window_FxGuide)))
                {
                    Find.WindowStack.Add(new Window_FxGuide());
                }
            }
            TooltipHandler.TipRegion(guideBtn, "RimForex_GuideButtonTip".Translate());
            if (Widgets.ButtonText(settingsBtn, "RimForex_OpenSettings".Translate()))
            {
                if (RimForexMod.Instance != null)
                {
                    Find.WindowStack.Add(new Dialog_ModSettings(RimForexMod.Instance));
                }
            }
            TooltipHandler.TipRegion(settingsBtn, "RimForex_OpenSettingsTip".Translate());

            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            if (RimForexMod.Settings == null || RimForexMod.Settings.showTimeSpeedHint)
            {
                Widgets.Label(new Rect(buttons.xMax + Gap, zone.y + 8f,
                        zone.width - buttons.width - Gap - SettingsBtnW * 2f - Gap * 2f, 18f),
                    "RimForex_TimeHint".Translate());
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        // ============================ 账户区 ============================

        private void DrawAccountZone(Rect zone, FxGameComponent fxComponent)
        {
            Widgets.DrawMenuSection(zone);
            Rect r = zone.ContractedBy(Pad);

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, 200f, 26f), "RimForex_Title".Translate());
            Text.Font = GameFont.Small;

            // 累计手续费已从这一行移除（2026-10-23，作者认为这个统计没有意义）。
            // 交易历史里的手续费明细保留不动。
            // 隔夜利息费率原来放在设置页，作者要求挪到终端这一行后面
            string status = "RimForex_StatusLine".Translate(
                account.lastDayPnl.ToString("F0"),
                account.lastDayPositions)
                + "　·　" + "RimForex_CarryRateInline".Translate(
                    (FxGameComponent.DefaultCarryRate * 100f).ToString("0.##"));
            Widgets.Label(new Rect(r.x + 210f, r.y + 4f, r.width - 400f, 22f), status);

            // 距下次每日结算的倒计时 —— 便于验证"每天 0 时结算一次"。
            // 数值直接来自 GenDate（原版 API），所以与游戏内时钟是恒等关系。
            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg == null || cfg.showSettlementCountdown)
            {
                int untilHours = fxComponent != null ? fxComponent.HoursUntilSettlement : 0;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(r.xMax - 190f, r.y + 6f, 190f, 18f),
                    "RimForex_SettleCountdown".Translate(untilHours.ToString("0")));
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;

                // 诊断行（模组设置里可关）：把原始 tick 与游戏时钟并排显示。
                // 结算边界 = TicksAbs 的整日边界，所以"距下次结算 tick"
                // 应当恒等于 (24 - 当前小时) × 150。
                if (cfg == null || cfg.showTimeDebug)
                {
                    int abs = GenTicks.TicksAbs;
                    int hour = GenDate.HourOfDay(abs, 0f);
                    int remaining = fxComponent != null ? fxComponent.TicksUntilSettlementExact : 0;
                    GUI.color = new Color(0.62f, 0.68f, 0.75f);
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.UpperRight;
                    Widgets.Label(new Rect(r.xMax - 460f, r.y + 22f, 460f, 16f),
                        "RimForex_TimeDebug".Translate(
                            abs.ToString(),
                            hour.ToString(),
                            remaining.ToString(),
                            (remaining / (float)FxGameComponent.TicksPerHour).ToString("0.0")));
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                }
            }

            // 净值行
            float rowY = r.y + 30f;
            float w = (r.width - 3f * Gap) / 4f;
            DrawStat(new Rect(r.x + (w + Gap) * 0f, rowY, w, StatRowH), "RimForex_Equity", account.Equity, "RimForex_TipEquity");
            DrawStat(new Rect(r.x + (w + Gap) * 1f, rowY, w, StatRowH), "RimForex_FreeMargin", account.balance, null);
            DrawStat(new Rect(r.x + (w + Gap) * 2f, rowY, w, StatRowH), "RimForex_UsedMargin", account.MarginUsed, null);
            DrawStat(new Rect(r.x + (w + Gap) * 3f, rowY, w, StatRowH), "RimForex_FloatingPnl", account.FloatingPnl, "RimForex_TipFloat", true);

            // ---- 穿仓警示：负余额必须靠补银填平 ----
            // 行情跳空让浮亏超过保证金时就会出现。此时不能开新仓，也不能提款。
            bool inDebt = account.balance < 0f;
            if (inDebt)
            {
                float warnY = rowY + StatRowH + 2f;
                Rect warn = new Rect(r.x, warnY, r.width, 20f);
                GUI.color = new Color(1f, 0.45f, 0.40f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(warn, "RimForex_DebtWarning".Translate(
                    (-account.balance).ToString("F0")));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }

            // 冻结倒计时（E7）：告诉玩家还有多少天不能开仓/提款
            int frozenDays = account.frozenUntilTick > GenTicks.TicksGame
                ? (account.frozenUntilTick - GenTicks.TicksGame) / FxGameComponent.TicksPerDay + 1
                : 0;
            if (frozenDays > 0)
            {
                float fzY = rowY + StatRowH + (inDebt ? 24f : 2f);
                GUI.color = new Color(0.55f, 0.72f, 0.95f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(r.x, fzY, r.width, 20f),
                    "RimForex_FrozenWarning".Translate(frozenDays.ToString()));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }

            // 存取款行：直接填数字
            float y = rowY + StatRowH + (inDebt ? 24f : 0f)
                    + (frozenDays > 0 ? 22f : 0f) + Gap;
            int availableSilver = CountMapSilver();

            if (depositBufferDirty)
            {
                depositAmount = availableSilver;
                depositBuffer = depositAmount.ToString();
                depositBufferDirty = false;
            }

            Widgets.Label(new Rect(r.x, y + 4f, 74f, 22f), "RimForex_Deposit".Translate());
            Rect depField = new Rect(r.x + 78f, y, 88f, BtnH);
            Widgets.TextFieldNumeric(depField, ref depositAmount, ref depositBuffer, 0f, 100000000f);
            Rect depAll = new Rect(depField.xMax + 4f, y, 42f, BtnH);
            if (Widgets.ButtonText(depAll, "RimForex_All".Translate()))
            {
                depositAmount = availableSilver;
                depositBuffer = depositAmount.ToString();
            }
            Rect depBtn = new Rect(depAll.xMax + 4f, y, 74f, BtnH);
            if (Widgets.ButtonText(depBtn, "RimForex_DoDeposit".Translate()))
            {
                DoDeposit(depositAmount);
                depositBufferDirty = true;
            }
            TooltipHandler.TipRegion(new Rect(r.x, y, depBtn.xMax - r.x, BtnH),
                "RimForex_TipDeposit".Translate(availableSilver));

            // 提款组（右半）
            float half = r.x + r.width * 0.52f;
            Widgets.Label(new Rect(half, y + 4f, 74f, 22f), "RimForex_Withdraw".Translate());
            if (withdrawBufferDirty)
            {
                withdrawAmount = Mathf.Max(0, Mathf.FloorToInt(account.balance));
                withdrawBuffer = withdrawAmount.ToString();
                withdrawBufferDirty = false;
            }
            Rect witField = new Rect(half + 78f, y, 88f, BtnH);
            Widgets.TextFieldNumeric(witField, ref withdrawAmount, ref withdrawBuffer, 0f, 100000000f);
            Rect witAll = new Rect(witField.xMax + 4f, y, 42f, BtnH);
            if (Widgets.ButtonText(witAll, "RimForex_All".Translate()))
            {
                withdrawAmount = Mathf.Max(0, Mathf.FloorToInt(account.balance));
                withdrawBuffer = withdrawAmount.ToString();
            }
            Rect witBtn = new Rect(witAll.xMax + 4f, y, 74f, BtnH);
            if (Widgets.ButtonText(witBtn, "RimForex_DoWithdraw".Translate()))
            {
                DoWithdraw(withdrawAmount);
                withdrawBufferDirty = true;
            }
            TooltipHandler.TipRegion(new Rect(half, y, witBtn.xMax - half, BtnH),
                "RimForex_TipWithdraw".Translate(account.balance.ToString("F0")));
        }

        private static void DrawStat(Rect rect, string labelKey, float value, string tipKey, bool colorize = false)
        {
            string label = labelKey.Translate();
            if (tipKey != null) label += " " + "RimForex_TipMark".Translate();
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 20f), new GUIContent(label, tipKey != null ? tipKey.Translate() : null));

            string text = value.ToString("F0");
            if (colorize) text = ColorizeBySign(text, value);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y + 20f, rect.width, 24f), text);
            Text.Font = GameFont.Small;
        }

        private static string ColorizeBySign(string text, float value)
            => FxUiUtil.ColorizeBySign(text, value);

        /// <summary>
        /// 盈亏的 IMGUI 着色。色板与 <see cref="FxUiUtil.ColorizeBySign"/> 保持一致，
        /// 这样顶部「浮动盈亏」与持仓行看起来是同一种绿/红。
        ///
        /// 阈值 ±1：0 附近不着色（刚开仓时盈亏几乎是 0，涂成绿色会误导）。
        ///
        /// public：悬浮盈亏窗（<see cref="FxHud"/>）也用它，
        /// 这样默认色和终端里的色永远一致，不会各调各的。
        /// </summary>
        public static Color PnlGuiColor(float value)
        {
            if (value > 1f) return new Color(0.45f, 0.85f, 0.50f);
            if (value < -1f) return new Color(0.95f, 0.45f, 0.40f);
            return Color.white;
        }

        /// <summary>
        /// 按字符数截断并加省略号。原版设计哲学里没有文字省略号，
        /// 但窄表格里不截断就会溢出到相邻列，反而更难读。
        /// </summary>
        private static string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "…";
        }

        // ============================ 行情表 ============================

        private void DrawMarketZone(Rect zone, List<FxCurrency> currencies)
        {
            Widgets.DrawMenuSection(zone);
            Rect r = zone.ContractedBy(Pad);

            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(r.x + 2f, r.y, 152f, 20f), "RimForex_ColPair".Translate());
            Widgets.Label(new Rect(r.x + 156f, r.y, 74f, 20f), "RimForex_ColFaction".Translate());
            Widgets.Label(new Rect(r.x + 232f, r.y, 58f, 20f), "RimForex_ColRate".Translate());
            Widgets.Label(new Rect(r.x + 292f, r.y, 52f, 20f), "RimForex_ColChange".Translate());
            Widgets.Label(new Rect(r.x + 346f, r.y, 38f, 20f), "RimForex_ColVol".Translate());
            Widgets.Label(new Rect(r.x + 386f, r.y, 72f, 20f), "RimForex_ColPosition".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect outRect = new Rect(r.x, r.y + 20f, r.width, r.height - 20f);
            if (outRect.height <= 0f) return;

            float viewH = Mathf.Max(outRect.height, currencies.Count * MarketRowH + 4f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 18f, viewH);

            Widgets.BeginScrollView(outRect, ref currencyScroll, viewRect);
            float y = 0f;
            for (int i = 0; i < currencies.Count; i++)
            {
                FxCurrency cur = currencies[i];
                Rect row = new Rect(0f, y, viewRect.width, MarketRowH);

                if (cur.factionLoadID == selectedFactionId) Widgets.DrawHighlightSelected(row);
                else if (i % 2 == 1) Widgets.DrawLightHighlight(row);

                if (Widgets.ButtonInvisible(row)) selectedFactionId = cur.factionLoadID;
                Widgets.DrawHighlightIfMouseover(row);

                float pct = cur.previousRate > 0.01f ? (cur.rate / cur.previousRate - 1f) * 100f : 0f;

                Widgets.Label(new Rect(row.x + 2f, row.y + 5f, 154f, 20f), cur.PairLabel);
                Widgets.Label(new Rect(row.x + 156f, row.y + 5f, 76f, 20f), Truncate(cur.factionName, 5));
                Widgets.Label(new Rect(row.x + 232f, row.y + 5f, 58f, 20f), cur.rate.ToString("F2"));
                Widgets.Label(new Rect(row.x + 292f, row.y + 5f, 52f, 20f), ColorizeBySign(pct.ToString("+0.0;-0.0;0.0"), pct));
                Widgets.Label(new Rect(row.x + 346f, row.y + 5f, 38f, 20f), "x" + cur.volatility.ToString("0.0"));
                Widgets.Label(new Rect(row.x + 386f, row.y + 5f, 72f, 20f), PositionSummaryFor(cur.factionLoadID));

                // 单位数不再显示在格子里（会挤掉盈亏），改放悬停提示。
                float sumUnits, sumPnl;
                PositionTotalsFor(cur.factionLoadID, out sumUnits, out sumPnl);
                if (Mathf.Abs(sumUnits) >= 0.5f)
                {
                    TooltipHandler.TipRegion(
                        new Rect(row.x + 386f, row.y, 72f, MarketRowH),
                        "RimForex_TipPosCell".Translate(
                            sumUnits.ToString("+0;-0;0"), sumPnl.ToString("F0")));
                }

                y += MarketRowH;
            }
            Widgets.EndScrollView();
        }

        /// <summary>
        /// 该货币的持仓汇总：单位数（带方向）与浮动盈亏。
        /// </summary>
        private void PositionTotalsFor(int factionLoadID, out float units, out float pnl)
        {
            units = 0f;
            pnl = 0f;
            for (int i = 0; i < account.positions.Count; i++)
            {
                FxPosition p = account.positions[i];
                if (p.currencyFactionLoadID != factionLoadID) continue;
                units += p.isLong ? p.units : -p.units;
                pnl += p.Pnl;
            }
        }

        /// <summary>
        /// 货币列表里"盈亏"那一格的内容。
        ///
        /// ── 只显示盈亏，不再显示单位数（2026-10-20）──
        /// 原来是 <c>单位 / 盈亏</c>，例如 <c>+258 / -33</c>。
        /// 但单位数能到四位数（实测 -4551），72px 的格子根本放不下，
        /// 于是**把后面的盈亏挤出了格子** —— 玩家只看到 "-4551 /"，
        /// 最该看的那个数字反而消失了。
        ///
        /// 盈亏才是这一列的用途。单位数移进了这一格的悬停提示。
        /// </summary>
        private string PositionSummaryFor(int factionLoadID)
        {
            float units, pnl;
            PositionTotalsFor(factionLoadID, out units, out pnl);
            if (Mathf.Abs(pnl) < 0.5f) return "-";
            return ColorizeBySign(pnl.ToString("+0;-0;0"), pnl);
        }

        // ============================ K 线 ============================

        private void DrawChartZone(Rect zone, FxGameComponent fx)
        {
            Widgets.DrawMenuSection(zone);
            Rect r = zone.ContractedBy(Pad);
            if (r.height < 60f) return;

            FxCurrency sel = fx.market.Get(selectedFactionId);
            if (sel == null)
            {
                Widgets.Label(r, "RimForex_NoSelection".Translate());
                return;
            }

            // ---- 头部：标题行 / 提示行 / 重置按钮，三者互不重叠 ----
            //
            // 早期把标题、倒计时、提示全塞在同一行，长货币名（如"紫色三角洲氏族 Bead/XAG"）
            // 会和右侧的倒计时挤在一起。现在改成显式分行：
            //   第 1 行：标题（左）+ 重置按钮（右），标题宽度扣掉按钮宽度
            //   第 2 行：报价节奏提示（可选，占满整行）
            // 倒计时不再单独占一行 —— 它并进标题行的右侧、按钮左边。
            RimForexSettings cfg = RimForexMod.Settings;

            // 右侧控件从左到右：倒计时 | 时K | 日K | 重置视图
            const float SpanW = 44f;
            const float SpanGap = 4f;
            float resetW = 96f;
            float spanBlock = (SpanW + SpanGap) * 2f;   // 两个周期按钮 + 间距
            Rect resetBtn = new Rect(r.xMax - resetW, r.y, resetW, 22f);
            Rect dayBtn = new Rect(resetBtn.x - spanBlock, resetBtn.y, SpanW, resetBtn.height);
            Rect hourBtn = new Rect(dayBtn.xMax + SpanGap, resetBtn.y, SpanW, resetBtn.height);

            // 倒计时（与推进逻辑同源：基于 lastStepTick；1x 下 60 tick = 1 现实秒）
            //
            // 2026-10-21：只显示**现实秒**。
            // 原来第一个占位符是 "ticksLeft / TicksPerStep"，却被写成"游戏时" ——
            // 那是"还剩几成一个报价步"的比例（0~1），不是游戏小时。
            // 一个报价步只有 0.07 游戏时，用游戏时表达毫无意义，玩家关心的就是"还有几秒"。
            int ticksLeft = fx.TicksUntilQuote;
            string countdown = cfg == null || cfg.showQuotePacingHint
                ? "RimForex_NextQuote".Translate(
                    (ticksLeft / (float)FxGameComponent.TicksPerRealSecond).ToString("0.0"))
                : null;

            // 标题只占用"倒计时左侧"的空间，避免与它重叠
            float countdownW = countdown != null ? 150f : 0f;
            float titleW = r.width - resetW - spanBlock - Gap * 3f - countdownW;

            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(r.x, r.y, Mathf.Max(60f, titleW), 22f),
                "RimForex_ChartTitle".Translate(sel.PairLabel, FxMarket.QuotesPerCandle));

            if (countdown != null)
            {
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperRight;
                // 2026-10-21：锚点从 hourBtn 移到 dayBtn 的左边。
                // 原来锚在时K按钮左侧，而日K按钮就在时K左边 ——
                // 倒计时矩形 [hourBtn.x-150-Gap, hourBtn.x-Gap] 与日K按钮
                // [dayBtn.x, dayBtn.xMax] 重叠 44px，且日K按钮后绘制，
                // 会把倒计时**右边的字符直接盖掉**。
                Widgets.Label(new Rect(dayBtn.x - countdownW - Gap, r.y + 3f, countdownW, 18f), countdown);
                Text.Anchor = TextAnchor.UpperLeft;
            }

            if (Widgets.ButtonText(resetBtn, "RimForex_ChartReset".Translate()))
            {
                chartCandles = 60;
            }
            TooltipHandler.TipRegion(resetBtn, "RimForex_ChartNavTip".Translate());

            // ── 周期切换按钮（时K / 日K），在"重置视图"左边 ──
            // 三级各有独立缓冲，切换后看到的是各自的历史长度，不是同一批数据换标签。
            DrawSpanButton(hourBtn, FxChartSpan.Hour, "RimForex_SpanHour");
            DrawSpanButton(dayBtn, FxChartSpan.Day, "RimForex_SpanDay");

            // 第 2 行：节奏说明（可关闭）。鼠标操作说明一律放 tooltip，不再占版面。
            float hintH = 0f;
            if (cfg == null || cfg.showQuotePacingHint)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(r.x, r.y + 22f, r.width, 16f),
                    "RimForex_ChartSpeedHint".Translate(
                        FxMarket.QuotesPerCandle,
                        // 2026-10-21：这里原来漏乘 QuotesPerCandle，把"一步"当成"一根K线"，
                        // 于是显示 0.1 游戏时，而实际是 0.43。
                        (FxMarket.TicksPerCandle / (float)FxGameComponent.TicksPerDay * 24f).ToString("0.##"),
                        (FxMarket.TicksPerCandle / (float)FxGameComponent.TicksPerRealSecond).ToString("0"),
                        FxMarket.SubSteps)
                    + "   " + "RimForex_ChartSpanLabel".Translate(
                        chartSpan == FxChartSpan.Day ? "RimForex_SpanDay".Translate()
                            : chartSpan == FxChartSpan.Hour ? "RimForex_SpanHour".Translate()
                            : "RimForex_SpanMinuteShort".Translate(),
                        SpanPeriodText()));
                GUI.color = Color.white;
                hintH = 18f;
            }
            Text.Font = GameFont.Small;

            float plotY = r.y + 22f + hintH;
            Rect plot = new Rect(r.x, plotY, r.width, r.yMax - plotY);
            if (plot.height < 60f) return;

            HandleChartInput(plot);

            // 贴着最新一根显示最近 chartCandles 根。
            // 平移已移除（见 HandleChartInput 的说明），所以不再需要"多取一些供拖动"。
            List<FxCandle> view = fx.market.GetCandles(selectedFactionId, chartCandles, chartSpan);

            DrawCandles(plot, view);
            // 持仓开仓价线 + 盈亏平衡线：必须在 DrawCandles 之后画
            //（它会把价格区间记下来），否则不知道 K 线的 y 轴范围，线会画错位置。
            if (ChartHasData)
            {
                DrawPositionLines(ChartArea, ChartMin, ChartMax);
                DrawBreakevenLine(ChartArea, ChartMin, ChartMax);
                DrawStopOutLine(ChartArea, ChartMin, ChartMax);
            }
            DrawChartOverlay(plot, view);

            // 悬停提示放最后：此时 ChartArea / LastSlotWidth 都已由 DrawCandles 算好
            if (ChartHasData) DrawCandleHover(plot, ChartArea, view);
        }

        /// <summary>
        /// 图表输入：目前只有滚轮缩放。
        ///
        /// ── 中键拖动已移除（2026-10-16）──
        /// 它一直没能真正工作：IMGUI 对中键的 MouseDown/MouseDrag/MouseUp 派发不可靠
        /// （中键按下后收不到 MouseDrag），换成直接轮询键盘模块的按键状态也一样不成 ——
        /// 拖动期间窗口本身收不到事件。
        /// 与其留着一个"看着有、用起来没有"的功能，不如先删干净：
        /// 死代码会让人误以为平移可用，也会干扰后续重构。
        /// 之后重做拖动时，倾向改成"左键在绘图区内拖动"（IMGUI 对左键的派发是可靠的），
        /// 但那是另一件事，这次只做减法。
        ///
        /// 滚轮缩放保留 —— 它走 IMGUI 事件、只读 <c>delta</c>，一直是好的。
        /// </summary>
        private void HandleChartInput(Rect plot)
        {
            Event e = Event.current;
            if (e == null) return;

            bool over = plot.Contains(e.mousePosition);

            if (over && e.type == EventType.ScrollWheel)
            {
                // 向上滚（delta > 0）= 放大 = 减少可见根数
                int dir = e.delta.y > 0f ? 1 : -1;
                chartCandles = Mathf.Clamp(chartCandles + dir * ChartZoomStep, ChartZoomMin, ChartZoomMax);
                e.Use();
            }
        }

        /// <summary>图表左下角的当前视图信息。</summary>
        private void DrawChartOverlay(Rect plot, List<FxCandle> view)
        {
            if (view == null || view.Count == 0) return;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.75f, 0.78f, 0.82f);
            Widgets.Label(new Rect(plot.x + 6f, plot.yMax - 34f, plot.width - 60f, 16f),
                "RimForex_ChartView".Translate(view.Count, SpanPeriodText()));

            // 有持仓线时补一行图例，说明"线为什么不在最新价上"。
            // 根因：K 线画的是中间价，而开仓线画的是方向成交价（做空=bid，低于 mid），
            // 偏移恰好是半个点差。不说明的话玩家会以为图表算错了。
            //
            // 可以在模组设置里关掉 —— 熟悉之后这行只是占地方。
            bool showLegend = RimForexMod.Settings == null || RimForexMod.Settings.showChartEntryLegend;
            if (showLegend && account != null && HasPositionLineFor(selectedFactionId))
            {
                GUI.color = new Color(0.68f, 0.72f, 0.78f);
                Widgets.Label(new Rect(plot.x + 6f, plot.yMax - 50f, plot.width - 12f, 16f),
                    "RimForex_ChartEntryLegend".Translate());
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        /// <summary>当前选中货币是否有会显示在图上的开仓线（用于决定要不要画图例）。</summary>
        private bool HasPositionLineFor(int factionLoadID)
        {
            if (account == null) return false;
            for (int i = 0; i < account.positions.Count; i++)
            {
                FxPosition p = account.positions[i];
                if (p != null && p.currencyFactionLoadID == factionLoadID) return true;
            }
            return false;
        }

        /// <summary>
        /// 由"价格轴每格的跨度"决定要显示几位小数，保证相邻刻度标签不会重复。
        ///
        /// 目标：让 1 格跨度里至少有 2 位有效数字。
        ///     跨度 0.17  → 需要 2 位小数
        ///     跨度 2.5   → 需要 1 位小数
        ///     跨度 20    → 需要 0 位小数
        /// 上限 4 位，避免极端小跨度时标签长得放不下（列宽只有 46px）。
        /// </summary>
        private static int PriceDecimals(float step)
        {
            if (step <= 0f || float.IsNaN(step)) return 2;
            // 跨度 >= 10 时 0 位就够；每小 10 倍多要 1 位
            int d = 0;
            float s = step;
            while (s < 10f && d < 4) { s *= 10f; d++; }
            return d;
        }

        /// <summary>
        /// 画一个周期切换按钮。
        ///
        /// 分K 是默认态，它没有自己的按钮 —— 只在时K/日K 按钮上体现"当前不在此周期"。
        /// 再点一次当前已选中的周期按钮 = 回到分K，这样不用为"分K"多占一个按钮位。
        /// </summary>
        private void DrawSpanButton(Rect btn, FxChartSpan span, string labelKey)
        {
            bool active = chartSpan == span;
            if (active) GUI.color = new Color(0.62f, 0.78f, 0.95f);
            else GUI.color = new Color(0.80f, 0.80f, 0.80f);

            if (Widgets.ButtonText(btn, labelKey.Translate()))
            {
                // 再点一次取消 → 回到分K
                chartSpan = active ? FxChartSpan.Minute : span;
                chartCandles = 60;     // 换周期后根数重置（各周期的合理视窗不同）
            }
            GUI.color = Color.white;

            TooltipHandler.TipRegion(btn, "RimForex_SpanBtnTip".Translate(
                labelKey.Translate(), active ? "RimForex_SpanClickToMinute".Translate() : ""));
        }

        /// <summary>当前周期每根 K 线覆盖的游戏时间，用于绘图区提示。</summary>
        private string SpanPeriodText()
        {
            if (chartSpan == FxChartSpan.Day) return "RimForex_SpanUnitDay".Translate();
            if (chartSpan == FxChartSpan.Hour) return "RimForex_SpanUnitHour".Translate();

            // 分K：长度由「每根K线由几次报价合成」决定，换算成游戏分钟
            float mins = FxMarket.PeriodTicksOf(FxChartSpan.Minute) / (FxGameComponent.TicksPerHour / 60f);
            return "RimForex_SpanUnitMinute".Translate(mins.ToString("0.#"));
        }

        /// <summary>
        /// 鼠标悬停在某根 K 线上时，显示该根的完整数据。
        ///
        /// 用 TooltipHandler 而不是自绘浮层：位置、边界避让都交给原版，
        /// 而且不必保存"上一帧悬停在哪根"这种跨帧状态。
        ///
        /// 判定口径必须与 <see cref="DrawCandles"/> 完全一致，否则会指到相邻一根 ——
        /// 所以槽宽走 <see cref="LastSlotWidth"/>，由绘制时一并算出来。
        ///
        /// <paramref name="view"/> 末位是"正在形成中"的那根，会额外标注，避免玩家
        /// 把它当成已封的收盘价去下判断。
        /// </summary>
        private void DrawCandleHover(Rect plot, Rect area, List<FxCandle> view)
        {
            Event e = Event.current;
            if (e == null || view == null || view.Count == 0) return;
            if (!area.Contains(e.mousePosition)) return;
            if (LastSlotWidth <= 0.01f) return;

            int n = view.Count;
            int idx = (int)((e.mousePosition.x - area.x) / LastSlotWidth);
            if (idx < 0) idx = 0;
            if (idx >= n) idx = n - 1;

            FxCandle c = view[idx];
            bool forming = idx == n - 1;

            string body = "RimForex_HoverBody".Translate(
                c.open.ToString("F2"),
                c.high.ToString("F2"),
                c.low.ToString("F2"),
                c.close.ToString("F2"));

            // 涨跌用"收盘 vs 开盘"判定，与蜡烛配色同源
            string title = (c.close >= c.open ? "RimForex_HoverUp" : "RimForex_HoverDown").Translate();
            string text = body + "\n" + "RimForex_HoverRange".Translate(
                (c.high - c.low).ToString("F2"),
                c.open > 0.0001f ? ((c.close / c.open - 1f) * 100f).ToString("+0.00;-0.00;0.00") + "%" : "-");

            if (forming)
            {
                text += "\n\n" + "RimForex_HoverForming".Translate(SpanPeriodText());
            }

            TooltipHandler.TipRegion(area, new TipSignal(title + "\n\n" + text));
        }

        /// <summary>
        /// 算出图表的价格区间（含 10% 留白）。K 线本身和叠加线都必须用同一套
        /// min/max，否则持仓线会画在错误的高度。
        /// </summary>
        private static void ComputePriceRange(List<FxCandle> candles, out float min, out float max)
        {
            min = float.MaxValue; max = float.MinValue;
            for (int i = 0; i < candles.Count; i++)
            {
                if (candles[i].low < min) min = candles[i].low;
                if (candles[i].high > max) max = candles[i].high;
            }
            if (max - min < 0.0001f) { max += 0.5f; min -= 0.5f; }
            float pad = (max - min) * 0.10f;
            min -= pad; max += pad;
        }

        /// <summary>
        /// 持仓开仓价横线。
        ///
        /// ── 为什么做空的线在最新价线下方（这是常见疑问）──
        /// K 线图画的是<b>中间价 mid</b>，而 entryRate 是<b>方向成交价</b>：
        ///     做多开仓吃 ask = mid × (1 + 半价差)  → 线在 mid 上方
        ///     做空开仓吃 bid = mid × (1 - 半价差)  → 线在 mid 下方
        /// 偏移量恰好是半个点差，不是算错。
        ///
        /// 但只看线会误判"永远在亏"：做空要以 ask 买回，所以真正回本
        /// 需要 <b>mid 再跌一个完整点差</b>。这个信息写在图例里
        /// （见 <see cref="DrawChartOverlay"/>），否则玩家会以为图表坏了。
        ///
        /// 只画当前选中货币的持仓；别的货币的线画在这张图上没有意义。
        /// </summary>
        private void DrawPositionLines(Rect area, float min, float max)
        {
            if (account == null || account.positions.Count == 0) return;

            Color longCol = new Color(0.45f, 0.85f, 0.55f, 0.85f);
            Color shortCol = new Color(0.95f, 0.55f, 0.50f, 0.85f);

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;

            for (int i = 0; i < account.positions.Count; i++)
            {
                FxPosition p = account.positions[i];
                if (p == null) continue;
                if (p.currencyFactionLoadID != selectedFactionId) continue;
                if (p.entryRate < min || p.entryRate > max) continue;   // 超出可视区间就不画

                float y = PriceToY(p.entryRate, min, max, area);
                Color col = p.isLong ? longCol : shortCol;

                // 虚线：每 6px 画 3px，比实线更容易和 K 线区分开
                for (float x = area.x; x < area.xMax; x += 6f)
                {
                    Widgets.DrawBoxSolid(new Rect(x, y, Mathf.Min(3f, area.xMax - x), 1f), col);
                }

                string tag = (p.isLong ? "RimForex_Long" : "RimForex_Short").Translate()
                    + " " + p.entryRate.ToString("F2");
                Vector2 sz = Text.CalcSize(tag);
                Rect lbl = new Rect(area.x + 2f, y - sz.y - 1f, sz.x + 6f, sz.y);
                Widgets.DrawBoxSolid(lbl, new Color(0.08f, 0.09f, 0.11f, 0.85f));
                GUI.color = col;
                Widgets.Label(new Rect(lbl.x + 3f, lbl.y, lbl.width, lbl.height), tag);
                GUI.color = Color.white;

                // 悬停说明：把"开仓价 vs 现价"和自己的盈亏摆在一起，
                // 免得玩家因为线在最新价下方而误以为图表出错。
                string tip = "RimForex_PosLineTip".Translate(
                    p.entryRate.ToString("F2"),
                    p.markRate.ToString("F2"),
                    p.Pnl.ToString("+0;-0;0"));
                TooltipHandler.TipRegion(lbl, tip);
            }

            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        /// <summary>
        /// 强平线：当前持仓会在哪个价位被强制平仓。
        ///
        /// ── 推导 ──
        /// 强平判据是 <c>eq &lt;= margin × StopOutFraction</c>（见 CheckMarginCalls），
        /// 而 <c>eq = margin + pnl</c>、<c>pnl = units × (mark − entry)</c>（多）。
        /// 解出强平时的方向价：
        ///     <c>mark* = entry − StopOutFraction × margin / units</c>   （多）
        /// 做空对称，为 <c>entry + StopOutFraction × margin / units</c>。
        ///
        /// 坐标轴是中间价，所以再把 mark* 还原成 mid：
        ///     <c>mid* = mark* / (1 ∓ 半价差)</c>
        ///
        /// ⚠️ 这里用的是 <c>margin</c>（<b>含追加保证金</b>），
        /// 因为强平判据用的就是 <c>pos.margin × StopOutFraction</c>。
        /// 追加保证金会把强平线推远，这个函数要如实反映 —— 否则玩家补了保证金
        /// 却发现线没动，会以为补了个寂寞。
        ///
        /// 用真实爆仓单验证过：紫色B LONG 2457 单位 / 保证金 3356.26
        /// → 算得强平价 133.1085，实际强平时 exitRate = 133.0078（已跌破）。
        ///
        /// 只画该货币<b>最新一笔</b>持仓的线 —— 多笔同货币时，最新那笔才是"当前这一注"。
        /// </summary>
        /// <summary>
        /// 判断某个纵向位置能否放标签：必须在区间内，且与所有保留区保持至少
        /// <paramref name="clear"/> 的间距。抽出来是因为候选试探与兜底扫描共用同一判据。
        /// </summary>
        private static bool IsLabelSlotFree(float cand, float minY, float maxY,
            float[] reserved, float clear)
        {
            if (cand < minY || cand > maxY) return false;
            for (int j = 0; j < reserved.Length; j++)
            {
                if (Mathf.Abs(cand - reserved[j]) < clear) return false;
            }
            return true;
        }

        /// <summary>
        /// 图表叠加文字的<b>统一布局</b>：给定期望位置，返回一个不与既有元素重叠的 y。
        ///
        /// ── 为什么要收成一个函数 ──
        /// "避让"这个坑我踩了<b>两次</b>：先给爆仓线标签写了避让，
        /// 后来新增边缘标记时没复用，同一个 bug 又出现一次（文字压在底部图例上）。
        /// 根因是保留区当时是各写各的局部变量，新代码天然会绕过。
        /// 现在集中在这里，谁要放图表文字都走这条路径。
        ///
        /// 保留区（自上而下）：
        ///   area.y + 2      —— 盈亏平衡线标签
        ///   area.yMax − 50  —— 持仓线图例（由 DrawChartOverlay 画在 plot 上）
        ///   area.yMax − 34  —— 「显示 N 根 | 每根…」
        /// </summary>
        private static float ReserveLabelY(Rect area, float want, params float[] extraTries)
        {
            const float Clear = 16f;
            float minY = area.y + 2f;
            float maxY = area.yMax - Clear;
            float[] reserved = { area.y + 2f, area.yMax - 34f, area.yMax - 50f };

            if (IsLabelSlotFree(want, minY, maxY, reserved, Clear)) return want;

            for (int i = 0; i < extraTries.Length; i++)
            {
                if (IsLabelSlotFree(extraTries[i], minY, maxY, reserved, Clear)) return extraTries[i];
            }

            // 兜底：从期望位置向外逐像素扫描第一个空位。
            // 有些价位附近确实没空位，只试固定候选会漏（实测 41 个采样点里漏 4 个）。
            for (int d = 1; d < (int)area.height; d++)
            {
                if (IsLabelSlotFree(want - d, minY, maxY, reserved, Clear)) return want - d;
                if (IsLabelSlotFree(want + d, minY, maxY, reserved, Clear)) return want + d;
            }
            return Mathf.Clamp(want, minY, maxY);
        }


        /// <summary>
        /// 爆仓线超出坐标轴时的边缘指示。
        ///
        /// 画一条贴在上下边缘的橙红虚线段，加一个指向出界方向的箭头，
        /// 并标出爆仓价 —— 玩家一眼能看出"线还在，只是跑到框外了"。
        ///
        /// <paramref name="below"/> = true 表示爆仓线在<b>下方</b>出界（做多常见）。
        /// </summary>
        private void DrawStopOutEdgeMarker(Rect area, bool below, float midStop, float entryRate)
        {
            Color col = new Color(0.98f, 0.45f, 0.35f, 0.95f);
            float y = below ? area.yMax - 4f : area.y + 4f;

            // 上下各一小段虚线，暗示"线在框外"
            for (float x = area.x; x < area.x + 28f; x += 6f)
            {
                Widgets.DrawBoxSolid(new Rect(x, y, 3f, 1f), col);
            }
            for (float x = area.xMax - 28f; x < area.xMax; x += 6f)
            {
                Widgets.DrawBoxSolid(new Rect(x, y, 3f, 1f), col);
            }

            // 箭头：出界在下方就向下，在上方就向上
            float ax = area.x + 16f;
            for (int k = 0; k < 5; k++)
            {
                float wy = below ? y + 4f + k : y - 4f - k;
                float w = (5 - k) * 2f;
                if (wy < area.y || wy > area.yMax) continue;
                Widgets.DrawBoxSolid(new Rect(ax - w * 0.5f, wy, w, 1f), col);
            }

            string tag = "RimForex_ChartStopOutOff".Translate(
                midStop.ToString("F2"),
                ((midStop / entryRate - 1f) * 100f).ToString("+0.00;-0.00;0.00"));
            Text.Font = GameFont.Tiny;
            GUI.color = col;

            // ── 文字也要避让 ──
            // 上下边缘正是最挤的地方：底部有「显示 N 根…」与持仓线图例，
            // 顶部有「回本 xxx」标签，右侧还有轴上的价格刻度。
            // 上次只给爆仓线标签做了避让，边缘标记是新代码漏掉了 ——
            // 结果文字直接压在图例上（截图里两行字叠在一起）。
            // 这里复用同一套保留区判据。
            float ty = ReserveLabelY(area, below ? y - 16f : y + 4f,
                                     below ? y - 32f : y + 20f,
                                     area.y + 20f, area.y + 38f);

            // 先量文字宽度，再铺一块深色底 —— 保证与任何元素并排时都读得清。
            // 纵向避让已把"上下叠字"解决，但文字横向有 200+ px，
            // 与底部图例并排时仍可能横向压字；底色是最后一道保险。
            Vector2 tsz = Text.CalcSize(tag);

            // 曾经这里从箭头引一条竖线到文字，想维持"这条线通向那里"的关联。
            // 实测多余：文字被避让推远后，竖线会纵向穿过整张 K 线图，
            // 又乱又挡行情；而且文字自带橙红色 + 深色底，指向性已经够了。
            // 删掉更干净。

            Rect tbg = new Rect(area.x + 38f, ty - 1f, tsz.x + 8f, tsz.y + 2f);
            Widgets.DrawBoxSolid(tbg, new Color(0.08f, 0.09f, 0.11f, 0.85f));
            Widgets.Label(new Rect(area.x + 42f, ty, tsz.x + 4f, 16f), tag);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }


        private void DrawStopOutLine(Rect area, float min, float max)
        {
            if (account == null) return;
            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg != null && !cfg.showStopOutLine) return;

            FxPosition newest = null;
            for (int i = account.positions.Count - 1; i >= 0; i--)
            {
                FxPosition p = account.positions[i];
                if (p != null && p.currencyFactionLoadID == selectedFactionId) { newest = p; break; }
            }
            if (newest == null || newest.units == 0) return;
            if (newest.entrySpreadRate <= 0f) return;

            float half = newest.entrySpreadRate * 0.5f;
            float denom = newest.isLong ? (1f - half) : (1f + half);
            if (denom <= 0.0001f) return;

            // ── 强平距离（方向价空间）──────────────────────────────
            //
            // 从开仓价到强平价，价格还能走：
            //     可亏损净值 / 单位数 = (1 − StopOutFraction) × margin / units
            //
            // ⚠️ 这里原来写的是 `StopOutFraction × margin / units`（2026-10-20 修）。
            // 当 StopOutFraction = 0.5 时 0.5 == 1 − 0.5，错误看不出来；
            // 改成 0.3 后，这个式子只算出 30% 的距离，
            // **K 线上的爆仓线被画到了实际位置的 3/7 处** ——
            // 表现为"价格早就越过爆仓线了，却还没被强平"。
            //
            // 2026-10-21：基准从 margin 改成 BaseMargin。
            // 强平判据用初始保证金（追加保证金不该抬高强平线），
            // 画线必须跟判据同口径，否则补过保证金的仓位会画出一条偏远的假线。
            // ⚠️ 2026-10-25 修：这里原来只算 `(1-stopOut) × BaseMargin`，
            // 画出来的线**离现价太近**，而且追加保证金后完全不动。
            //
            // 判据是：margin + pnl <= stopOut × BaseMargin
            //   → pnl <= -( (1-stopOut) × BaseMargin + addedMargin )
            // 因为 margin = BaseMargin + addedMargin，追加的那部分必须加回来。
            // 漏掉 addedMargin 的后果（作者实测）：补了保证金后线不动，
            // 价格跌破画出来的线却没有爆仓 —— 实际强平点在一个更远的地方。
            float baseM = newest.BaseMargin;
            if (baseM < 0.01f) baseM = newest.margin;
            float dist = ((1f - FxGameComponent.StopOutFraction) * baseM + newest.addedMargin)
                       / Mathf.Abs((float)newest.units);
            float markStop = newest.isLong
                ? newest.entryRate - dist
                : newest.entryRate + dist;
            float midStop = markStop / denom;

            if (midStop <= 0f) return;

            // ── 超出可视区间时画【边缘箭头】，而不是直接消失 ──
            // 这是"做空有时看不到爆仓线"的真因：
            // 做空的爆仓线在现价【上方】，而做空通常是因为价格在涨 ——
            // 也就是现价接近坐标轴上界。于是爆仓线经常落在轴外，
            // 随窗口滚动时就有时候在框内、有时候被裁掉。
            //
            // 坐标轴不可能画下所有价位，所以正确的做法是：
            // 出界时在对应边缘钉一个箭头 + 距离读数，明确告诉玩家"线还在，只是在上/下方"。
            if (midStop < min || midStop > max)
            {
                DrawStopOutEdgeMarker(area, midStop < min, midStop, newest.entryRate);
                return;
            }

            float y = PriceToY(midStop, min, max, area);
            Color col = new Color(0.98f, 0.45f, 0.35f, 0.95f);

            // 用【虚线】与盈亏平衡线（实线）区分开
            for (float x = area.x; x < area.xMax; x += 6f)
            {
                Widgets.DrawBoxSolid(new Rect(x, y, Mathf.Min(3f, area.xMax - x), 1f), col);
            }

            // ── 标签贴在【爆仓线自己的左上方】──
            // 原先放绘图区左下角（area.yMax − 18），而那里正是
            // DrawChartOverlay 画「显示 N 根 | 每根…」历史图例的位置 —— 两行字直接叠在一起。
            //
            // 现在跟着线走，和开仓价标签的做法一致（都贴在自己的线上方）。
            // 这样无论线停在哪里，标签都不会跑到别的元素上。
            //
            // 但「贴线」还不够 —— 绘图区里已经有三个固定元素：
            //   左上角 = 盈亏平衡线标签
            //   底部   = 「显示 N 根…」与持仓线图例（由 DrawChartOverlay 画在 plot 上）
            // 而且坐标原点不同（标签相对 area，图例相对 plot），硬算容易算错。
            // 所以这里用【主动避让】：在若干个候选位置里挑第一个不撞的。
            string tag = "RimForex_ChartStopOut".Translate(midStop.ToString("F2"));
            Text.Font = GameFont.Tiny;

            // 贴线放，交给统一布局避让既有元素。
            // 先试离图例更远的候选，避开"线的正下方就是图例"那种情况。
            float lblY = ReserveLabelY(area, y - 36f, y + 21f, y - 18f, y + 3f);
            Rect lbl = new Rect(area.x + 4f, lblY, 170f, 16f);
            GUI.color = col;
            Widgets.Label(lbl, tag);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            TooltipHandler.TipRegion(lbl, "RimForex_ChartStopOutTip".Translate(
                (newest.isLong ? "RimForex_Long" : "RimForex_Short").Translate(),
                newest.entryRate.ToString("F2"),
                midStop.ToString("F2"),
                (dist / newest.entryRate * 100f).ToString("0.00")));
        }

        /// <summary>
        /// 盈亏平衡线：<b>中间价</b>要走到这个位置，该货币的持仓浮盈浮亏才归零。
        ///
        /// ── 与"开仓价线"的区别 ──
        /// 开仓价线画的是成交价（含半个点差）；而本线画的是<b>中间价</b>，
        /// 因为 K 线图的纵轴就是中间价，两者同一口径才能直接比较。
        ///
        /// 开仓卖出吃 bid = M₀(1 − h)，平仓买回吃 ask = M(1 + h)。
        /// 盈亏归零即 M(1 + h) = M₀(1 − h)，解得：
        ///     <c>M_breakeven = M₀ × (1 − h) / (1 + h)</c>   （做空）
        /// 做多对称，为 M₀ × (1 + h) / (1 − h)。
        /// 也就是说：<b>中间价必须比开仓时朝有利方向走满一整个往返点差才回本</b>。
        ///
        /// 只画该货币最新一笔持仓的线 —— 多笔同货币时，最新那笔才是"当前这一注"。
        /// </summary>
        private void DrawBreakevenLine(Rect area, float min, float max)
        {
            if (account == null) return;
            // 可在模组设置里关掉 —— 仓位多、线多的时候会显得杂
            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg != null && !cfg.showBreakevenLine) return;

            FxPosition newest = null;
            for (int i = account.positions.Count - 1; i >= 0; i--)
            {
                FxPosition p = account.positions[i];
                if (p != null && p.currencyFactionLoadID == selectedFactionId) { newest = p; break; }
            }
            if (newest == null || newest.entrySpreadRate <= 0f) return;

            FxCurrency cur = Fx.market.Get(selectedFactionId);
            if (cur == null) return;

            float half = newest.entrySpreadRate * 0.5f;
            float mid0 = newest.entryMid > 0f ? newest.entryMid : cur.rate;
            float denom = newest.isLong ? (1f - half) : (1f + half);
            if (denom <= 0.0001f) return;
            float beMid = mid0 * (newest.isLong ? (1f + half) : (1f - half)) / denom;

            if (beMid < min || beMid > max) return;    // 超出可视区间就不画

            float y = PriceToY(beMid, min, max, area);
            Color col = new Color(0.55f, 0.72f, 0.98f, 0.95f);

            Widgets.DrawBoxSolid(new Rect(area.x, y, area.width, 1f), col);

            // ── 标签移到【绘图区左上角】 ──
            // 原先贴在右侧（area.xMax − 宽 − 8），而最新几根 K 线正好在最右边，
            // 标签连同它的深色背景把最新的行情整个盖住了 —— 那恰恰是最该看清的部分。
            // 左上角则永远是空的：价格线受 10% 留白保护，不会顶到绘图区上沿；
            // 开仓价标签虽然在左侧，但它们跟着各自的价格走，通常在中下部。
            // 为了彻底避免与开仓价标签重叠，这里【不再画深色背景】，只用彩色文字。
            string tag = "RimForex_ChartBreakeven".Translate(beMid.ToString("F2"));
            Text.Font = GameFont.Tiny;
            Rect lbl = new Rect(area.x + 4f, area.y + 2f, 160f, 16f);
            GUI.color = col;
            Widgets.Label(lbl, tag);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            TooltipHandler.TipRegion(lbl, "RimForex_ChartBreakevenTip".Translate(
                (newest.isLong ? "RimForex_Long" : "RimForex_Short").Translate(),
                newest.entryRate.ToString("F2"),
                beMid.ToString("F2")));
        }

        private static void DrawCandles(Rect plot, List<FxCandle> candles)
        {
            // ── 先把"本帧有没有成功画出 K 线"置为否 ──
            //
            // 这四个静态字段（ChartArea / ChartMin / ChartMax / ChartHasData）
            // 在画完之后供 DrawPositionLines、DrawBreakevenLine、DrawStopOutLine、
            // DrawCandleHover 使用 —— 它们需要知道 K 线的 y 轴范围。
            //
            // 之前这里只在真正画出 K 线时置 true，**没有任何地方置 false**。
            // 于是本函数从下面三个提前 return 之一离开时，标志会停留在上一帧的 true，
            // 而那三个绘图函数就拿着一份**过期的坐标**去画线 ——
            // 表现为持仓线/回本线/爆仓线闪到错误位置。
            //
            // 触发场景：窗口刚打开时尺寸尚未稳定、或某一刻数据为空。
            // 有持仓且恰好碰上才看得见，所以一直没被发现。
            //
            // 统一在开头置 false、末处置 true，就不会漏掉任何退出路径。
            ChartHasData = false;

            if (plot.width < 40f || plot.height < 40f) return;

            Widgets.DrawBoxSolid(plot, new Color(0.10f, 0.11f, 0.13f, 1f));

            if (candles == null || candles.Count == 0)
            {
                Widgets.Label(plot.ContractedBy(6f), "RimForex_ChartNoData".Translate());
                return;
            }

            float min, max;
            ComputePriceRange(candles, out min, out max);
            float plotW = plot.width - 52f;
            float plotH = plot.height - 16f;
            if (plotW < 20f || plotH < 20f) return;

            Rect area = new Rect(plot.x + 4f, plot.y + 6f, plotW, plotH);

            Color grid = new Color(1f, 1f, 1f, 0.07f);
            for (int g = 0; g <= 4; g++)
            {
                float gy = area.y + area.height * g / 4f;
                Widgets.DrawBoxSolid(new Rect(area.x, gy, area.width, 1f), grid);
            }

            int n = candles.Count;
            float slot = area.width / n;
            LastSlotWidth = slot;   // 悬停命中判定要用同一个槽宽
            float bodyW = Mathf.Max(2f, slot * 0.62f);
            Color up = new Color(0.42f, 0.82f, 0.48f, 1f);
            Color down = new Color(0.90f, 0.38f, 0.34f, 1f);

            for (int i = 0; i < n; i++)
            {
                FxCandle c = candles[i];
                float cx = area.x + slot * i + slot * 0.5f;
                bool isUp = c.close >= c.open;
                Color col = isUp ? up : down;

                float yHigh = PriceToY(c.high, min, max, area);
                float yLow = PriceToY(c.low, min, max, area);
                Widgets.DrawBoxSolid(new Rect(cx - 0.5f, yHigh, 1f, Mathf.Max(1f, yLow - yHigh)), col);

                float yOpen = PriceToY(c.open, min, max, area);
                float yClose = PriceToY(c.close, min, max, area);
                float top = Mathf.Min(yOpen, yClose);
                float h = Mathf.Max(1.5f, Mathf.Abs(yClose - yOpen));
                Widgets.DrawBoxSolid(new Rect(cx - bodyW * 0.5f, top, bodyW, h), col);
            }

            // 最新价横线
            float lastClose = candles[n - 1].close;
            float lastY = PriceToY(lastClose, min, max, area);
            Widgets.DrawBoxSolid(new Rect(area.x, lastY, area.width, 1f), new Color(0.55f, 0.75f, 0.95f, 0.55f));
            ChartArea = area;
            ChartMin = min;
            ChartMax = max;
            ChartHasData = true;

            // 价格刻度放最后画，压在 K 线与持仓线之上
            //
            // ── 小数位数必须由「每格跨度」决定，不能由「价格量级」决定 ──
            // 原写法是 `p >= 100f ? "F0" : "F2"`：价格上百就取整。
            // 但价格高不代表跨度大 —— 联邦信用点在 116~117 之间运行时，
            // 五格跨度只有 0.86，每格 0.17，取整后全部塌成 116/116/116/117/117，
            // 看上去像坐标轴坏了（实测截图确实是 117、117、116、116、116）。
            //
            // 正确做法：按每格跨度算出「至少需要几位小数才能让相邻标签不同」。
            // 例如跨度 0.17 → 小数点后第 1 位有效 → 取 2 位；跨度 20 → 取 0 位。
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            float step = (max - min) / 4f;
            int dec = PriceDecimals(step);
            for (int g = 0; g <= 4; g++)
            {
                float p = max - (max - min) * g / 4f;
                float gy = area.y + area.height * g / 4f;
                Widgets.Label(new Rect(area.xMax + 4f, gy - 8f, 46f, 16f),
                    p.ToString(dec == 0 ? "F0" : "F" + dec));
            }
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;

            Widgets.Label(new Rect(plot.x + 6f, plot.yMax - 18f, plot.width - 60f, 16f),
                "RimForex_ChartLast".Translate(lastClose.ToString("F2")));
        }

        /// <summary>本帧 K 线绘图区的价格边界，供持仓线复用（避免两处各算一遍导致错位）。</summary>
        private static Rect ChartArea;
        private static float ChartMin;
        private static float ChartMax;
        private static bool ChartHasData;
        /// <summary>本帧每根 K 线占用的横向像素。悬停命中判定必须与绘制用同一个值。</summary>
        private static float LastSlotWidth;

        private static float PriceToY(float price, float min, float max, Rect area)
        {
            float t = (price - min) / (max - min);
            return area.yMax - t * area.height;
        }

        // ============================ 下单区 ============================

        /// <summary>
        /// 开 1 单位所需的最低保证金（含手续费）。
        /// 用 ask 作单价是最保守的口径：做多时正好，做空时会略微高估，不会算出买不起的量。
        /// </summary>
        private static float MinMarginFor(float ask, float leverage)
        {
            if (ask <= 0.01f || leverage <= 0f) return 0f;
            float perUnitNotional = ask;
            return perUnitNotional / leverage + FxGameComponent.FeeFor(perUnitNotional);
        }

        /// <summary>
        /// 由保证金金额算出最多能开多少单位。
        ///
        /// ── 为什么不能写"逐次减一"（2026-10-17 修）──
        /// 原来的写法是从 floor(margin / perUnitMargin) 开始，每次减 1 直到负担得起。
        /// 看起来很安全，实际收敛步数是「手续费 ÷ 每单位保证金」：
        ///     输入上限 1 亿（见 DrawOrderZone 的 TextFieldNumeric）、价格 113、杠杆 100
        ///     → 初值约 8849 万单位、手续费约 200 万
        ///     → 实测需要递减 1,728,014 次
        /// 而本函数在 DoWindowContents 里**每帧**都会调用一次，
        /// 于是玩家在保证金框里输入一个大数字就会把终端卡死（百万级浮点运算/帧）。
        ///
        /// ── 为什么用二分而不是开根号式的闭式解 ──
        /// 手续费是 max(MinFee, 名义 × FeeRate)，是u的分段线性函数，
        /// 闭式解要分两个区间各解一次、还要判断解是否落在自己的区间里 —— 容易写错。
        /// 而"给定u是否负担得起"是单调的（u 买得起，则 u−1 必然也买得起），
        /// 所以直接二分即可：
        ///     · 迭代次数 = log2(u)，u 最大 1e9 时也只有 **30 次**
        ///     · 与输入规模无关，且不依赖任何舍入假设
        ///     · 不低估也不高估：二分端点就是精确答案
        ///
        /// 上界取无手续费的 floor(margin / perUnitMargin)：手续费只会让结果更小，
        /// 所以它一定是个安全的上界。
        /// </summary>
        private static int EstimateUnits(int margin, float ask, float leverage)
        {
            if (ask <= 0.01f || leverage <= 0f || margin <= 0) return 0;

            float perUnitMargin = ask / leverage;
            if (perUnitMargin <= 0f) return 0;

            // 单位数是 int，而 margin 上限 1 亿、ask 最低 1、杠杆最高 100 时
            // 理论上能算出 1e10 单位 —— 直接转 int 会溢出成负数。
            // 这个量级本来也没有意义（余额根本不够），封个顶防止溢出。
            const double MaxUnits = 1e9;

            int hi = (int)System.Math.Min(margin / (double)perUnitMargin, MaxUnits);
            if (hi <= 0) return 0;

            // 二分开区间 [0, hi]，找最大的"负担得起"的 u
            int lo = 0;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (Affordable(mid, ask, leverage, margin)) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>给定单位数，保证金 + 手续费是否仍在预算内。</summary>
        private static bool Affordable(int units, float ask, float leverage, float margin)
        {
            if (units <= 0) return true;
            float m, n, f;
            int real;
            ComputeEntry(units, ask, leverage, out real, out m, out n, out f);
            return m + f <= margin + 0.0001f;
        }

        /// <summary>
        /// 由单位数算出实际成交量、实际占用保证金、名义仓位与手续费。
        ///
        /// 关键点：单位是<b>整数</b>，所以实际保证金一般略低于玩家输入的金额。
        /// 例：输入 120 白银、每单位需 11.4 → 只能开 10 单位 → 实际占用 114，剩下的 6 不扣。
        /// 这样账目永远精确，不会出现"扣了钱但仓位对不上"的情况。
        /// </summary>
        private static void ComputeEntry(int wantUnits, float ask, float leverage,
            out int units, out float margin, out float notional, out float fee)
        {
            units = wantUnits > 0 ? wantUnits : 0;
            notional = units * ask;
            margin = leverage > 0f ? notional / leverage : notional;
            fee = FxGameComponent.FeeFor(notional);
        }

        /// <summary>忽略手续费的重载，用于只需要保证金的场合。</summary>
        private static void ComputeEntry(int wantUnits, float ask, float leverage, out float margin)
        {
            int u;
            float n, f;
            ComputeEntry(wantUnits, ask, leverage, out u, out margin, out n, out f);
        }

        private void DrawOrderZone(Rect zone, FxGameComponent fx)
        {
            Widgets.DrawMenuSection(zone);
            Rect r = zone.ContractedBy(Pad);

            float leftW = Mathf.Clamp(r.width * 0.46f, 380f, r.width - 240f);
            Rect left = new Rect(r.x, r.y, leftW, r.height);
            Rect right = new Rect(r.x + leftW + Gap, r.y, r.width - leftW - Gap, r.height);

            FxCurrency sel = fx.market.Get(selectedFactionId);

            // ---- 左：下单 ----
            Widgets.Label(new Rect(left.x, left.y, left.width, 22f),
                "RimForex_OrderFor".Translate(sel != null ? sel.PairLabel : "RimForex_NoSelection".Translate()));

            float ask = sel != null ? FxMarket.AskOf(sel) : 0f;
            float bid = sel != null ? FxMarket.BidOf(sel) : 0f;
            float spreadPct = FxMarket.SpreadRateFor(sel) * 100f;

            // 切换货币后，原来选的杠杆可能对新高波动品种不安全（点差 × 杠杆 超过强平线）。
            // 这里自动降档到该品种的最大安全档位，避免玩家在不知情的情况下开出必死的仓位。
            if (sel != null)
            {
                int capIdx = MaxLadderIndex(FxGameComponent.MaxSafeLeverageFor(sel));
                if (LadderIndexOf(orderLeverage) > capIdx) orderLeverage = LeverageLadder[capIdx];
            }

            // 能开 1 单位所需的最低保证金（含手续费）。数量框的最小值就是它。
            float minMargin = MinMarginFor(ask, orderLeverage);

            float y = left.y + 24f;
            Widgets.Label(new Rect(left.x, y, 76f, 24f), "RimForex_MarginInput".Translate());
            Rect marginRect = new Rect(left.x + 80f, y, 92f, 24f);
            if (orderMargin < minMargin)
            {
                orderMargin = Mathf.CeilToInt(minMargin);
                marginBuffer = orderMargin.ToString();
            }
            int marginBeforeEdit = orderMargin;
            Widgets.TextFieldNumeric(marginRect, ref orderMargin, ref marginBuffer, minMargin, 100000000f);
            // 玩家自己改了数字 —— 不再替他跟随最小值
            if (orderMargin != marginBeforeEdit) marginAtMinimum = false;
            y += 27f;

            // 加减档 + 梭哈 + 归零（单位：白银）
            //
            // ── 尺寸按面板宽度【动态】算，不能写死 ──
            // 曾经写死 44pxx8 + 74 + 52，一行合计 496px，
            // 而左侧面板被 `Clamp(r.width*0.46, 380, ...)` 钳在 380~400px ——
            // 超出约 116px，"梭哈"和"最小值"直接压在右侧持仓行上。
            // 写死尺寸的毛病在于面板宽度随窗口变，任何固定值迟早会溢出。
            const float stepGap = 2f;      // 档位按钮之间
            const float allInGap = 2f;     // 最后一个档位与"梭哈"之间
            const float allInW  = 66f;
            const float zeroW   = 50f;
            const float tailGap = 4f;      // "梭哈"与"最小值"之间
            int[] steps = { -1000, -100, -10, -1, 1, 10, 100, 1000 };

            // 预算必须把 allInGap 也算进去 —— 漏掉它就会超出 2px（实测过）。
            float usedByTail = allInGap + allInW + tailGap + zeroW;
            float stepW = (left.width - usedByTail - stepGap * (steps.Length - 1)) / steps.Length;
            // 下限取 30px：Tiny 字体下 "+1000" 需要约 30px，再窄就会截断成 "+10…"
            if (stepW < 30f) stepW = 30f;
            if (stepW > 44f) stepW = 44f;      // 面板很宽时不必跟着铺满

            float sx = left.x;
            for (int i = 0; i < steps.Length; i++)
            {
                int d = steps[i];
                Rect sr = new Rect(sx, y, stepW, 24f);
                string label = (d > 0 ? "+" : "") + d.ToString();
                if (Widgets.ButtonText(sr, label))
                {
                    orderMargin = Mathf.Max(Mathf.CeilToInt(minMargin), orderMargin + d);
                    marginBuffer = orderMargin.ToString();
                    marginAtMinimum = false;
                }
                sx += stepW + stepGap;
            }

            // 梭哈：由"可用余额能开的最大单位数"反推所需保证金
            Rect allIn = new Rect(sx + allInGap, y, allInW, 24f);
            GUI.color = new Color(1f, 0.82f, 0.55f);
            if (Widgets.ButtonText(allIn, "RimForex_AllIn".Translate()))
            {
                float askForMax = sel != null ? sel.rate * (1f + FxMarket.SpreadRateFor(sel) * 0.5f) : 0f;
                int maxUnits = FxGameComponent.MaxAffordableUnits(account.balance, askForMax, orderLeverage);

                // ── 必须回填【保证金 + 手续费】，不能只回填保证金（2026-10-21 修 R08）──
                //
                // 这个输入框的语义是"这次最多花多少白银"：下单路径会拿它当预算，
                // 用 EstimateUnits 反推能开多少单位（预算 = 保证金 + 手续费）。
                // 原来只回填保证金，下单时又扣一次手续费 —— 于是梭哈开出来的量
                // 比真正买得起的少一截：实测余额 10000、价 100、100x 时，
                // 最大可开 9803 单位，但梭哈只开到 9610，白剩约 198 白银。
                float m;
                ComputeEntry(maxUnits, askForMax, orderLeverage, out m);
                float feeForMax = FxGameComponent.FeeFor(maxUnits * askForMax);
                orderMargin = Mathf.Max(Mathf.CeilToInt(minMargin), Mathf.CeilToInt(m + feeForMax));
                marginBuffer = orderMargin.ToString();
                marginAtMinimum = false;
            }
            GUI.color = Color.white;
            TooltipHandler.TipRegion(allIn, "RimForex_AllInTip".Translate());

            Rect zeroBtn = new Rect(allIn.xMax + tailGap, y, zeroW, 24f);
            if (Widgets.ButtonText(zeroBtn, "RimForex_Zero".Translate()))
            {
                orderMargin = Mathf.CeilToInt(minMargin);
                marginBuffer = orderMargin.ToString();
                marginAtMinimum = true;   // 记住"我要的就是最小值"，改杠杆时跟过去
            }
            TooltipHandler.TipRegion(zeroBtn, "RimForex_ZeroTip".Translate());
            y += 29f;

            Widgets.Label(new Rect(left.x, y, 52f, 24f), "RimForex_Leverage".Translate());

            // ── 杠杆：滑块（2026-10-23 改，原来是一排按钮）──
            //
            // 为什么用**离散档位**而不是 1~100 连续：
            // 线性滑轨上 1~10 只占 9%，低倍区根本点不准；而实际常用的正是低倍。
            // 档位：1 · 2 · 3 · 5 · 8 · 10 · 15 · 20 · 30 · 50 · 75 · 100
            //
            // 滑块上限**夹到该品种的安全杠杆**：点差占保证金的比例不能超过强平线，
            // 否则一解锁就会被强平。所以滑块拖到头就是"这个品种能用的最高档"，
            // 拖不到 100x 时右侧会写明上限，免得玩家以为滑块坏了。
            float maxSafe = FxGameComponent.MaxSafeLeverageFor(sel);
            int maxIdx = MaxLadderIndex(maxSafe);
            int curIdx = Mathf.Clamp(LadderIndexOf(orderLeverage), 0, maxIdx);

            float sliderW = Mathf.Clamp(left.width - 230f, 120f, 220f);
            Rect sliderR = new Rect(left.x + 56f, y + 1f, sliderW, 22f);
            // 防御：若某品种连 2x 都撑不住（正常参数下不会发生），
            // 滑轨的 min==max 会让原版 Slider 里出现 0/0。给一个最小跨度避免 NaN。
            int sliderMax = Mathf.Max(1, maxIdx);
            int newIdx = Mathf.RoundToInt(Widgets.HorizontalSlider(
                sliderR, curIdx, 0f, sliderMax, true, null, null, null, 1f));
            if (newIdx != curIdx)
            {
                newIdx = Mathf.Clamp(newIdx, 0, maxIdx);
                orderLeverage = LeverageLadder[newIdx];
                float newMin = MinMarginFor(ask, orderLeverage);
                if (marginAtMinimum)
                {
                    // 上次按的就是「最小值」→ 跟着新杠杆重算（高杠杆会变小，低杠杆会变大）
                    orderMargin = Mathf.CeilToInt(newMin);
                    marginBuffer = orderMargin.ToString();
                }
                else if (orderMargin < newMin)
                {
                    // 只托底：降到低杠杆时旧金额可能连 1 单位都开不出
                    orderMargin = Mathf.CeilToInt(newMin);
                    marginBuffer = orderMargin.ToString();
                }
                curIdx = newIdx;
            }

            Widgets.Label(new Rect(sliderR.xMax + 6f, y, 54f, 24f),
                LeverageLadder[curIdx].ToString("F0") + "x");

            // 该品种撑不满 100x 时才写上限；能拉满就不加噪音。
            if (maxIdx < LeverageLadder.Length - 1)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.72f, 0.72f, 0.72f);
                Widgets.Label(new Rect(sliderR.xMax + 60f, y + 5f, left.width - sliderW - 118f, 18f),
                    "RimForex_LevCap".Translate(maxSafe.ToString("0")));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            TooltipHandler.TipRegion(new Rect(left.x, y, left.width, 24f),
                FxGameComponent.LeverageSafeFor(sel, orderLeverage)
                    ? "RimForex_TipLevSafe".Translate(LeverageLadder[curIdx].ToString("F0"))
                    : "RimForex_TipLevUnsafe".Translate(
                        (spreadPct * orderLeverage).ToString("0"), maxSafe.ToString("0")));
            y += 30f;

            // ---- 杠杆风险提示 ----
            // 独占一"块"（可能是多行），并用 Text.CalcHeight 预留真实高度。
            // 早期把提示画在 y-4f、下一行画在 y —— 间隔只有 4px，必然重叠。
            string riskNote = null;
            Color riskColor = Color.white;
            if (sel != null && !FxGameComponent.LeverageSafeFor(sel, orderLeverage))
            {
                riskNote = "RimForex_LevUnsafeNote".Translate(maxSafe.ToString("0")).ToString();
                riskColor = new Color(1f, 0.62f, 0.55f);
            }
            else if (sel != null)
            {
                // 杠杆可用，但如果强平距离比一天波动还小，就把这个事实摆出来。
                // 不禁止 —— 梭哈本来就是这个 mod 的乐趣 —— 只是别让玩家不知情。
                //
                // 距离 = (1 − StopOut) / 杠杆，不是 StopOut / 杠杆。
                // 与 K 线爆仓线、MaxSafeLeverageFor 用的是同一个口径；
                // 0.5 时代两者相等，改 0.3 后必须一起改（2026-10-20）。
                float stopDist = (1f - FxGameComponent.StopOutFraction) / orderLeverage;
                float dailyVol = FxMarket.DailyVol * sel.volatility;
                if (stopDist < dailyVol)
                {
                    riskNote = "RimForex_LevRiskyNote".Translate(
                        (stopDist * 100f).ToString("0.00"),
                        (dailyVol * 100f).ToString("0.00"),
                        (stopDist / dailyVol).ToString("0.00")).ToString();
                    riskColor = new Color(1f, 0.80f, 0.45f);
                }
            }

            if (riskNote != null)
            {
                Text.Font = GameFont.Tiny;
                float noteH = Text.CalcHeight(riskNote, left.width);
                GUI.color = riskColor;
                Widgets.Label(new Rect(left.x, y, left.width, noteH), riskNote);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y += noteH + 3f;
            }

            // ---- 买价 / 卖价 / 点差 ----
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(left.x, y, left.width, 18f),
                "RimForex_LineBidAsk".Translate(
                    ask.ToString("F2"),
                    bid.ToString("F2"),
                    spreadPct.ToString("0.###")).ToString()
                .Colorize(new Color(0.72f, 0.80f, 0.92f)));
            y += 18f;

            // ---- 由保证金金额反解出实际可开的仓位 ----
            // 单位必须是整数，所以实际保证金可能略低于输入的金额：
            // 例如输入 120 白银、每单位需 11.4，只能开 10 单位，实际占用 114，余下 6 不会被扣。
            int wantUnits = EstimateUnits(orderMargin, ask, orderLeverage);
            int units;
            float actualMargin, notional, fee;
            ComputeEntry(wantUnits, ask, orderLeverage, out units, out actualMargin, out notional, out fee);

            float spreadCost = units * (ask - bid);
            float feePct = FxGameComponent.FeeRate * 100f;
            float totalCost = actualMargin + fee;
            bool enough = totalCost <= account.balance;
            bool canOpen = sel != null && units > 0 && enough;

            // 逐行成本明细可以在模组设置里关掉，只留总花费。
            // 关掉后省下的纵向空间直接让给下方的提示行，不会留空洞。
            RimForexSettings cfg = RimForexMod.Settings;
            bool showBreakdown = cfg == null || cfg.showCostBreakdown;

            if (showBreakdown)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(left.x, y, left.width, 16f),
                    "RimForex_LineSpreadCost".Translate(spreadCost.ToString("F1")).ToString());
                GUI.color = Color.white;
                y += 18f;

                GUI.color = new Color(0.78f, 0.80f, 0.82f);
                Widgets.Label(new Rect(left.x, y, left.width, 18f),
                    "RimForex_LineUnits".Translate(units.ToString("N0")));
                y += 17f;
                // 「名义仓位」= 数量 × 成交价，也就是这笔交易在市场里的实际规模
                Widgets.Label(new Rect(left.x, y, left.width, 18f),
                    "RimForex_LineNotional".Translate(notional.ToString("F0"), orderLeverage.ToString("F0")));
                y += 17f;
                Widgets.Label(new Rect(left.x, y, left.width, 18f),
                    "RimForex_LineMarginActual".Translate(actualMargin.ToString("F0")));
                y += 17f;
                Widgets.Label(new Rect(left.x, y, left.width, 18f),
                    "RimForex_LineFee".Translate(feePct.ToString("0.####"), fee.ToString("F2")));
                y += 17f;
                GUI.color = Color.white;
            }
            else
            {
                // 精简模式：把关键信息压成一行 tooltip，鼠标悬停即可查看
                y += 2f;
                GUI.color = new Color(0.78f, 0.80f, 0.82f);
                Widgets.Label(new Rect(left.x, y, left.width, 18f),
                    "RimForex_LineCompact".Translate(units.ToString("N0"), notional.ToString("F0")));
                TooltipHandler.TipRegion(new Rect(left.x, y, left.width, 18f),
                    "RimForex_LineCompactTip".Translate(
                        spreadCost.ToString("F1"), actualMargin.ToString("F0"), fee.ToString("F2")));
                GUI.color = Color.white;
                y += 17f;
            }

            Widgets.Label(new Rect(left.x, y, left.width, 20f),
                "RimForex_LineTotal".Translate(
                    totalCost.ToString("F0"),
                    account.balance.ToString("F0")).ToString()
                .Colorize(enough ? new Color(0.55f, 0.85f, 0.60f) : new Color(1f, 0.62f, 0.55f)));
            y += 21f;

            GUI.color = new Color(0.78f, 0.80f, 0.82f);
            Widgets.Label(new Rect(left.x, y, left.width, 18f),
                "RimForex_LineMaxMargin".Translate(account.balance.ToString("N0")));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 23f;

            float halfW = (left.width - Gap) / 2f;
            Rect longR = new Rect(left.x, y, halfW, 32f);
            Rect shortR = new Rect(left.x + halfW + Gap, y, halfW, 32f);

            if (!canOpen) GUI.color = Color.gray;
            if (Widgets.ButtonText(longR, "RimForex_GoLong".Translate()) && canOpen) OpenPosition(sel, true, units, orderLeverage);
            if (Widgets.ButtonText(shortR, "RimForex_GoShort".Translate()) && canOpen) OpenPosition(sel, false, units, orderLeverage);
            GUI.color = Color.white;

            y += 35f;
            if (sel == null)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(left.x, y, left.width, 18f), "RimForex_NoSelection".Translate());
                GUI.color = Color.white;
            }
            else if (!enough)
            {
                GUI.color = new Color(1f, 0.62f, 0.55f);
                Widgets.Label(new Rect(left.x, y, left.width, 18f), "RimForex_NotEnoughMargin".Translate());
                GUI.color = Color.white;
            }

            // ---- 右：持仓（每行带独立平仓按钮） ----
            //
            // ── 列头已移除（2026-10-16）──
            // 原因：列头占了整个第一行，和按钮、标题互相挤压 ——
            // 「操作」列头被「交易历史」按钮整格盖住，标题也被列区顶到左边。
            // 去掉列头后第一行只剩「当前持仓」+ 按钮，互不干扰；
            // 两列的含义改由数据自带的后缀标注（"+12345 盈亏" / "1234 成本"）。
            //
            // 列宽因此从"按列头文字定"改成"按内容定"，左侧文字区也跟着宽松了。
            float histW = 92f;

            Widgets.Label(new Rect(right.x, right.y, right.width - histW - ColGap, 22f),
                "RimForex_Positions".Translate());

            Rect histBtn = new Rect(right.xMax - histW, right.y - 2f, histW, 24f);
            if (Widgets.ButtonText(histBtn, "RimForex_HistoryBtn".Translate()))
            {
                OpenHistory();
            }
            TooltipHandler.TipRegion(histBtn, "RimForex_HistoryBtnTip".Translate());

            // ── 列偏移相对【列表右边缘】定义，不要相对 right.xMax ──
            // 滚动视图的宽度是 listRect.width − 18（给滚动条留位），
            // 所以数据行的 row.xMax 比 right.xMax 靠左 18px。
            // 早先列头按 right.xMax 推算、数据行按 row.xMax 推算，两套基准混用，
            // 结果「平仓」按钮和右上角的「交易历史」按钮水平重叠 17px
            //（垂直上只差 2px，滚动条一浮动就叠上）。
            // 另外横向也要让开按钮：按钮占了最右侧 histW，列表宽度必须扣掉。
            // 列表占满右侧面板；行宽扣掉 18px 给滚动条。
            //
            // 注意：列表与「交易历史」按钮在【水平上会重叠】，但那没关系 ——
            // 按钮在 right.y-2 ~ right.y+22，而列表从 right.y+24 开始，垂直差 24px，
            // 两者永远不会画在一起。所以这里不必为按钮让出横向空间。
            Rect listRect = new Rect(right.x, right.y + 24f, right.width, right.height - 24f);
            if (listRect.height <= 0f) return;

            float viewH = Mathf.Max(listRect.height, account.positions.Count * 30f + 4f);
            Rect viewRect = new Rect(0f, 0f, listRect.width - 18f, viewH);

            Widgets.BeginScrollView(listRect, ref positionScroll, viewRect);
            float ry = 0f;
            FxPosition toClose = null;
            for (int i = 0; i < account.positions.Count; i++)
            {
                FxPosition p = account.positions[i];
                Rect row = new Rect(0f, ry, viewRect.width, 30f);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);

                // ── 行首只留「货币 + 方向」──
                // 早先这里塞了「货币 方向 数量 开仓 现价 杠杆」，实测需要 384px，
                // 而右侧持仓区的行首可用宽度只有约 119px —— 超出 3 倍，
                // 于是文字溢出到盈亏/成本列上，持仓一多就"打架"。
                // 这些数字在悬停提示里全都有，没必要挤在行内。
                string dir = p.isLong ? "RimForex_Long".Translate() : "RimForex_Short".Translate();
                string line = p.currencyName + "  " + dir;

                // 两列的含义靠数据自带的后缀标注，因为列头已移除。
                //
                // 为什么还保留"成本"这一列：光有净盈亏会让玩家误判 ——
                // 锁定期一过数字就跳到 -点差×数量并一直挂着，看起来像"行情永远在跟自己作对"。
                // 分开显示后：净盈亏 = 纯行情盈亏 - 入场成本。
                // ── 盈亏数字用 GUI.color 分段绘制 ──
                // 之前这里用 ColorizeBySign（拼 <color=...> 标签），当时观察到持仓格里
                // 标签不生效、数字显示为白色。**但那个归因是错的**（2026-10-20 修正）：
                // 作者实测确认富文本着色在货币列表里工作正常（涨跌幅是彩色的），
                // 所以当初持仓格的问题另有原因（更可能是文本被截断或标签被拆开），
                // 不是"8 位 RGBA 不被 Unity 接受"。
                //
                // 保留 GUI.color 写法的理由不是"富文本不行"，而是：
                // 这一行要把【数字】和【灰色后缀】分成两种颜色画，
                // 用 GUI.color 逐段绘制比在字符串里塞标签更直观、也不依赖标签解析。
                //
                // ⚠️ 别照着"富文本不可用"这个旧结论去改别处的代码 —— 它可以正常用。
                string pnlNum = p.Pnl.ToString("+0;-0;0");
                string pnlTag = " " + "RimForex_PnlTag".Translate();
                string costText = p.SpreadCost.ToString("F0") + " " + "RimForex_CostTag".Translate();

                // 与上方 posPnlX / posCostX 同源：都以「行右边缘」为基准
                float rowActRight = row.xMax - ColActW;
                float rowCostX = rowActRight - ColGap - ColCostW;
                float rowPnlX = rowCostX - ColGap - ColPnlW;

                Widgets.Label(new Rect(row.x + 2f, row.y + 6f, rowPnlX - row.x - 6f, 20f), line);

                // 数字（按盈亏着色）
                float numW = Text.CalcSize(pnlNum).x;
                GUI.color = PnlGuiColor(p.Pnl);
                Widgets.Label(new Rect(rowPnlX, row.y + 6f, numW + 2f, 20f), pnlNum);
                GUI.color = Color.white;
                // 后缀（固定灰色），紧跟数字右侧
                GUI.color = new Color(0.70f, 0.70f, 0.70f);
                Widgets.Label(new Rect(rowPnlX + numW + 2f, row.y + 6f, ColPnlW - numW - 2f, 20f), pnlTag);
                GUI.color = Color.white;

                // 已付成本固定灰色，不参与涨跌配色 —— 它是成本，不是损益
                GUI.color = new Color(0.72f, 0.66f, 0.55f);
                Widgets.Label(new Rect(rowCostX, row.y + 6f, ColCostW, 20f), costText);
                GUI.color = Color.white;

                // 悬停：完整数据 + 盈亏拆分。
                // 行首不再显示数量/开仓/现价/杠杆，所以这些必须在这里补齐。
                TooltipHandler.TipRegion(
                    new Rect(row.x, row.y + 3f, row.width, 24f),
                    "RimForex_PosRowTip".Translate(
                        p.units, p.entryRate.ToString("F2"), p.markRate.ToString("F2"),
                        p.leverage.ToString("F0"),
                        p.Pnl.ToString("+0;-0;0"),
                        p.MarketPnl.ToString("+0;-0;0"),
                        p.SpreadCost.ToString("F0")).ToString());

                // 还没跨过报价的持仓不允许平仓（瞬时往返会被作废）。
                // 按钮上直接显示剩余秒数，否则玩家只会看到"点不动"却不知道要等多久。
                bool closable = fx.CanClose(p);
                Rect closeBtn = new Rect(rowActRight, row.y + 3f, ColActW, 24f);
                string closeLabel = closable
                    ? "RimForex_Close".Translate()
                    : (fx.TicksUntilClosable(p) / (float)FxGameComponent.TicksPerRealSecond).ToString("0.0") + "s";
                if (!closable) GUI.color = Color.gray;
                if (Widgets.ButtonText(closeBtn, closeLabel) && closable)
                {
                    toClose = p;
                }
                GUI.color = Color.white;
                TooltipHandler.TipRegion(closeBtn, closable
                    ? "RimForex_TipClose".Translate()
                    : "RimForex_TipTooSoon".Translate((fx.TicksUntilClosable(p) / (float)FxGameComponent.TicksPerRealSecond).ToString("0.0")));

                ry += 30f;
            }
            if (account.positions.Count == 0)
            {
                Widgets.Label(new Rect(2f, 4f, viewRect.width, 20f), "RimForex_NoPositions".Translate());
            }
            Widgets.EndScrollView();

            // 在滚动视图之外执行修改，避免遍历中改动集合
            if (toClose != null) ClosePosition(toClose);
        }

        // ============================ 交易操作 ============================

        /// <summary>
        /// 开仓。成交价按方向取（做多吃 ask、做空吃 bid），保证金与手续费都按该成交价计。
        /// markRate 立刻设为"现在平仓能拿到的价"（方向价的另一端），
        /// 所以开仓瞬间就会出现一个等于点差的小额浮亏 —— 这是真实市场的入场即亏点差。
        /// </summary>
        private void OpenPosition(FxCurrency cur, bool isLong, int units, float leverage)
        {
            if (cur == null || units <= 0) return;

            // E7：账户冻结期间禁止开仓
            if (account.frozenUntilTick > GenTicks.TicksGame)
            {
                Messages.Message("RimForex_MsgFrozen".Translate(
                    ((account.frozenUntilTick - GenTicks.TicksGame) / FxGameComponent.TicksPerDay)
                        .ToString("0")), MessageTypeDefOf.RejectInput, false);
                return;
            }

            FxMarket market = Fx.market;
            float entry = market.EntryPriceOf(cur.factionLoadID, isLong);
            float notional = units * entry;
            float margin = leverage > 0f ? notional / leverage : notional;
            float fee = FxGameComponent.FeeFor(notional);

            if (margin + fee > account.balance) return;

            account.balance -= margin + fee;
            account.feesSinceSettle += fee;   // 结算信要把手续费摊开给玩家看

            // 开仓也算"当天动过仓"。结算信件靠这个计数决定要不要吭声，
            // 否则"当天只开不平"的日子会被判成没动静，玩家收不到任何结算反馈。
            account.activityToday++;

            // 开仓时每单位承担的点差成本 = 方向开仓价 − 同方向的反向价。
            //
            // 不能写成 `entry - BidOf(id)`：entry 是【方向价】（做多吃 ask、做空吃 bid），
            // 所以做空时 entry 恰好就是 bid，差值恒为 0；
            // 而做多时若 AskOf/BidOf 与 entry 之间有任何采样偏差，
            // 差值里就混进了价格变动，能算出远超真实点差的值。
            // 实测历史里同一批交易的点差成本在 0 和 0.71（真实约 0.34）之间乱跳。
            //
            // 正确口径：用 ExitPriceOf（按方向平仓能拿到的价）与 entry 相减，
            // 对做多做空都恰好等于一个往返点差。
            float spreadPerUnit = Mathf.Abs(entry - market.ExitPriceOf(cur.factionLoadID, isLong));

            account.positions.Add(new FxPosition
            {
                currencyFactionLoadID = cur.factionLoadID,
                currencyName = cur.name,
                symbol = cur.symbol,
                isLong = isLong,
                units = units,
                leverage = leverage,
                entryRate = entry,
                markRate = market.ExitPriceOf(cur.factionLoadID, isLong),
                margin = margin,
                openFee = fee,
                spreadAtOpen = spreadPerUnit,
                // 点差率与中间价都在开仓时锁定：
                // 前者用于把 markRate 还原成 mid，后者是"行情盈亏"的基准。
                // 不锁的话行情一漂移，成本与行情的拆分就会错。
                entrySpreadRate = FxMarket.SpreadRateFor(cur),
                entryMid = cur.rate,
                openTick = GenTicks.TicksGame,
                // 绝对解锁时刻：越过一次报价即可平仓（不再随报价滚动重置）
                closableAtTick = GenTicks.TicksGame + FxMarket.TicksPerStep,
                entryHasSpread = true
            });
            Messages.Message("RimForex_MsgOpened".Translate(
                cur.name,
                isLong ? "RimForex_Long".Translate() : "RimForex_Short".Translate(),
                units,
                entry.ToString("F2"),
                fee.ToString("F2")), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>
        /// 平仓后把这个仓位的预警从队列里摘掉。
        ///
        /// ── 为什么必须有这一句 ──
        /// 预警队列 `pendingWarnings` 以 `pos.openTick` 为键，由 `MaybeWarn` 塞入。
        /// 手动平仓只是把仓位从 `account.positions` 移除，**队列里那条还在**。
        /// 而预警窗的失效判据是 `warn.position == null`（见 Window_MarginCall.cs）——
        /// `positions.Remove` 只是从列表移除，对象引用仍被 MarginWarning 自己持有，
        /// 所以判据不成立，**窗口不会自动关闭**。
        ///
        /// 更糟的是那个窗口是 `forcePause = true`：游戏被暂停，
        /// 玩家必须点掉一个"关于已经平掉的仓位"的预警才能继续玩。
        ///
        /// 队列本身是运行时对象、不进存档，所以这里只是内存清理，不影响任何账目。
        /// </summary>
        private void ClosePosition(FxPosition pos)
        {
            // 记账逻辑已下沉到 FxGameComponent.ClosePositionFromUi ——
            // 悬浮窗的"平仓"按钮走的是同一段代码，两处不会算出不同结果。
            FxGameComponent fx = Fx;
            if (fx == null) return;
            fx.ClosePositionFromUi(account, pos);
        }

        private void OpenHistory()
        {
            if (Find.WindowStack.IsOpen(typeof(Window_TradeHistory))) return;
            Find.WindowStack.Add(new Window_TradeHistory(account));
        }

        /// <summary>统计地图上可存入的白银总量（不含被禁用的）。</summary>
        private int CountMapSilver()
        {
            Map map = terminal.Map;
            if (map == null) return 0;
            List<Thing> silver = map.listerThings.ThingsOfDef(ThingDefOf.Silver);
            int total = 0;
            for (int i = 0; i < silver.Count; i++)
            {
                Thing t = silver[i];
                if (t == null || t.Destroyed || !t.Spawned) continue;
                if (t.IsForbidden(Faction.OfPlayer)) continue;
                total += t.stackCount;
            }
            return total;
        }

        private void DoDeposit(int requested)
        {
            Map map = terminal.Map;
            if (map == null) return;

            int available = CountMapSilver();
            int want = Mathf.Clamp(requested, 0, available);
            if (want <= 0)
            {
                Messages.Message("RimForex_MsgNoSilver".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<Thing> silver = map.listerThings.ThingsOfDef(ThingDefOf.Silver);
            List<Thing> toDespawn = new List<Thing>();
            int remaining = want;
            for (int i = 0; i < silver.Count && remaining > 0; i++)
            {
                Thing t = silver[i];
                if (t == null || t.Destroyed || !t.Spawned) continue;
                if (t.IsForbidden(Faction.OfPlayer)) continue;

                if (t.stackCount <= remaining)
                {
                    remaining -= t.stackCount;
                    toDespawn.Add(t);
                }
                else
                {
                    t.stackCount -= remaining;
                    remaining = 0;
                }
            }
            for (int i = 0; i < toDespawn.Count; i++) toDespawn[i].Destroy();

            int deposited = want - remaining;
            account.balance += deposited;
            // 记入"期间外部现金流"：存入是玩家自己搬进来的钱，不是交易收益。
            // 不计的话每日结算会把它报成等额的假盈利（见 FxAccount.cashFlowSinceSettle）。
            account.cashFlowSinceSettle += deposited;
            Messages.Message("RimForex_MsgDeposited".Translate(deposited), MessageTypeDefOf.PositiveEvent, false);
        }

        private void DoWithdraw(int requested)
        {
            // E7：账户冻结期间禁止提款
            if (account.frozenUntilTick > GenTicks.TicksGame)
            {
                Messages.Message("RimForex_MsgFrozen".Translate(
                    ((account.frozenUntilTick - GenTicks.TicksGame) / FxGameComponent.TicksPerDay)
                        .ToString("0")), MessageTypeDefOf.RejectInput, false);
                return;
            }

            Map map = terminal.Map;
            if (map == null) return;

            ThingDef silverDef = ThingDefOf.Silver;
            int affordable = Mathf.FloorToInt(account.balance / silverDef.BaseMarketValue);
            int amount = Mathf.Clamp(requested, 0, affordable);
            if (amount <= 0)
            {
                Messages.Message("RimForex_MsgNothingToWithdraw".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            IntVec3 dropCell = terminal.Position;
            if (!dropCell.InBounds(map) || !dropCell.Walkable(map))
            {
                dropCell = CellFinder.RandomClosewalkCellNear(terminal.Position, map, 3);
            }

            int spawned = 0;
            int remaining = amount;
            while (remaining > 0)
            {
                int chunk = Mathf.Min(remaining, silverDef.stackLimit);
                Thing thing = ThingMaker.MakeThing(silverDef);
                thing.stackCount = chunk;
                GenPlace.TryPlaceThing(thing, dropCell, map, ThingPlaceMode.Near);
                remaining -= chunk;
                spawned += chunk;
            }

            // 提取是"玩家把钱搬出去"，权益会随之下降，但那不是亏损。
            // 必须记入外部现金流，否则每日结算会报出一笔等于提取额的假亏损
            // （实测：真实 +56，报出 −1000）。详见 FxAccount.cashFlowSinceSettle。
            //
            // ── 这里【不能】乘以 Silver.BaseMarketValue（2026-10-20 改）──
            // 白银是**记账单位本身**，不是有价格的商品：原版 Core 里
            // <MarketValue>1</MarketValue> 是定义，整场游戏的经济都以白银计价。
            // 乘一个恒为 1 的系数，除了暗示"白银价格会变"之外没有任何作用，
            // 反而和"存入按数量 1:1 计"（见 DoDeposit）拼出一个假的不对称。
            // 直接用数量，让"1 白银 = 1 余额单位"在代码里显式成立。
            float taken = spawned;
            account.balance -= taken;
            account.cashFlowSinceSettle -= taken;
            // 不钳到 0：余额本来就可能因为穿仓而为负，这里只是继续扣减。
            Messages.Message("RimForex_MsgWithdrew".Translate(spawned), MessageTypeDefOf.PositiveEvent, false);
        }
    }
}
