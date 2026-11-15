using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// FX 交易终端。界面绘制在 Window_FxTerminal_NewUi.cs；
    /// 本文件只保留窗口生命周期、共享交易状态与记账入口。
    /// </summary>
    public partial class Window_FxTerminal : Window
    {
        private readonly Building_FXTradingTerminal terminal;
        private readonly FxAccount account;

        // 下单表单状态：输入保证金金额，不是数量
        private int selectedFactionId = -1;
        private int orderMargin;
        private bool marginAtMinimum;
        private float orderLeverage = 1f;
        private float orderTp;
        private float orderSl;
        private string orderTpBuf = "";
        private string orderSlBuf = "";
        private string marginBuffer = "";

        private static readonly float[] LeverageLadder =
            { 1f, 2f, 3f, 5f, 8f, 10f, 15f, 20f, 30f, 50f, 75f, 100f };

        private FxChartSpan chartSpan = FxChartSpan.Minute;
        private int chartCandles = 60;
        private const int ChartZoomMin = 10;
        private const int ChartZoomMax = 100;
        private const int ChartZoomStep = 6;

        /// <summary>视图模式：0 = 表+图，1 = 只看表，2 = 只看图。</summary>
        private int chartViewMode;

        private int holdTarget;
        private int holdDir;
        private float holdTime;
        private float holdAcc;

        /// <summary>底部留白，避免与主按钮栏 / 原版时间控制重叠。</summary>
        private const float BottomOffset = 78f;

        public override Vector2 InitialSize
        {
            get
            {
                float w = Mathf.Clamp(UI.screenWidth * 0.88f, 1100f, 1800f);
                float h = Mathf.Clamp(UI.screenHeight * 0.86f, 680f, 1000f);
                w = Mathf.Min(w, UI.screenWidth - 24f);
                h = Mathf.Min(h, UI.screenHeight - 24f);
                return new Vector2(w, h);
            }
        }

        public Window_FxTerminal(Building_FXTradingTerminal terminal, FxAccount account)
        {
            this.terminal = terminal;
            this.account = account;
            doCloseX = false;
            doCloseButton = false;
            doWindowBackground = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = true;
            onlyOneOfTypeAllowed = true;
        }

        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect.y = Mathf.Max(8f, windowRect.y - BottomOffset * 0.5f);
        }

        private FxGameComponent Fx => Find.World.GetComponent<FxGameComponent>();

        public override void DoWindowContents(Rect inRect)
        {
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

            DrawNewUiContents(inRect, fx, currencies);
        }

        private static int LadderIndexOf(float lev)
        {
            int best = 0;
            for (int i = 0; i < LeverageLadder.Length; i++)
                if (LeverageLadder[i] <= lev + 0.001f) best = i;
            return best;
        }

        private static int MaxLadderIndex(float cap)
        {
            int best = 0;
            for (int i = 0; i < LeverageLadder.Length; i++)
                if (LeverageLadder[i] <= cap + 0.001f) best = i;
            return best;
        }

        private void HoldInt(Rect minus, Rect plus, int target,
            System.Action<int, int> apply)
        {
            HoldCommon(minus, plus, target, (dir, t01) =>
            {
                // t01 == 0 表示"刚按下"那一次：只走 1
                // 之后按 5 → 10 → 50 → 200 逐档加速（阈值放宽，手感更可控）
                int step = t01 <= 0f ? 1
                    : t01 > 6f ? 200
                    : t01 > 4f ? 50
                    : t01 > 2f ? 10 : 5;
                apply(dir, step);
            });
        }

        private void HoldFloat(Rect minus, Rect plus, int target,
            System.Action<int, float> apply)
        {
            HoldCommon(minus, plus, target, (dir, t01) =>
            {
                // t01 == 0 表示"刚按下"那一次：只走 0.01
                float step = t01 <= 0f ? 0.01f
                    : t01 > 6f ? 20f
                    : t01 > 4f ? 5f
                    : t01 > 2f ? 0.5f : 0.05f;
                apply(dir, step);
            });
        }

        private void HoldCommon(Rect minus, Rect plus, int target,
            System.Action<int, float> apply)
        {
            Event e = Event.current;
            if (e == null) return;
            Vector2 m = e.mousePosition;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (minus.Contains(m))
                {
                    holdTarget = target; holdDir = -1; holdTime = 0f; holdAcc = 0f;
                    apply(-1, 0f);   // 短按那一下
                    e.Use();
                }
                else if (plus.Contains(m))
                {
                    holdTarget = target; holdDir = 1; holdTime = 0f; holdAcc = 0f;
                    apply(1, 0f);
                    e.Use();
                }
                else
                {
                    // 按下别处就取消，避免"松手事件丢了"之后一直连发
                    holdTarget = 0;
                }
                return;
            }

            if (e.type == EventType.MouseUp)
            {
                if (holdTarget == target) holdTarget = 0;
                return;
            }

            if (holdTarget != target || holdDir == 0) return;

            holdTime += UnityEngine.Time.unscaledDeltaTime;
            // 没有 InputLegacyModule 可引用，拿不到"鼠标是否仍按住"。
            // 用 30 秒硬上限兜底（原来 6 秒，作者反馈"按到 8600 就停"就是它）；
            // 正常松手会收到 MouseUp 立即结束。
            if (holdTime > 30f) { holdTarget = 0; return; }
            if (holdTime < 0.30f) return;          // 短按只算那一次

            holdAcc += UnityEngine.Time.unscaledDeltaTime;
            float interval = holdTime > 6f ? 0.04f
                : holdTime > 4f ? 0.05f
                : holdTime > 2f ? 0.08f : 0.12f;
            if (holdAcc < interval) return;
            holdAcc = 0f;
            apply(holdDir, holdTime);
        }

        private static string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "…";
        }

        private string SpanPeriodText()
        {
            if (chartSpan == FxChartSpan.Day) return "RimForex_SpanUnitDay".Translate();
            if (chartSpan == FxChartSpan.Hour) return "RimForex_SpanUnitHour".Translate();

            // 分K：长度由「每根K线由几次报价合成」决定，换算成游戏分钟
            float mins = FxMarket.PeriodTicksOf(FxChartSpan.Minute) / (FxGameComponent.TicksPerHour / 60f);
            return "RimForex_SpanUnitMinute".Translate(mins.ToString("0.#"));
        }

        private static float MinMarginFor(float ask, float leverage)
        {
            if (ask <= 0.01f || leverage <= 0f) return 0f;
            float perUnitNotional = ask;
            return perUnitNotional / leverage + FxGameComponent.FeeFor(perUnitNotional);
        }

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

        private static bool Affordable(int units, float ask, float leverage, float margin)
        {
            if (units <= 0) return true;
            float m, n, f;
            int real;
            ComputeEntry(units, ask, leverage, out real, out m, out n, out f);
            return m + f <= margin + 0.0001f;
        }

        private static void ComputeEntry(int wantUnits, float ask, float leverage,
            out int units, out float margin, out float notional, out float fee)
        {
            units = wantUnits > 0 ? wantUnits : 0;
            notional = units * ask;
            margin = leverage > 0f ? notional / leverage : notional;
            fee = FxGameComponent.FeeFor(notional);
        }

        private static void ComputeEntry(int wantUnits, float ask, float leverage, out float margin)
        {
            int u;
            float n, f;
            ComputeEntry(wantUnits, ask, leverage, out u, out margin, out n, out f);
        }

        private static float ParsePrice(string buf)
        {
            if (buf == null) return 0f;
            buf = buf.Trim();
            if (buf.Length == 0) return 0f;
            float v;
            if (!float.TryParse(buf, out v)) return 0f;
            return v > 0f ? v : 0f;
        }

        private static float PctVs(FxCurrency cur, float price, bool asLong)
        {
            if (cur == null || price <= 0f) return 0f;
            float mid = cur.rate;
            if (mid <= 0.001f) return 0f;
            return (price / mid - 1f) * 100f;
        }

        private bool TpSlDirectionBad(FxCurrency cur, bool isLong, float tp, float sl)
        {
            if (cur == null) return false;
            float entry = Fx.market.EntryPriceOf(cur.factionLoadID, isLong);
            if (entry <= 0.001f) return false;

            bool badTp = tp > 0f && (isLong ? tp <= entry : tp >= entry);
            bool badSl = sl > 0f && (isLong ? sl >= entry : sl <= entry);
            return badTp || badSl;
        }

        private void OpenPosition(FxCurrency cur, bool isLong, int units, float leverage,
            float tp = 0f, float sl = 0f)
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

            if (Fx != null && Fx.CurrencyHalted(cur))
            {
                Messages.Message("RimForex_MsgTradeHalted".Translate(cur.PairLabel),
                    MessageTypeDefOf.RejectInput, false);
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
                entryHasSpread = true,
                // 止盈止损只在方向合理时收下：做多的止盈必须在入场价之上、止损之下，做空反过来
                tpRate = isLong ? (tp > entry ? tp : 0f) : (tp > 0f && tp < entry ? tp : 0f),
                slRate = isLong ? (sl > 0f && sl < entry ? sl : 0f) : (sl > entry ? sl : 0f)
            });
            Messages.Message("RimForex_MsgOpened".Translate(
                cur.name,
                isLong ? "RimForex_Long".Translate() : "RimForex_Short".Translate(),
                units,
                entry.ToString("F2"),
                fee.ToString("F2")), MessageTypeDefOf.NeutralEvent, false);
        }

        private void ClosePosition(FxPosition pos)
        {
            // 记账逻辑已下沉到 FxGameComponent.ClosePositionFromUi ——
            // 悬浮窗的"平仓"按钮走的是同一段代码，两处不会算出不同结果。
            FxGameComponent fx = Fx;
            if (fx == null) return;
            fx.ClosePositionFromUi(account, pos);
        }

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
            // 存入也是余额变动，交易历史要留痕；正数表示钱进了账户。
            account.RecordTrade(new FxTradeRecord
            {
                isFee = true,
                isCashFlow = true,
                feeLabel = "RimForex_FeeDeposit".Translate(),
                currencyName = "RimForex_FeeDeposit".Translate(),
                pairLabel = "RimForex_FeeDeposit".Translate(),
                units = 0,
                pnl = deposited,
                closedTick = GenTicks.TicksGame
            });
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
            account.RecordTrade(new FxTradeRecord
            {
                isFee = true,
                isCashFlow = true,
                feeLabel = "RimForex_FeeWithdraw".Translate(),
                currencyName = "RimForex_FeeWithdraw".Translate(),
                pairLabel = "RimForex_FeeWithdraw".Translate(),
                units = 0,
                pnl = -taken,
                closedTick = GenTicks.TicksGame
            });
            // 不钳到 0：余额本来就可能因为穿仓而为负，这里只是继续扣减。
            Messages.Message("RimForex_MsgWithdrew".Translate(spawned), MessageTypeDefOf.PositiveEvent, false);
        }
    }
}
