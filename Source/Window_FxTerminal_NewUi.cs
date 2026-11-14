using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 0.3.3 新版终端界面（阶段 A/B）。
    ///
    /// 与旧界面共用同一个 Window 类和全部交易逻辑，只替换绘制方式。
    /// 旧界面保留到迁移完成，通过设置里的开发开关切换。
    /// </summary>
    public partial class Window_FxTerminal
    {
        private const float UiTitleH = 44f;
        private const float UiAccountH = 58f;

        private float uiLeftRatio = 0.24f;
        private float uiRightRatio = 0.22f;
        private float uiBottomRatio = 0.20f;
        /// <summary>正在拖的分隔条：0 无，1 左，2 右，3 底。</summary>
        private int uiDragSplit;
        private int uiBottomTab;
        private float uiBottomScroll;

        private void ResetUiLayout()
        {
            uiLeftRatio = 0.24f;
            uiRightRatio = 0.22f;
            uiBottomRatio = 0.20f;
            uiDragSplit = 0;
        }

        private void DrawNewUiContents(Rect inRect, FxGameComponent fx,
            List<FxCurrency> currencies)
        {
            FxTheme.Fill(inRect, FxTheme.WindowBg);
            FxTheme.Border(inRect, FxTheme.Line);

            Rect title = new Rect(inRect.x, inRect.y, inRect.width, UiTitleH);
            DrawNewTitleBar(title, fx);

            Rect content = new Rect(inRect.x + 1f, title.yMax,
                inRect.width - 2f, inRect.height - UiTitleH - 1f);

            Rect accountRect = new Rect(content.x, content.y, content.width, UiAccountH);
            DrawNewAccountStrip(accountRect, fx);

            float maxBottom = content.height - UiAccountH - 260f;
            float bottomH = Mathf.Clamp(content.height * uiBottomRatio, 120f, Mathf.Max(120f, maxBottom));
            Rect bottomRect = new Rect(content.x, content.yMax - bottomH,
                content.width, bottomH);

            float mainY = accountRect.yMax + 8f;
            float mainH = bottomRect.y - 8f - mainY;
            Rect mainRect = new Rect(content.x, mainY, content.width, Mathf.Max(180f, mainH));

            float leftW = Mathf.Clamp(mainRect.width * uiLeftRatio, 300f,
                mainRect.width - 600f);
            float rightW = Mathf.Clamp(mainRect.width * uiRightRatio, 300f,
                mainRect.width - leftW - 320f);
            float centerW = mainRect.width - leftW - rightW - 16f;

            Rect leftRect = new Rect(mainRect.x, mainRect.y, leftW, mainRect.height);
            Rect centerRect = new Rect(leftRect.xMax + 8f, mainRect.y, centerW, mainRect.height);
            Rect rightRect = new Rect(centerRect.xMax + 8f, mainRect.y, rightW, mainRect.height);

            DrawNewMarket(leftRect, currencies);
            DrawNewChart(centerRect, fx);
            DrawNewOrder(rightRect, fx);
            DrawNewBottom(bottomRect, fx);

            // ── 两根竖向分隔条 ──
            Rect leftHit = new Rect(leftRect.xMax, mainRect.y, 8f, mainRect.height);
            Rect rightHit = new Rect(centerRect.xMax, mainRect.y, 8f, mainRect.height);
            HandleUiSplitterV(leftHit, 1, ref uiLeftRatio, mainRect.width, false);
            HandleUiSplitterV(rightHit, 2, ref uiRightRatio, mainRect.width, true);

            // ── 横向分隔条 ──
            Rect bottomHit = new Rect(content.x, bottomRect.y - 8f, content.width, 8f);
            HandleUiSplitterH(bottomHit, 3, content.height);

            FxWidgets.SplitterV(leftHit,
                new Rect(leftHit.center.x, leftHit.y + 6f, 1f, leftHit.height - 12f),
                Mouse.IsOver(leftHit) || uiDragSplit == 1);
            FxWidgets.SplitterV(rightHit,
                new Rect(rightHit.center.x, rightHit.y + 6f, 1f, rightHit.height - 12f),
                Mouse.IsOver(rightHit) || uiDragSplit == 2);
            FxWidgets.SplitterH(bottomHit,
                new Rect(bottomHit.x + 16f, bottomHit.center.y, bottomHit.width - 32f, 1f),
                Mouse.IsOver(bottomHit) || uiDragSplit == 3);
        }

        private void HandleUiSplitterV(Rect hit, int id, ref float ratio,
            float totalWidth, bool invert)
        {
            Event e = Event.current;
            if (e == null || totalWidth < 1f) return;

            if (e.type == EventType.MouseDown && Mouse.IsOver(hit))
            {
                uiDragSplit = id;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && uiDragSplit == id)
            {
                float delta = e.delta.x / totalWidth;
                ratio = Mathf.Clamp(ratio + (invert ? -delta : delta), 0.16f, 0.42f);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && uiDragSplit == id)
            {
                uiDragSplit = 0;
            }
        }

        private void HandleUiSplitterH(Rect hit, int id, float totalHeight)
        {
            Event e = Event.current;
            if (e == null || totalHeight < 1f) return;

            if (e.type == EventType.MouseDown && Mouse.IsOver(hit))
            {
                uiDragSplit = id;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && uiDragSplit == id)
            {
                float delta = e.delta.y / totalHeight;
                uiBottomRatio = Mathf.Clamp(uiBottomRatio - delta, 0.10f, 0.42f);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && uiDragSplit == id)
            {
                uiDragSplit = 0;
            }
        }

        private void DrawNewTitleBar(Rect bar, FxGameComponent fx)
        {
            FxTheme.HLine(bar.x, bar.xMax, bar.yMax - 1f, FxTheme.Line);

            FxTheme.Label(new Rect(bar.x + 16f, bar.y + 10f, 200f, 26f),
                "FX TERMINAL", GameFont.Medium, FxTheme.TextColor,
                TextAnchor.UpperLeft);

            FxTheme.Tiny(new Rect(bar.x + 190f, bar.y + 16f, 160f, 18f),
                "RIMFOREX  0.3.3", FxTheme.Muted);

            if (bar.width > 1000f)
            {
                int dateAbs = GenDate.TickGameToAbs(GenTicks.TicksGame);
                string date = GenDate.DateFullStringAt(dateAbs, Vector2.zero);
                FxTheme.Tiny(new Rect(bar.xMax - 760f, bar.y + 16f, 300f, 18f),
                    date, FxTheme.Muted, TextAnchor.MiddleRight);
            }

            float right = bar.xMax - 12f;
            const float bh = 26f;
            float by = bar.y + 9f;

            Rect close = new Rect(right - 28f, by, 28f, bh);
            if (FxWidgets.Button(close, "×", true, false, true))
            {
                Close();
                return;
            }
            right -= 34f;

            Rect settings = new Rect(right - 44f, by, 44f, bh);
            if (FxWidgets.Button(settings, "RimForex_UiSettings".Translate()))
            {
                if (RimForexMod.Instance != null)
                {
                    Find.WindowStack.Add(new Dialog_ModSettings(RimForexMod.Instance));
                }
            }
            right -= 50f;

            Rect reset = new Rect(right - 68f, by, 68f, bh);
            if (FxWidgets.Button(reset, "RimForex_UiResetLayout".Translate()))
            {
                ResetUiLayout();
            }
            right -= 74f;

            Rect guide = new Rect(right - 44f, by, 44f, bh);
            if (FxWidgets.Button(guide, "RimForex_UiGuide".Translate()))
            {
                if (!Find.WindowStack.IsOpen(typeof(Window_FxGuide)))
                {
                    Find.WindowStack.Add(new Window_FxGuide());
                }
            }
            right -= 50f;

            Rect loans = new Rect(right - 44f, by, 44f, bh);
            if (FxWidgets.Button(loans, "RimForex_UiLoans".Translate()))
            {
                if (Find.WindowStack.IsOpen(typeof(Window_FxLoans)))
                {
                    Find.WindowStack.TryRemove(typeof(Window_FxLoans), true);
                }
                else
                {
                    FxAccount acc = ColonyAccountForUi();
                    if (acc != null && fx != null)
                    {
                        Find.WindowStack.Add(new Window_FxLoans(fx, acc, terminal));
                    }
                }
            }
        }

        private FxAccount ColonyAccountForUi()
        {
            FxGameComponent fx = Fx;
            if (fx == null) return null;
            return fx.ColonyAccount(Find.AnyPlayerHomeMap);
        }

        private void DrawNewAccountStrip(Rect r, FxGameComponent fx)
        {
            FxWidgets.Panel(r);
            if (account == null) return;

            float pad = 18f;
            const float btnW = 58f;
            const float btnGap = 6f;
            float buttonsW = btnW * 2f + btnGap + 12f;
            float colW = (r.width - pad * 2f - buttonsW) / 5f;
            string[] labels = { "RimForex_Equity".Translate(), "RimForex_FreeMargin".Translate(),
                "RimForex_UsedMargin".Translate(), "RimForex_FloatingPnl".Translate(),
                "RimForex_UiPositionsCount".Translate() };
            float[] values = { account.Equity, account.balance, account.MarginUsed,
                account.FloatingPnl, account.positions != null ? account.positions.Count : 0 };

            for (int i = 0; i < labels.Length; i++)
            {
                float x = r.x + pad + i * colW;
                FxTheme.Tiny(new Rect(x, r.y + 9f, colW - 8f, 16f), labels[i], FxTheme.Muted);
                Color c = i == 3 ? FxTheme.PnlColor(values[i]) : FxTheme.TextColor;
                FxTheme.Label(new Rect(x, r.y + 27f, colW - 8f, 26f),
                    i == 4 ? values[i].ToString("0") : values[i].ToString("F0"),
                    GameFont.Medium, c, TextAnchor.UpperLeft);
            }
            float bx = r.xMax - pad - btnW;
            if (FxWidgets.Button(new Rect(bx, r.y + 14f, btnW, 28f),
                    "RimForex_UiDeposit".Translate(), true, true))
            {
                OpenCashTransfer(true);
            }
            if (FxWidgets.Button(new Rect(bx - btnW - btnGap, r.y + 14f, btnW, 28f),
                    "RimForex_UiWithdraw".Translate()))
            {
                OpenCashTransfer(false);
            }
            FxTheme.HLine(r.x, r.xMax, r.yMax - 1f, FxTheme.Line);
        }

        private void OpenCashTransfer(bool deposit)
        {
            if (Find.WindowStack.IsOpen(typeof(Window_FxCashTransfer))) return;
            Find.WindowStack.Add(new Window_FxCashTransfer(this, deposit));
        }

        public FxAccount AccountForUi
        {
            get { return account; }
        }

        public int CountMapSilverForUi()
        {
            return CountMapSilver();
        }

        public void DepositSilverForUi(int amount)
        {
            DoDeposit(amount);
        }

        public void WithdrawSilverForUi(int amount)
        {
            DoWithdraw(amount);
        }

        private void DrawNewMarket(Rect r, List<FxCurrency> currencies)
        {
            FxWidgets.Panel(r);
            FxWidgets.SectionHeader(r, "RimForex_UiMarket".Translate());

            float headY = r.y + 34f;
            float w = r.width - 20f;
            float x = r.x + 10f;
            FxTheme.Tiny(new Rect(x, headY, w * 0.56f, 16f), "RimForex_UiColPair".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(x + w * 0.56f, headY, w * 0.16f, 16f), "RimForex_UiColRate".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(x + w * 0.74f, headY, w * 0.14f, 16f), "RimForex_UiColChange".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(x + w * 0.88f, headY, w * 0.12f, 16f), "RimForex_UiColVol".Translate(), FxTheme.Muted);
            FxTheme.HLine(r.x, r.xMax, headY + 18f, FxTheme.Line);

            float rowH = 34f;
            float y = headY + 22f;
            for (int i = 0; i < currencies.Count; i++)
            {
                FxCurrency c = currencies[i];
                if (c == null) continue;
                if (y + rowH > r.yMax - 4f) break;

                Rect row = new Rect(x - 4f, y, w + 8f, rowH);
                bool selected = c.factionLoadID == selectedFactionId;
                if (selected)
                {
                    FxTheme.Fill(row, FxTheme.PanelSel);
                    FxTheme.VLine(row.x, row.y, row.yMax, FxTheme.Accent);
                }
                else if (Mouse.IsOver(row))
                {
                    FxTheme.Fill(row, new Color32(24, 30, 38, 255));
                }

                if (Widgets.ButtonInvisible(row))
                {
                    selectedFactionId = c.factionLoadID;
                }

                string status = c.haltUntilTick > GenTicks.TicksGame ? "停牌"
                    : c.pegUntilTick > GenTicks.TicksGame ? "钉住" : "";
                FxTheme.Small(new Rect(x + 6f, y + 2f, w * 0.54f, 18f),
                    Truncate(c.PairLabel, 18), FxTheme.TextColor);
                Color statusColor = c.haltUntilTick > GenTicks.TicksGame
                    ? FxTheme.Warning
                    : c.pegUntilTick > GenTicks.TicksGame ? FxTheme.Accent : FxTheme.Muted;
                FxTheme.Tiny(new Rect(x + 6f, y + 18f, w * 0.54f, 14f),
                    c.factionName + (status.Length > 0 ? "  " + status : ""),
                    statusColor);

                float pct = c.previousRate > 0.01f
                    ? (c.rate / c.previousRate - 1f) * 100f : 0f;
                FxTheme.Small(new Rect(x + w * 0.56f, y + 8f, w * 0.17f, 18f),
                    c.rate.ToString("F2"), FxTheme.TextColor, TextAnchor.MiddleLeft);
                FxTheme.Small(new Rect(x + w * 0.74f, y + 8f, w * 0.14f, 18f),
                    pct.ToString("+0.0;-0.0;0.0") + "%",
                    pct >= 0f ? FxTheme.Long : FxTheme.Short, TextAnchor.MiddleRight);
                FxTheme.Small(new Rect(x + w * 0.88f, y + 8f, w * 0.12f, 18f),
                    "×" + c.volatility.ToString("0.0"), FxTheme.Muted, TextAnchor.MiddleRight);

                y += rowH;
                FxTheme.HLine(r.x + 8f, r.xMax - 8f, y, FxTheme.Line);
            }
        }

        private void DrawNewChart(Rect r, FxGameComponent fx)
        {
            FxWidgets.Panel(r);

            float tabY = r.y + 4f;
            float tx = r.x + 10f;
            string[] names = { "RimForex_SpanMinuteShort".Translate(),
                "RimForex_SpanHour".Translate(), "RimForex_SpanDay".Translate() };
            FxChartSpan[] spans = { FxChartSpan.Minute, FxChartSpan.Hour, FxChartSpan.Day };
            for (int i = 0; i < names.Length; i++)
            {
                Rect tab = new Rect(tx, tabY, 50f, 26f);
                if (FxWidgets.Tab(tab, names[i], chartSpan == spans[i]))
                {
                    chartSpan = spans[i];
                }
                tx += 52f;
            }

            Rect plot = new Rect(r.x + 10f, r.y + 36f,
                r.width - 76f, r.height - 68f);
            if (plot.height < 80f) return;
            if (selectedFactionId < 0)
            {
                FxWidgets.Center(plot, "RimForex_UiSelectCurrency".Translate(), FxTheme.Muted);
                return;
            }

            Event wheel = Event.current;
            if (Mouse.IsOver(plot) && wheel.type == EventType.ScrollWheel)
            {
                chartCandles = Mathf.Clamp(chartCandles
                    - Mathf.RoundToInt(Mathf.Sign(wheel.delta.y)) * ChartZoomStep,
                    ChartZoomMin, ChartZoomMax);
                wheel.Use();
            }

            List<FxCandle> view = fx.market.GetCandles(selectedFactionId, chartCandles, chartSpan);
            if (view == null || view.Count == 0)
            {
                FxWidgets.Center(plot, "RimForex_UiNoData".Translate(), FxTheme.Muted);
                return;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < view.Count; i++)
            {
                if (view[i].low < min) min = view[i].low;
                if (view[i].high > max) max = view[i].high;
            }
            if (max - min < 0.0001f) { max += 0.5f; min -= 0.5f; }
            float pad = (max - min) * 0.1f;
            min -= pad;
            max += pad;

            Color grid = new Color32(27, 33, 41, 255);
            for (int i = 0; i <= 4; i++)
            {
                float gy = plot.y + plot.height * i / 4f;
                FxTheme.HLine(plot.x, plot.xMax, gy, grid);
                FxTheme.Tiny(new Rect(plot.xMax + 6f, gy - 7f, 60f, 14f),
                    (max - (max - min) * i / 4f).ToString("F2"), FxTheme.Muted);
            }
            for (int i = 0; i <= 5; i++)
            {
                float gx = plot.x + plot.width * i / 5f;
                FxTheme.VLine(gx, plot.y, plot.yMax, grid);
            }

            float slot = plot.width / view.Count;
            float bw = Mathf.Max(2f, slot * 0.55f);
            for (int i = 0; i < view.Count; i++)
            {
                FxCandle c = view[i];
                float cx = plot.x + slot * (i + 0.5f);
                float oy = plot.y + (max - c.open) / (max - min) * plot.height;
                float cy = plot.y + (max - c.close) / (max - min) * plot.height;
                float hy = plot.y + (max - c.high) / (max - min) * plot.height;
                float ly = plot.y + (max - c.low) / (max - min) * plot.height;
                Color col = c.close >= c.open ? FxTheme.Long : FxTheme.Short;
                FxTheme.VLine(cx, hy, ly, col);
                float top = Mathf.Min(oy, cy);
                float h = Mathf.Max(1f, Mathf.Abs(oy - cy));
                FxTheme.Fill(new Rect(cx - bw / 2f, top, bw, h), col);
            }

            // 当前货币的持仓开仓线
            if (account != null && account.positions != null)
            {
                for (int i = 0; i < account.positions.Count; i++)
                {
                    FxPosition p = account.positions[i];
                    if (p == null || p.currencyFactionLoadID != selectedFactionId) continue;
                    if (p.entryRate < min || p.entryRate > max) continue;
                    float py = plot.y + (max - p.entryRate) / (max - min) * plot.height;
                    Color lineColor = p.isLong
                        ? new Color(FxTheme.Long.r, FxTheme.Long.g, FxTheme.Long.b, 0.55f)
                        : new Color(FxTheme.Short.r, FxTheme.Short.g, FxTheme.Short.b, 0.55f);
                    FxTheme.HLine(plot.x, plot.xMax, py, lineColor);
                    FxTheme.Tiny(new Rect(plot.x + 4f, py - 14f, 70f, 12f),
                        "RimForex_UiEntryLine".Translate(), lineColor);
                }
            }

            float last = view[view.Count - 1].close;
            float lastY = plot.y + (max - last) / (max - min) * plot.height;
            FxTheme.HLine(plot.x, plot.xMax, lastY, FxTheme.Warning);
            Rect tag = new Rect(plot.xMax + 4f, lastY - 9f, 58f, 18f);
            FxTheme.Fill(tag, FxTheme.Warning);
            FxTheme.Tiny(tag, last.ToString("F2"), new Color32(20, 20, 20, 255),
                TextAnchor.MiddleCenter);

            // 悬停：竖线 + 该根的开高低收
            if (Mouse.IsOver(plot) && view.Count > 0)
            {
                float mx = Event.current.mousePosition.x;
                int hi = Mathf.Clamp((int)((mx - plot.x) / slot), 0, view.Count - 1);
                float hx = plot.x + slot * (hi + 0.5f);
                FxTheme.VLine(hx, plot.y, plot.yMax, FxTheme.Muted);

                FxCandle hc = view[hi];
                string detail = "O " + hc.open.ToString("F2")
                    + "  H " + hc.high.ToString("F2")
                    + "  L " + hc.low.ToString("F2")
                    + "  C " + hc.close.ToString("F2");
                float tw = Text.CalcSize(detail).x + 16f;
                float txx = Mathf.Clamp(mx + 12f, plot.x, plot.xMax - tw);
                Rect tip = new Rect(txx, plot.y + 6f, tw, 24f);
                FxTheme.Fill(tip, new Color32(20, 26, 34, 245));
                FxTheme.Border(tip, FxTheme.LineHi);
                FxTheme.Tiny(new Rect(tip.x + 6f, tip.y + 4f, tip.width - 12f, 16f),
                    detail, FxTheme.TextColor);
            }

            int dateAbs = GenDate.TickGameToAbs(GenTicks.TicksGame);
            FxTheme.Tiny(new Rect(plot.x, plot.yMax + 4f, 240f, 16f),
                GenDate.DateFullStringAt(dateAbs, Vector2.zero), FxTheme.Muted);
        }

        private void DrawNewOrder(Rect r, FxGameComponent fx)
        {
            FxWidgets.Panel(r);
            float x = r.x + 12f;
            float w = r.width - 24f;
            float y = r.y + 10f;

            FxCurrency sel = fx.market.Get(selectedFactionId);
            if (sel == null)
            {
                FxWidgets.Center(new Rect(x, r.y + 40f, w, 30f),
                    "RimForex_NoSelection".Translate(), FxTheme.Muted);
                return;
            }

            bool halted = fx.CurrencyHalted(sel);
            bool pegged = sel.pegUntilTick > GenTicks.TicksGame;
            string status = halted ? "RimForex_UiStatusHalt".Translate()
                : pegged ? "RimForex_UiStatusPeg".Translate() : "";
            FxTheme.Small(new Rect(x, y, w - 76f, 20f),
                Truncate(sel.PairLabel, 18), FxTheme.TextColor);
            if (status.Length > 0)
            {
                FxTheme.Tiny(new Rect(x + w - 76f, y + 2f, 76f, 16f), status,
                    halted ? FxTheme.Warning : FxTheme.Accent, TextAnchor.MiddleRight);
            }
            y += 26f;

            bool compact = r.height < 430f;
            float ask = FxMarket.AskOf(sel);
            float bid = FxMarket.BidOf(sel);
            float spreadPct = FxMarket.SpreadRateFor(sel) * 100f;
            if (compact)
            {
                FxTheme.Tiny(new Rect(x, y, w, 20f),
                    "RimForex_UiQuoteLine".Translate(bid.ToString("F2"),
                        ask.ToString("F2"), spreadPct.ToString("0.###")),
                    FxTheme.Muted);
                y += 26f;
            }
            else
            {
                float cell = (w - 8f) / 3f;
                DrawQuoteCell(new Rect(x, y, cell, 42f), "RimForex_UiBid".Translate(),
                    bid.ToString("F2"), FxTheme.Long);
                DrawQuoteCell(new Rect(x + cell + 4f, y, cell, 42f), "RimForex_UiAsk".Translate(),
                    ask.ToString("F2"), FxTheme.Short);
                DrawQuoteCell(new Rect(x + (cell + 4f) * 2f, y, cell, 42f),
                    "RimForex_UiSpread".Translate(), spreadPct.ToString("0.###") + "%",
                    FxTheme.Muted);
                y += 54f;
            }

            // ── 保证金 ──
            FxTheme.Tiny(new Rect(x, y, w, 16f), "RimForex_MarginInput".Translate(),
                FxTheme.Muted);
            y += 18f;
            Rect marginRect = new Rect(x, y, w, 28f);
            marginBuffer = FxWidgets.TextField(marginRect, marginBuffer, 14, "fx-order-margin");
            int parsed;
            if (!int.TryParse(marginBuffer, out parsed)) parsed = 0;
            orderMargin = Mathf.Max(0, parsed);

            float minMargin = MinMarginFor(ask, orderLeverage);
            bool focused = GUI.GetNameOfFocusedControl() == "fx-order-margin";
            if (!focused && orderMargin < minMargin)
            {
                orderMargin = Mathf.CeilToInt(minMargin);
                marginBuffer = orderMargin.ToString();
            }
            y += 34f;

            float half = (w - 6f) / 2f;
            if (FxWidgets.Button(new Rect(x, y, half, 24f), "RimForex_Zero".Translate()))
            {
                orderMargin = Mathf.CeilToInt(minMargin);
                marginBuffer = orderMargin.ToString();
                marginAtMinimum = true;
            }
            if (FxWidgets.Button(new Rect(x + half + 6f, y, half, 24f), "RimForex_AllIn".Translate()))
            {
                int maxUnits = FxGameComponent.MaxAffordableUnits(account.balance, ask, orderLeverage);
                float m;
                ComputeEntry(maxUnits, ask, orderLeverage, out m);
                float maxFee = FxGameComponent.FeeFor(maxUnits * ask);
                orderMargin = Mathf.Max(Mathf.CeilToInt(minMargin),
                    Mathf.CeilToInt(m + maxFee));
                marginBuffer = orderMargin.ToString();
                marginAtMinimum = false;
            }
            y += 32f;

            // ── 杠杆 ──
            float maxSafe = FxGameComponent.MaxSafeLeverageFor(sel);
            int maxIdx = MaxLadderIndex(maxSafe);
            int curIdx = Mathf.Clamp(LadderIndexOf(orderLeverage), 0, Mathf.Max(0, maxIdx));
            orderLeverage = LeverageLadder[curIdx];

            FxTheme.Tiny(new Rect(x, y, w - 60f, 16f), "RimForex_Leverage".Translate(),
                FxTheme.Muted);
            FxTheme.Small(new Rect(x + w - 60f, y, 60f, 16f),
                orderLeverage.ToString("F0") + "x", FxTheme.TextColor,
                TextAnchor.MiddleRight);
            y += 18f;

            int newIdx = DrawLeverageSlider(new Rect(x, y, w, 18f), curIdx,
                Mathf.Max(0, maxIdx));
            if (newIdx != curIdx)
            {
                orderLeverage = LeverageLadder[newIdx];
                float newMin = MinMarginFor(ask, orderLeverage);
                if (marginAtMinimum || orderMargin < newMin)
                {
                    orderMargin = Mathf.CeilToInt(newMin);
                    marginBuffer = orderMargin.ToString();
                }
            }
            y += 26f;

            // ── 止盈止损 ──
            FxTheme.Tiny(new Rect(x, y, half, 16f), "RimForex_TpShort".Translate(),
                FxTheme.Muted);
            FxTheme.Tiny(new Rect(x + half + 6f, y, half, 16f), "RimForex_SlShort".Translate(),
                FxTheme.Muted);
            y += 18f;
            orderTpBuf = FxWidgets.TextField(new Rect(x, y, half, 26f), orderTpBuf, 10,
                "fx-order-tp");
            orderSlBuf = FxWidgets.TextField(new Rect(x + half + 6f, y, half, 26f),
                orderSlBuf, 10, "fx-order-sl");
            orderTp = ParsePrice(orderTpBuf);
            orderSl = ParsePrice(orderSlBuf);
            y += 34f;

            // ── 费用 ──
            int wantUnits = EstimateUnits(orderMargin, ask, orderLeverage);
            int units;
            float actualMargin, notional, fee;
            ComputeEntry(wantUnits, ask, orderLeverage, out units,
                out actualMargin, out notional, out fee);
            float total = actualMargin + fee;
            bool enough = total <= account.balance;

            if (compact)
            {
                FxTheme.Tiny(new Rect(x, y, w, 16f),
                    "RimForex_UiCompactLine".Translate(units.ToString("N0"),
                        total.ToString("F0"), account.balance.ToString("F0")),
                    enough ? FxTheme.Long : FxTheme.Short);
                y += 22f;
            }
            else
            {
                FxTheme.Tiny(new Rect(x, y, w, 16f),
                    "RimForex_LineUnits".Translate(units.ToString("N0")), FxTheme.Muted);
                y += 17f;
                FxTheme.Tiny(new Rect(x, y, w, 16f),
                    "RimForex_LineNotional".Translate(notional.ToString("F0"),
                        orderLeverage.ToString("F0")), FxTheme.Muted);
                y += 17f;
                FxTheme.Tiny(new Rect(x, y, w, 16f),
                    "RimForex_UiFeeLine".Translate(actualMargin.ToString("F0"),
                        fee.ToString("F2")), FxTheme.Muted);
                y += 17f;
                FxTheme.Tiny(new Rect(x, y, w, 16f),
                    "RimForex_LineTotal".Translate(total.ToString("F0"),
                        account.balance.ToString("F0")),
                    enough ? FxTheme.Long : FxTheme.Short);
                y += 22f;
            }

            if (halted)
            {
                FxTheme.Tiny(new Rect(x, y, w, 18f),
                    "RimForex_MsgTradeHalted".Translate(sel.PairLabel), FxTheme.Warning);
            }
            else if (!enough)
            {
                FxTheme.Tiny(new Rect(x, y, w, 18f),
                    "RimForex_NotEnoughMargin".Translate(), FxTheme.Short);
            }

            // ── 下单按钮 ──
            float by = r.yMax - 40f;
            bool canOpen = units > 0 && enough && !halted;
            if (FxWidgets.Button(new Rect(x, by, half, 32f),
                    "RimForex_GoLong".Translate(), canOpen, true))
            {
                TryOpenFromUi(sel, true, units);
            }
            if (FxWidgets.Button(new Rect(x + half + 6f, by, half, 32f),
                    "RimForex_GoShort".Translate(), canOpen, false, true))
            {
                TryOpenFromUi(sel, false, units);
            }
        }

        private void TryOpenFromUi(FxCurrency sel, bool isLong, int units)
        {
            if (TpSlDirectionBad(sel, isLong, orderTp, orderSl))
            {
                Messages.Message("RimForex_MsgTpSlReversed".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            OpenPosition(sel, isLong, units, orderLeverage, orderTp, orderSl);
        }

        private static void DrawQuoteCell(Rect r, string label, string value, Color valueColor)
        {
            FxTheme.Fill(r, new Color32(17, 21, 27, 255));
            FxTheme.Border(r, FxTheme.Line);
            FxTheme.Tiny(new Rect(r.x + 8f, r.y + 4f, r.width - 16f, 14f),
                label, FxTheme.Muted);
            FxTheme.Small(new Rect(r.x + 8f, r.y + 19f, r.width - 16f, 18f),
                value, valueColor);
        }

        /// <summary>返回新的档位索引。点或拖都按最近档位吸附。</summary>
        private static int DrawLeverageSlider(Rect r, int curIdx, int maxIdx)
        {
            int result = curIdx;
            Event e = Event.current;
            if (maxIdx <= 0)
            {
                FxTheme.VLine(r.x, r.center.y, r.xMax, FxTheme.LineHi);
                FxTheme.Fill(new Rect(r.x - 3f, r.center.y - 3f, 7f, 7f), FxTheme.Accent);
                return 0;
            }

            if (Mouse.IsOver(r) && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
            {
                float t = Mathf.Clamp01((e.mousePosition.x - r.x) / r.width);
                result = Mathf.RoundToInt(t * maxIdx);
                e.Use();
            }

            float cy = r.center.y;
            FxTheme.HLine(r.x, r.xMax, cy, FxTheme.LineHi);
            for (int i = 0; i <= maxIdx; i++)
            {
                float tx = r.x + r.width * i / maxIdx;
                FxTheme.Fill(new Rect(tx - 1f, cy - 5f, 2f, 10f),
                    i <= curIdx ? FxTheme.Accent : FxTheme.LineHi);
            }
            float kx = r.x + r.width * Mathf.Clamp01((float)result / maxIdx);
            FxTheme.Fill(new Rect(kx - 5f, cy - 5f, 10f, 10f), FxTheme.Accent);
            return result;
        }

        private void DrawNewBottom(Rect r, FxGameComponent fx)
        {
            FxWidgets.Panel(r);
            float tx = r.x + 12f;
            string[] tabs =
            {
                "RimForex_UiPositions".Translate(),
                "RimForex_UiHistory".Translate(),
                "RimForex_UiLog".Translate(),
            };
            for (int i = 0; i < tabs.Length; i++)
            {
                Rect tab = new Rect(tx, r.y + 2f, 74f, 28f);
                if (FxWidgets.Tab(tab, tabs[i], uiBottomTab == i))
                {
                    uiBottomTab = i;
                    uiBottomScroll = 0f;
                }
                tx += 76f;
            }
            FxTheme.HLine(r.x, r.xMax, r.y + 30f, FxTheme.Line);

            Rect content = new Rect(r.x + 10f, r.y + 34f, r.width - 20f, r.height - 38f);
            if (content.height < 16f) return;

            if (uiBottomTab == 0) DrawPositionsTab(content);
            else if (uiBottomTab == 1) DrawHistoryTab(content);
            else FxWidgets.Center(content, "RimForex_UiLogWip".Translate(), FxTheme.Muted);
        }

        private void DrawPositionsTab(Rect content)
        {
            if (account == null || account.positions == null || account.positions.Count == 0)
            {
                FxWidgets.Center(content, "RimForex_NoPositions".Translate(), FxTheme.Muted);
                return;
            }

            float rowH = 30f;
            float viewH = account.positions.Count * rowH;
            HandleUiScroll(content, ref uiBottomScroll, viewH);

            Rect list = new Rect(content.x, content.y, content.width - 10f, content.height);
            GUI.BeginGroup(list);
            for (int i = 0; i < account.positions.Count; i++)
            {
                FxPosition p = account.positions[i];
                if (p == null) continue;
                float yy = i * rowH - uiBottomScroll;
                if (yy + rowH < 0f || yy > list.height) continue;

                Rect row = new Rect(0f, yy, list.width, rowH);
                if (i % 2 == 1) FxTheme.Fill(row, new Color32(18, 23, 30, 255));

                string dir = p.isLong ? "RimForex_Long".Translate() : "RimForex_Short".Translate();
                FxTheme.Small(new Rect(8f, yy + 6f, 120f, 18f),
                    Truncate(p.currencyName + "  " + dir, 20),
                    p.isLong ? FxTheme.Long : FxTheme.Short);

                FxTheme.Tiny(new Rect(150f, yy + 7f, 80f, 16f),
                    "×" + p.units.ToString("N0"), FxTheme.Muted);
                FxTheme.Small(new Rect(232f, yy + 6f, 100f, 18f),
                    p.Pnl.ToString("+0;-0;0"), FxTheme.PnlColor(p.Pnl),
                    TextAnchor.MiddleRight);

                Rect edit = new Rect(8f, yy, list.width - 80f, rowH);
                if (Widgets.ButtonInvisible(edit))
                {
                    Find.WindowStack.Add(new Window_FxTpSl(Fx, account, p));
                }

                if (FxWidgets.Button(new Rect(list.width - 68f, yy + 3f, 60f, 24f),
                        "RimForex_Close".Translate()))
                {
                    ClosePosition(p);
                }
                FxTheme.HLine(0f, list.width, yy + rowH - 1f, FxTheme.Line);
            }
            GUI.EndGroup();

            float frac = content.height / Mathf.Max(content.height, viewH);
            FxWidgets.ScrollBar(new Rect(content.xMax - 8f, content.y, 6f, content.height),
                uiBottomScroll, frac);
        }

        private void DrawHistoryTab(Rect content)
        {
            List<FxTradeRecord> hist = account != null ? account.history : null;
            if (hist == null || hist.Count == 0)
            {
                FxWidgets.Center(content, "RimForex_UiNoHistory".Translate(), FxTheme.Muted);
                return;
            }

            float rowH = 24f;
            float viewH = hist.Count * rowH;
            HandleUiScroll(content, ref uiBottomScroll, viewH);

            Rect list = new Rect(content.x, content.y, content.width - 10f, content.height);
            GUI.BeginGroup(list);
            for (int n = 0; n < hist.Count; n++)
            {
                FxTradeRecord rec = hist[hist.Count - 1 - n];
                if (rec == null) continue;
                float yy = n * rowH - uiBottomScroll;
                if (yy + rowH < 0f || yy > list.height) continue;

                if (n % 2 == 1) FxTheme.Fill(new Rect(0f, yy, list.width, rowH),
                    new Color32(18, 23, 30, 255));

                string label = rec.isFee ? rec.feeLabel : rec.pairLabel;
                FxTheme.Small(new Rect(8f, yy + 4f, list.width - 180f, 16f),
                    Truncate(label, 34), FxTheme.TextColor);
                FxTheme.Small(new Rect(list.width - 170f, yy + 4f, 100f, 16f),
                    rec.pnl.ToString("+0;-0;0"), FxTheme.PnlColor(rec.pnl),
                    TextAnchor.MiddleRight);
                FxTheme.Tiny(new Rect(list.width - 66f, yy + 5f, 62f, 14f),
                    (rec.closedTick / FxGameComponent.TicksPerDay).ToString(),
                    FxTheme.Muted, TextAnchor.MiddleRight);
                FxTheme.HLine(0f, list.width, yy + rowH - 1f, FxTheme.Line);
            }
            GUI.EndGroup();

            float frac = content.height / Mathf.Max(content.height, viewH);
            FxWidgets.ScrollBar(new Rect(content.xMax - 8f, content.y, 6f, content.height),
                uiBottomScroll, frac);
        }

        private static void HandleUiScroll(Rect content, ref float scroll, float viewH)
        {
            Event e = Event.current;
            if (Mouse.IsOver(content) && e.type == EventType.ScrollWheel)
            {
                scroll -= e.delta.y * 24f;
                e.Use();
            }
            scroll = Mathf.Clamp(scroll, 0f, Mathf.Max(0f, viewH - content.height));
        }

    }
}
