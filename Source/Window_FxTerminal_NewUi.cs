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
        private bool uiLayoutLoaded;
        /// <summary>正在拖的分隔条：0 无，1 左，2 右，3 底。</summary>
        private int uiDragSplit;
        private int uiBottomTab;
        private float uiBottomScroll;
        private FxPosition uiChartPosition;
        private int uiChangeMode;          // 0 日 / 1 五日 / 2 象
        private bool uiAllIn;
        private float uiMarketScroll;
        private bool uiShowEntry = true;
        private bool uiShowBe = true;
        private bool uiShowStop = true;

        public override void PreClose()
        {
            base.PreClose();
            if (uiLayoutLoaded)
            {
                SaveUiRatios();
                if (RimForexMod.Instance != null) RimForexMod.Instance.WriteSettings();
            }
        }

        private void EnsureUiLayout()
        {
            if (uiLayoutLoaded) return;
            uiLayoutLoaded = true;
            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg != null)
            {
                uiLeftRatio = Mathf.Clamp(cfg.uiLeftRatio, 0.16f, 0.42f);
                uiRightRatio = Mathf.Clamp(cfg.uiRightRatio, 0.16f, 0.42f);
                uiBottomRatio = Mathf.Clamp(cfg.uiBottomRatio, 0.10f, 0.42f);
            }
        }

        private void SaveUiRatios()
        {
            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg == null) return;
            cfg.uiLeftRatio = uiLeftRatio;
            cfg.uiRightRatio = uiRightRatio;
            cfg.uiBottomRatio = uiBottomRatio;
        }

        private void ResetUiLayout()
        {
            uiLeftRatio = 0.24f;
            uiRightRatio = 0.22f;
            uiBottomRatio = 0.20f;
            uiDragSplit = 0;
            SaveUiRatios();
        }

        private void DrawNewUiContents(Rect inRect, FxGameComponent fx,
            List<FxCurrency> currencies)
        {
            if (windowRect.width < 1100f) windowRect.width = 1100f;
            if (windowRect.height < 680f) windowRect.height = 680f;
            EnsureUiLayout();
            FxTheme.Fill(inRect, FxTheme.WindowBg);
            FxTheme.Border(inRect, FxTheme.Line);

            Rect title = new Rect(inRect.x, inRect.y, inRect.width, UiTitleH);
            DrawNewTitleBar(title, fx);

            Rect content = new Rect(inRect.x + 1f, title.yMax,
                inRect.width - 2f, inRect.height - UiTitleH - 1f);

            bool inDebt = account != null && account.balance < 0f;
            bool frozenAccount = account != null
                && account.frozenUntilTick > GenTicks.TicksGame;
            float accountH = UiAccountH + (inDebt ? 18f : 0f)
                + (frozenAccount ? 18f : 0f);
            Rect accountRect = new Rect(content.x, content.y, content.width, accountH);
            DrawNewAccountStrip(accountRect, fx);

            const float timeH = 32f;
            float bottomLimit = content.yMax - timeH;
            float maxBottom = content.height - accountH - 260f;
            float bottomH = Mathf.Clamp(content.height * uiBottomRatio, 120f,
                Mathf.Max(120f, maxBottom));
            Rect bottomRect = new Rect(content.x, bottomLimit - bottomH,
                content.width, bottomH);
            Rect timeRect = new Rect(content.x, content.yMax - timeH,
                content.width, timeH);

            float mainY = accountRect.yMax + 8f;
            float mainH = bottomRect.y - 8f - mainY;
            Rect mainRect = new Rect(content.x, mainY, content.width, Mathf.Max(180f, mainH));

            if (chartViewMode == 1 || chartViewMode == 2)
            {
                float orderW = Mathf.Clamp(mainRect.width * uiRightRatio, 300f,
                    mainRect.width - 320f);
                Rect orderRect = new Rect(mainRect.xMax - orderW, mainRect.y,
                    orderW, mainRect.height);
                Rect bigRect = new Rect(mainRect.x, mainRect.y,
                    mainRect.width - orderW - 8f, mainRect.height);
                if (chartViewMode == 1) DrawNewMarket(bigRect, currencies);
                else DrawNewChart(bigRect, fx);
                DrawNewOrder(orderRect, fx);
                DrawNewBottom(bottomRect, fx);
                DrawNewTimeBar(timeRect);
                FxTooltip.Draw();
                return;
            }

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
            DrawNewTimeBar(timeRect);

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
            FxTooltip.Draw();
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
                SaveUiRatios();
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
                SaveUiRatios();
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

            if (bar.width > 1400f)
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

            Rect history = new Rect(right - 68f, by, 68f, bh);
            if (FxWidgets.Button(history, "RimForex_UiHistory".Translate()))
            {
                if (Find.WindowStack.IsOpen(typeof(Window_TradeHistory)))
                {
                    Find.WindowStack.TryRemove(typeof(Window_TradeHistory), true);
                }
                else if (account != null)
                {
                    Find.WindowStack.Add(new Window_TradeHistory(account));
                }
            }
            right -= 74f;

            string viewName = chartViewMode == 1
                ? "RimForex_ViewTable".Translate()
                : chartViewMode == 2 ? "RimForex_ViewChart".Translate()
                : "RimForex_ViewBoth".Translate();
            Rect view = new Rect(right - 98f, by, 98f, bh);
            if (FxWidgets.Button(view, "RimForex_TopBarView".Translate() + " " + viewName,
                    true, accent: true))
            {
                chartViewMode = (chartViewMode + 1) % 3;
            }
            right -= 104f;

            Rect loans = new Rect(right - 44f, by, 44f, bh);
            DrawLoanButton(loans, fx);
        }

        /// <summary>
        /// 借贷按钮：金闪闪的脉冲边框 + 高光，专门吸引散户点进来。
        /// 入口已经整合进终端，终端命令栏的旧 gizmo 已删除。
        /// </summary>
        private void DrawLoanButton(Rect r, FxGameComponent fx)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 3.2f);
            Color gold = Color.Lerp(new Color32(170, 124, 46, 255),
                new Color32(255, 222, 132, 255), pulse);

            FxTheme.Fill(r, new Color32(42, 31, 12, 255));
            FxTheme.Fill(new Rect(r.x, r.y, r.width, r.height * 0.48f),
                new Color32(74, 54, 20, 255));
            FxTheme.Border(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f),
                new Color(gold.r, gold.g, gold.b, 0.25f + 0.35f * pulse));
            FxTheme.Border(r, gold);

            // 两颗小亮点
            FxTheme.Fill(new Rect(r.x + 4f, r.y + 4f, 2f, 2f), gold);
            FxTheme.Fill(new Rect(r.xMax - 6f, r.y + 5f, 2f, 2f),
                new Color(gold.r, gold.g, gold.b, 0.7f));

            FxTheme.Label(r, "RimForex_UiLoans".Translate(), GameFont.Small,
                gold, TextAnchor.MiddleCenter);

            if (Widgets.ButtonInvisible(r))
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
            FxTooltip.Tip(r, "RimForex_LoanGizmoDesc".Translate());
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
            float colW = (r.width - pad * 2f - buttonsW) / 6f;
            string[] labels = { "RimForex_Equity".Translate(), "RimForex_FreeMargin".Translate(),
                "RimForex_UsedMargin".Translate(), "RimForex_FloatingPnl".Translate(),
                "RimForex_UiLastSettle".Translate(), "RimForex_UiPositionsCount".Translate() };
            float[] values = { account.Equity, account.balance, account.MarginUsed,
                account.FloatingPnl, account.lastDayPnl,
                account.positions != null ? account.positions.Count : 0 };

            for (int i = 0; i < labels.Length; i++)
            {
                float x = r.x + pad + i * colW;
                string label = labels[i];
                if (i == 0 || i == 3)
                {
                    label += " " + "RimForex_TipMark".Translate();
                }
                Rect labelRect = new Rect(x, r.y + 9f, colW - 8f, 16f);
                FxTheme.Tiny(labelRect, label, FxTheme.Muted);
                if (i == 0) FxTooltip.Tip(labelRect, "RimForex_TipEquity".Translate());
                else if (i == 3) FxTooltip.Tip(labelRect, "RimForex_TipFloat".Translate());
                Color c = (i == 3 || i == 4) ? FxTheme.PnlColor(values[i])
                    : FxTheme.TextColor;
                FxTheme.Label(new Rect(x, r.y + 27f, colW - 8f, 26f),
                    i == 5 ? values[i].ToString("0") : values[i].ToString("F0"),
                    GameFont.Medium, c, TextAnchor.UpperLeft);
            }

            if (account.balance < 0f)
            {
                FxTheme.Tiny(new Rect(r.x + pad, r.yMax - 18f, r.width - pad * 2f, 16f),
                    "RimForex_DebtWarning".Translate(
                        (-account.balance).ToString("F0")), FxTheme.Short);
            }
            if (account.frozenUntilTick > GenTicks.TicksGame)
            {
                int frozenDays = (account.frozenUntilTick - GenTicks.TicksGame)
                    / FxGameComponent.TicksPerDay + 1;
                FxTheme.Tiny(new Rect(r.x + pad,
                        r.yMax - (account.balance < 0f ? 34f : 18f),
                        r.width - pad * 2f, 16f),
                    "RimForex_FrozenWarning".Translate(frozenDays.ToString()),
                    FxTheme.Accent);
            }
            float bx = r.xMax - pad - btnW;
            if (FxWidgets.Button(new Rect(bx, r.y + 14f, btnW, 28f),
                    "RimForex_UiDeposit".Translate(), true, true))
            {
                OpenCashTransfer(true);
            }
            if (FxWidgets.Button(new Rect(bx - btnW - btnGap, r.y + 14f, btnW, 28f),
                    "RimForex_UiWithdraw".Translate(), true, accent: true))
            {
                OpenCashTransfer(false);
            }
            FxTheme.HLine(r.x, r.xMax, r.yMax - 1f, FxTheme.Line);
        }

        private void DrawNewTimeBar(Rect r)
        {
            FxTheme.HLine(r.x, r.xMax, r.y, FxTheme.Line);
            Rect buttons = new Rect(r.x + 4f,
                r.y + (r.height - TimeControls.TimeButSize.y) * 0.5f,
                TimeControls.TimeButSize.x * 4f, TimeControls.TimeButSize.y);
            TimeControls.DoTimeControlsGUI(buttons);
            FxTheme.Tiny(new Rect(buttons.xMax + 10f, r.y + 7f,
                    r.width - buttons.width - 20f, 16f),
                "RimForex_TimeHint".Translate(), FxTheme.Muted);
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
            FxTheme.Fill(r, new Color32(20, 27, 36, 255));
            FxTheme.Border(r, FxTheme.LineHi);
            FxWidgets.SectionHeader(r, "RimForex_UiMarket".Translate());

            float headY = r.y + 34f;
            float w = r.width - 20f;
            float x = r.x + 10f;
            FxTheme.Tiny(new Rect(x, headY, w * 0.54f, 16f),
                "RimForex_UiColPair".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(x + w * 0.54f, headY, w * 0.16f, 16f),
                "RimForex_UiColRate".Translate(), FxTheme.Muted);

            Rect changeHead = new Rect(x + w * 0.72f, headY, w * 0.16f, 16f);
            string modeName = uiChangeMode == 1
                ? "RimForex_UiChange5d".Translate()
                : uiChangeMode == 2 ? "RimForex_UiChangeQtr".Translate()
                : "RimForex_UiChangeDay".Translate();
            FxTheme.Tiny(changeHead, "RimForex_UiColChange".Translate() + " · " + modeName,
                FxTheme.Muted, TextAnchor.MiddleRight);
            if (Widgets.ButtonInvisible(changeHead))
            {
                uiChangeMode = (uiChangeMode + 1) % 3;
            }
            FxTooltip.Tip(changeHead, "RimForex_UiChangeCycleTip".Translate());

            FxTheme.Tiny(new Rect(x + w * 0.88f, headY, w * 0.12f, 16f),
                "RimForex_UiColVol".Translate(), FxTheme.Muted,
                TextAnchor.MiddleRight);
            FxTheme.HLine(r.x, r.xMax, headY + 18f, FxTheme.Line);

            float rowH = 34f;
            float listTop = headY + 22f;
            float viewH = currencies.Count * rowH;
            float listH = r.yMax - 6f - listTop;
            if (listH <= 0f) return;

            Event e = Event.current;
            if (Mouse.IsOver(r) && e.type == EventType.ScrollWheel)
            {
                uiMarketScroll += e.delta.y * 24f;
                e.Use();
            }
            uiMarketScroll = Mathf.Clamp(uiMarketScroll, 0f,
                Mathf.Max(0f, viewH - listH));

            for (int i = 0; i < currencies.Count; i++)
            {
                FxCurrency c = currencies[i];
                if (c == null) continue;
                float y = listTop + i * rowH - uiMarketScroll;
                if (y + rowH < listTop || y > r.yMax - 4f) continue;

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

                bool hasPos = false;
                if (account != null && account.positions != null)
                {
                    for (int k = 0; k < account.positions.Count; k++)
                    {
                        var p = account.positions[k];
                        if (p != null && p.currencyFactionLoadID == c.factionLoadID)
                        {
                            hasPos = true;
                            break;
                        }
                    }
                }

                string status = c.haltUntilTick > GenTicks.TicksGame ? "停牌"
                    : c.pegUntilTick > GenTicks.TicksGame ? "钉住" : "";
                FxTheme.Small(new Rect(x + 6f, y + 2f, w * 0.52f, 18f),
                    Truncate(c.PairLabel, 18), FxTheme.TextColor);
                Color statusColor = c.haltUntilTick > GenTicks.TicksGame
                    ? FxTheme.Warning
                    : c.pegUntilTick > GenTicks.TicksGame ? FxTheme.Accent
                    : hasPos ? FxTheme.Long : FxTheme.Muted;
                string sub = c.factionName;
                if (status.Length > 0) sub += "  " + status;
                if (hasPos) sub += "  ●";
                FxTheme.Tiny(new Rect(x + 6f, y + 18f, w * 0.52f, 14f),
                    sub, statusColor);

                FxTheme.Small(new Rect(x + w * 0.54f, y + 8f, w * 0.17f, 18f),
                    c.rate.ToString("F2"), FxTheme.TextColor, TextAnchor.MiddleLeft);

                float pct;
                if (uiChangeMode == 1)
                {
                    pct = c.logRate5dAgo > 0.0001f && c.rate > 0.0001f
                        ? (Mathf.Exp(FxGameComponent.Return5d(c)) - 1f) * 100f : 0f;
                }
                else if (uiChangeMode == 2)
                {
                    pct = (Mathf.Exp(FxGameComponent.ReturnQtr(c)) - 1f) * 100f;
                }
                else
                {
                    pct = c.previousRate > 0.01f
                        ? (c.rate / c.previousRate - 1f) * 100f : 0f;
                }

                string pctText = pct.ToString("+0.0;-0.0;0.0") + "%";
                FxTheme.Small(new Rect(x + w * 0.72f, y + 8f, w * 0.16f, 18f),
                    pctText, pct >= 0f ? FxTheme.Long : FxTheme.Short,
                    TextAnchor.MiddleRight);

                FxTheme.Small(new Rect(x + w * 0.88f, y + 8f, w * 0.12f, 18f),
                    "×" + c.volatility.ToString("0.0"), FxTheme.Muted,
                    TextAnchor.MiddleRight);

                FxTheme.HLine(r.x + 8f, r.xMax - 8f, y + rowH, FxTheme.Line);
            }

            float frac = listH / Mathf.Max(listH, viewH);
            float maxScroll = Mathf.Max(0f, viewH - listH);
            FxWidgets.ScrollBar(new Rect(r.xMax - 8f, listTop, 6f, listH),
                maxScroll > 0.01f ? uiMarketScroll / maxScroll : 0f, frac);
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

            float tgW = 58f;
            float tgX = r.xMax - 4f - (tgW + 4f) * 3f - 76f;
            if (FxWidgets.Button(new Rect(tgX, tabY, tgW, 26f),
                    "RimForex_UiToggleEntry".Translate(), true,
                    accent: uiShowEntry, font: GameFont.Tiny))
            {
                uiShowEntry = !uiShowEntry;
            }
            if (FxWidgets.Button(new Rect(tgX + tgW + 4f, tabY, tgW, 26f),
                    "RimForex_UiToggleBe".Translate(), true,
                    accent: uiShowBe, font: GameFont.Tiny))
            {
                uiShowBe = !uiShowBe;
            }
            if (FxWidgets.Button(new Rect(tgX + (tgW + 4f) * 2f, tabY, tgW, 26f),
                    "RimForex_UiToggleStop".Translate(), true,
                    accent: uiShowStop, font: GameFont.Tiny))
            {
                uiShowStop = !uiShowStop;
            }
            if (FxWidgets.Button(new Rect(r.xMax - 74f, tabY, 70f, 26f),
                    "RimForex_ChartReset".Translate(), true, font: GameFont.Tiny))
            {
                chartCandles = 60;
                ResetUiLayout();
            }

            FxCurrency chartCur = fx.market.Get(selectedFactionId);
            if (chartCur != null && tgX > r.x + 172f)
            {
                string chartTitle = Truncate(chartCur.PairLabel, 16);
                if (fx.CurrencyHalted(chartCur))
                    chartTitle += "  " + "RimForex_UiStatusHalt".Translate();
                else if (chartCur.pegUntilTick > GenTicks.TicksGame)
                    chartTitle += "  " + "RimForex_UiStatusPeg".Translate();
                FxTheme.Tiny(new Rect(r.x + 170f, tabY + 5f,
                        tgX - r.x - 174f, 16f),
                    chartTitle, FxTheme.Muted);
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
                    + Mathf.RoundToInt(Mathf.Sign(wheel.delta.y)) * ChartZoomStep,
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

            // 当前货币最新一笔持仓的三条参考线
            FxPosition pos = uiChartPosition;
            if (pos == null || pos.currencyFactionLoadID != selectedFactionId)
            {
                pos = null;
                if (account != null && account.positions != null)
                {
                    for (int i = account.positions.Count - 1; i >= 0; i--)
                    {
                        var p = account.positions[i];
                        if (p != null && p.currencyFactionLoadID == selectedFactionId)
                        {
                            pos = p;
                            break;
                        }
                    }
                }
            }

            if (pos != null)
            {
                float units = Mathf.Abs((float)pos.units);
                float spreadRate = pos.entrySpreadRate > 0.0001f
                    ? pos.entrySpreadRate
                    : FxMarket.SpreadRateFor(fx.market.Get(selectedFactionId));
                float half = spreadRate * 0.5f;
                float denom = pos.isLong ? (1f - half) : (1f + half);
                if (denom > 0.0001f)
                {
                    float mid0 = pos.entryMid > 0f ? pos.entryMid : pos.entryRate / denom;
                    if (uiShowEntry)
                    {
                        DrawChartRefLine(plot, min, max, mid0,
                            new Color(FxTheme.Warning.r, FxTheme.Warning.g,
                                FxTheme.Warning.b, 0.7f),
                            "RimForex_UiEntryLine".Translate(),
                            false, true);
                    }
                    if (uiShowBe)
                    {
                        float beMid = mid0 * (pos.isLong ? (1f + half) : (1f - half))
                            / denom;
                        DrawChartRefLine(plot, min, max, beMid,
                            new Color(FxTheme.Accent.r, FxTheme.Accent.g,
                                FxTheme.Accent.b, 0.85f),
                            "RimForex_UiBeLine".Translate(),
                            false, false);
                    }
                    if (uiShowStop && units > 0.0001f)
                    {
                        float baseM = pos.BaseMargin;
                        if (baseM < 0.01f) baseM = pos.margin;
                        float dist = ((1f - FxGameComponent.StopOutFraction) * baseM
                            + pos.addedMargin) / units;
                        float markStop = pos.isLong
                            ? pos.entryRate - dist : pos.entryRate + dist;
                        float soMid = markStop / denom;
                        DrawChartRefLine(plot, min, max, soMid,
                            new Color(FxTheme.Short.r, FxTheme.Short.g,
                                FxTheme.Short.b, 0.9f),
                            "RimForex_UiStopLine".Translate(),
                            true, true);

                        if (soMid < min || soMid > max)
                        {
                            bool above = soMid > max;
                            float edgeY = above ? plot.y : plot.yMax;
                            float offPct = (soMid / pos.entryMid - 1f) * 100f;
                            string txt = "RimForex_ChartStopOutOff".Translate(
                                soMid.ToString("F2"),
                                offPct.ToString("+0.00;-0.00;0.00"));
                            FxTheme.Tiny(new Rect(plot.x + 2f,
                                above ? edgeY + 1f : edgeY - 15f,
                                plot.width - 4f, 14f), txt, FxTheme.Short);
                        }
                    }
                }
            }

            float last = view[view.Count - 1].close;
            float lastY = plot.y + (max - last) / (max - min) * plot.height;
            FxTheme.HLine(plot.x, plot.xMax, lastY, FxTheme.Warning);
            Rect tag = new Rect(plot.xMax + 4f, lastY - 9f, 58f, 18f);
            FxTheme.Fill(tag, FxTheme.Warning);
            FxTheme.Tiny(tag, last.ToString("F2"), new Color32(20, 20, 20, 255),
                TextAnchor.MiddleCenter);

            // 悬停：十字线 + 完整 OHLC + 日期
            if (Mouse.IsOver(plot) && view.Count > 0)
            {
                Event he = Event.current;
                float mx = he.mousePosition.x;
                float my = he.mousePosition.y;
                int hi = Mathf.Clamp((int)((mx - plot.x) / slot), 0, view.Count - 1);
                float hx = plot.x + slot * (hi + 0.5f);

                FxTheme.VLine(hx, plot.y, plot.yMax, new Color(1f, 1f, 1f, 0.25f));
                FxTheme.HLine(plot.x, plot.xMax, my, new Color(1f, 1f, 1f, 0.18f));

                float hoverPrice = Mathf.Lerp(max, min,
                    Mathf.Clamp01((my - plot.y) / Mathf.Max(1f, plot.height)));
                FxTheme.Fill(new Rect(plot.xMax + 4f, my - 8f, 58f, 16f),
                    new Color32(40, 48, 60, 255));
                FxTheme.Tiny(new Rect(plot.xMax + 4f, my - 8f, 58f, 16f),
                    hoverPrice.ToString("F2"), FxTheme.TextColor, TextAnchor.MiddleCenter);

                FxCandle hc = view[hi];
                bool up = hc.close >= hc.open;
                string date = hc.startTick > 0
                    ? GenDate.DateFullStringAt(GenDate.TickGameToAbs(hc.startTick), Vector2.zero)
                    : "";
                string title = (up ? "RimForex_HoverUp" : "RimForex_HoverDown").Translate();
                if (date.Length > 0) title = date + "  " + title;

                string ohlc = "RimForex_UiCandleDetail".Translate(
                    hc.open.ToString("F2"), hc.high.ToString("F2"),
                    hc.low.ToString("F2"), hc.close.ToString("F2"));
                float pct = hc.open > 0.0001f ? (hc.close / hc.open - 1f) * 100f : 0f;
                string range = "RimForex_HoverRange".Translate(
                    (hc.high - hc.low).ToString("F2"),
                    pct.ToString("+0.00;-0.00;0.00") + "%");
                bool forming = hi == view.Count - 1;
                string formingText = forming
                    ? "RimForex_HoverForming".Translate(SpanPeriodText()) : "";

                float boxW = 320f;
                float boxH = forming ? 86f : 70f;
                float bx = Mathf.Clamp(mx + 14f, plot.x, plot.xMax - boxW - 4f);
                float by = Mathf.Clamp(my + 14f, plot.y + 4f, plot.yMax - boxH - 4f);
                Rect tip = new Rect(bx, by, boxW, boxH);
                FxTheme.Fill(tip, new Color32(18, 23, 30, 248));
                FxTheme.Border(tip, FxTheme.LineHi);

                FxTheme.Tiny(new Rect(tip.x + 10f, tip.y + 6f, tip.width - 20f, 16f),
                    title, FxTheme.TextColor);
                FxTheme.Tiny(new Rect(tip.x + 10f, tip.y + 24f, tip.width - 20f, 16f),
                    ohlc, FxTheme.Muted);
                FxTheme.Tiny(new Rect(tip.x + 10f, tip.y + 42f, tip.width - 20f, 16f),
                    range, up ? FxTheme.Long : FxTheme.Short);
                if (forming)
                {
                    FxTheme.Tiny(new Rect(tip.x + 10f, tip.y + 60f, tip.width - 20f, 18f),
                        formingText, FxTheme.Warning);
                }
            }

            int dateAbs = GenDate.TickGameToAbs(GenTicks.TicksGame);
            FxTheme.Tiny(new Rect(plot.x, plot.yMax + 4f, 240f, 16f),
                GenDate.DateFullStringAt(dateAbs, Vector2.zero), FxTheme.Muted);
        }

        private static void DrawChartRefLine(Rect plot, float min, float max,
            float price, Color col, string label, bool clampToEdge = false,
            bool dashed = false)
        {
            bool inRange = price >= min && price <= max;
            if (!inRange)
            {
                if (!clampToEdge) return;
                price = price > max ? max : min;
            }
            float y = plot.y + (max - price) / Mathf.Max(0.0001f, max - min)
                * plot.height;
            if (dashed)
            {
                for (float x = plot.x; x < plot.xMax; x += 6f)
                {
                    FxTheme.Fill(new Rect(x, y, Mathf.Min(3f, plot.xMax - x), 1f), col);
                }
            }
            else
            {
                FxTheme.HLine(plot.x, plot.xMax, y, col);
            }
            if (inRange && !string.IsNullOrEmpty(label))
            {
                FxTheme.Tiny(new Rect(plot.x + 4f, y - 13f, 80f, 12f),
                    label, col);
            }
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
            float minMargin = MinMarginFor(ask, orderLeverage);
            int minMarginCeil = Mathf.CeilToInt(minMargin);
            FxTheme.Tiny(new Rect(x, y, w, 16f), "RimForex_MarginInput".Translate(),
                FxTheme.Muted);

            int parsed;
            if (!int.TryParse(marginBuffer, out parsed)) parsed = 0;
            parsed = Mathf.Max(0, parsed);
            if (GUI.GetNameOfFocusedControl() == "fx-order-margin"
                && parsed != orderMargin) uiAllIn = false;
            bool focused = GUI.GetNameOfFocusedControl() == "fx-order-margin";
            bool belowMin = parsed > 0 && parsed < minMarginCeil;
            if (!focused && parsed < minMarginCeil)
            {
                parsed = minMarginCeil;
                marginBuffer = parsed.ToString();
                belowMin = false;
            }
            if (belowMin)
            {
                FxTheme.Tiny(new Rect(x, y, w, 16f),
                    "RimForex_UiBelowMin".Translate(minMarginCeil.ToString()),
                    FxTheme.Short, TextAnchor.MiddleRight);
            }
            y += 18f;

            Rect marginRect = new Rect(x, y, w - 60f, 28f);
            marginBuffer = FxWidgets.TextField(marginRect, marginBuffer, 14, "fx-order-margin");
            orderMargin = Mathf.Max(minMarginCeil, parsed);

            Rect minusBtn = new Rect(x + w - 56f, y, 26f, 28f);
            Rect plusBtn = new Rect(x + w - 26f, y, 26f, 28f);
            FxWidgets.ButtonVisual(minusBtn, "−");
            FxWidgets.ButtonVisual(plusBtn, "+");

            // 与原版风 UI 一致：短按 1，按住后 5 / 10 / 50 / 200 逐档加速
            HoldInt(minusBtn, plusBtn, 1, delegate(int dir, int step)
            {
                orderMargin = Mathf.Max(minMarginCeil, orderMargin + dir * step);
                marginBuffer = orderMargin.ToString();
                marginAtMinimum = orderMargin <= minMarginCeil;
                uiAllIn = false;
            });
            y += 34f;

            float half = (w - 6f) / 2f;
            if (FxWidgets.Button(new Rect(x, y, half, 24f), "RimForex_Zero".Translate()))
            {
                orderMargin = Mathf.CeilToInt(minMargin);
                marginBuffer = orderMargin.ToString();
                marginAtMinimum = true;
                uiAllIn = false;
            }
            if (FxWidgets.Button(new Rect(x + half + 6f, y, half, 24f),
                    "RimForex_AllIn".Translate(), true, false, false, true))
            {
                uiAllIn = true;
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
            DrawTpSlGroup(new Rect(x, y, half, 42f), "RimForex_TpShort",
                ref orderTpBuf, ref orderTp, true, sel);
            DrawTpSlGroup(new Rect(x + half + 6f, y, half, 42f), "RimForex_SlShort",
                ref orderSlBuf, ref orderSl, false, sel);
            y += 50f;

            // ── 费用 ──
            int wantUnits = EstimateUnits(orderMargin, ask, orderLeverage);
            int units;
            float actualMargin, notional, fee;
            ComputeEntry(wantUnits, ask, orderLeverage, out units,
                out actualMargin, out notional, out fee);
            float total = actualMargin + fee;
            bool enough = total <= account.balance;

            bool showBreakdown = RimForexMod.Settings == null
                || RimForexMod.Settings.showCostBreakdown;
            if (compact || !showBreakdown)
            {
                FxTheme.Tiny(new Rect(x, y, w, 16f),
                    "RimForex_UiCompactLine".Translate(units.ToString("N0"),
                        total.ToString("F0"), account.balance.ToString("F0")),
                    enough ? FxTheme.Long : FxTheme.Short);
                FxTooltip.Tip(new Rect(x, y, w, 16f),
                    "RimForex_LineCompactTip".Translate(
                        (units * (ask - bid)).ToString("F1"),
                        actualMargin.ToString("F0"), fee.ToString("F2")));
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

            // ── 风险提示（可同时出现多条）──
            float by = r.yMax - 40f;
            var notes = new System.Collections.Generic.List<string>();
            var noteColors = new System.Collections.Generic.List<Color>();

            if (units > 0 && (uiAllIn || account.balance - total < 5f))
            {
                notes.Add("RimForex_AllInWarn".Translate());
                noteColors.Add(FxTheme.Warning);
            }
            if (!FxGameComponent.LeverageSafeFor(sel, orderLeverage))
            {
                notes.Add("RimForex_LevUnsafeNote".Translate(maxSafe.ToString("0")));
                noteColors.Add(FxTheme.Short);
            }
            else
            {
                float stopDist = (1f - FxGameComponent.StopOutFraction)
                    / Mathf.Max(1f, orderLeverage);
                float dailyVol = FxMarket.DailyVol * sel.volatility;
                if (stopDist < dailyVol)
                {
                    notes.Add("RimForex_LevRiskyNote".Translate(
                        (stopDist * 100f).ToString("0.00"),
                        (dailyVol * 100f).ToString("0.00"),
                        (stopDist / Mathf.Max(0.0001f, dailyVol)).ToString("0.00")));
                    noteColors.Add(FxTheme.Warning);
                }
            }
            if (halted)
            {
                notes.Add("RimForex_MsgTradeHalted".Translate(sel.PairLabel));
                noteColors.Add(FxTheme.Warning);
            }
            else if (!enough)
            {
                notes.Add("RimForex_NotEnoughMargin".Translate());
                noteColors.Add(FxTheme.Short);
            }

            if (notes.Count > 0)
            {
                GameFont oldFont = Text.Font;
                bool oldWrap = Text.WordWrap;
                Text.Font = GameFont.Tiny;
                Text.WordWrap = true;
                float totalH = 0f;
                var heights = new System.Collections.Generic.List<float>();
                for (int i = 0; i < notes.Count; i++)
                {
                    float hh = Text.CalcHeight(notes[i], w + 8f);
                    heights.Add(hh);
                    totalH += hh + 2f;
                }
                Text.Font = oldFont;
                Text.WordWrap = oldWrap;

                float noteY = Mathf.Min(y, by - totalH - 4f);
                for (int i = 0; i < notes.Count; i++)
                {
                    FxTheme.Label(new Rect(x - 2f, noteY, w + 8f, heights[i]),
                        notes[i], GameFont.Tiny, noteColors[i],
                        TextAnchor.UpperLeft, true);
                    noteY += heights[i] + 2f;
                }
            }

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

        /// <summary>止盈或止损：标签 + 减号 + 输入 + 加号 + 相对现价的百分比。</summary>
        private void DrawTpSlGroup(Rect r, string labelKey, ref string buf,
            ref float value, bool isTp, FxCurrency sel)
        {
            FxTheme.Tiny(new Rect(r.x, r.y, r.width, 16f), labelKey.Translate(),
                FxTheme.Muted);

            const float btnW = 18f;
            const float pctW = 38f;
            const float gap = 4f;
            float fieldW = r.width - btnW * 2f - pctW - gap * 3f;
            float rowY = r.y + 16f;

            Rect minusRect = new Rect(r.x, rowY, btnW, 26f);
            Rect plusRect = new Rect(r.x + btnW + gap + fieldW + gap, rowY, btnW, 26f);
            FxWidgets.ButtonVisual(minusRect, "−");
            FxWidgets.ButtonVisual(plusRect, "+");

            // 闭包不能捕获 ref 参数，先落到局部变量，画完再写回
            string localBuf = buf;
            float cur = value;
            localBuf = FxWidgets.TextField(new Rect(r.x + btnW + gap, rowY, fieldW, 26f),
                localBuf, 10, isTp ? "fx-order-tp" : "fx-order-sl");
            cur = ParsePrice(localBuf);

            // 与原版风 UI 一致：短按 0.01，之后 0.05 / 0.5 / 5 / 20 逐档加速
            HoldFloat(minusRect, plusRect, isTp ? 2 : 3, delegate(int dir, float step)
            {
                float baseValue = cur > 0f ? cur : (sel != null ? sel.rate : 0f);
                cur = Mathf.Max(0f, baseValue + dir * step);
                localBuf = cur.ToString("F2");
            });

            buf = localBuf;
            value = cur;

            float pct = PctVs(sel, cur, isTp);
            if (Mathf.Abs(pct) >= 0.005f)
            {
                FxTheme.Tiny(new Rect(r.x + r.width - pctW, rowY + 5f, pctW, 16f),
                    pct.ToString("+0.0;-0.0;0.0") + "%",
                    pct >= 0f ? FxTheme.Long : FxTheme.Short, TextAnchor.MiddleRight);
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
            uiAllIn = false;
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
                "RimForex_UiDetails".Translate(),
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
            else DrawEventLogTab(content, fx);
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
            Rect tipLocal = default(Rect);
            string tipText = null;
            if (uiChartPosition != null && !account.positions.Contains(uiChartPosition))
            {
                uiChartPosition = null;
            }
            GUI.BeginGroup(list);
            for (int i = 0; i < account.positions.Count; i++)
            {
                FxPosition p = account.positions[i];
                if (p == null) continue;
                float yy = i * rowH - uiBottomScroll;
                if (yy + rowH < 0f || yy > list.height) continue;

                Rect row = new Rect(0f, yy, list.width, rowH);
                if (i % 2 == 1) FxTheme.Fill(row, new Color32(18, 23, 30, 255));

                if (FxWidgets.Toggle(new Rect(4f, yy + 7f, 14f, 14f),
                        uiChartPosition == p))
                {
                    uiChartPosition = uiChartPosition == p ? null : p;
                    if (uiChartPosition != null)
                    {
                        selectedFactionId = p.currencyFactionLoadID;
                    }
                }
                if (uiChartPosition == p)
                {
                    FxTheme.Fill(new Rect(0f, yy, 3f, rowH), FxTheme.Accent);
                }

                string dir = p.isLong ? "RimForex_Long".Translate() : "RimForex_Short".Translate();
                FxTheme.Small(new Rect(26f, yy + 6f, 110f, 18f),
                    Truncate(p.currencyName + "  " + dir, 20),
                    p.isLong ? FxTheme.Long : FxTheme.Short);

                FxTheme.Tiny(new Rect(150f, yy + 7f, 80f, 16f),
                    "×" + p.units.ToString("N0"), FxTheme.Muted);
                FxTheme.Small(new Rect(232f, yy + 6f, 100f, 18f),
                    p.Pnl.ToString("+0;-0;0"), FxTheme.PnlColor(p.Pnl),
                    TextAnchor.MiddleRight);

                Rect edit = new Rect(26f, yy, list.width - 98f, rowH);
                if (Mouse.IsOver(edit))
                {
                    tipLocal = edit;
                    tipText = "RimForex_PosRowTip".Translate(
                        p.units,
                        p.entryRate.ToString("F2"),
                        p.markRate.ToString("F2"),
                        p.leverage.ToString("F0"),
                        p.Pnl.ToString("+0;-0;0"),
                        p.MarketPnl.ToString("+0;-0;0"),
                        p.SpreadCost.ToString("F0"))
                        + "\n\n" + "RimForex_UiPositionEditTip".Translate();
                }
                if (Widgets.ButtonInvisible(edit))
                {
                    Find.WindowStack.Add(new Window_FxTpSl(Fx, account, p));
                }

                bool canClose = Fx != null && Fx.CanClose(p);
                string closeLabel = canClose
                    ? "RimForex_Close".Translate()
                    : ((p.closableAtTick - GenTicks.TicksGame) / 60f).ToString("0.0") + "s";
                if (FxWidgets.Button(new Rect(list.width - 68f, yy + 3f, 60f, 24f),
                        closeLabel, canClose))
                {
                    ClosePosition(p);
                }
                if (!canClose)
                {
                    Rect closeTip = new Rect(list.width - 68f, yy + 3f, 60f, 24f);
                    if (Mouse.IsOver(closeTip))
                    {
                        tipLocal = closeTip;
                        tipText = "RimForex_UiLockedTip".Translate();
                    }
                }
                FxTheme.HLine(0f, list.width, yy + rowH - 1f, FxTheme.Line);
            }
            GUI.EndGroup();

            if (tipText != null)
            {
                FxTooltip.Tip(new Rect(list.x + tipLocal.x, list.y + tipLocal.y,
                    tipLocal.width, tipLocal.height), tipText);
            }

            float frac = content.height / Mathf.Max(content.height, viewH);
            float maxScroll = Mathf.Max(0f, viewH - content.height);
            FxWidgets.ScrollBar(new Rect(content.xMax - 8f, content.y, 6f, content.height),
                maxScroll > 0.01f ? uiBottomScroll / maxScroll : 0f, frac);
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
            float maxScroll = Mathf.Max(0f, viewH - content.height);
            FxWidgets.ScrollBar(new Rect(content.xMax - 8f, content.y, 6f, content.height),
                maxScroll > 0.01f ? uiBottomScroll / maxScroll : 0f, frac);
        }

        private void DrawEventLogTab(Rect content, FxGameComponent fx)
        {
            List<FxEventLogEntry> log = fx != null ? fx.eventLog : null;
            if (log == null || log.Count == 0)
            {
                FxWidgets.Center(content, "RimForex_UiNoEvents".Translate(), FxTheme.Muted);
                return;
            }

            float rowH = 38f;
            float viewH = log.Count * rowH;
            HandleUiScroll(content, ref uiBottomScroll, viewH);

            Rect list = new Rect(content.x, content.y, content.width - 10f, content.height);
            GUI.BeginGroup(list);
            for (int n = 0; n < log.Count; n++)
            {
                FxEventLogEntry entry = log[log.Count - 1 - n];
                if (entry == null) continue;
                float yy = n * rowH - uiBottomScroll;
                if (yy + rowH < 0f || yy > list.height) continue;

                if (n % 2 == 1)
                {
                    FxTheme.Fill(new Rect(0f, yy, list.width, rowH),
                        new Color32(18, 23, 30, 255));
                }

                FxTheme.Small(new Rect(8f, yy + 4f, list.width - 170f, 18f),
                    entry.label, FxTheme.TextColor);
                if (!string.IsNullOrEmpty(entry.detail))
                {
                    FxTheme.Tiny(new Rect(8f, yy + 21f, list.width - 170f, 14f),
                        entry.detail, FxTheme.Muted);
                }

                string when = GenDate.DateFullStringAt(
                    GenDate.TickGameToAbs(entry.tick), Vector2.zero);
                FxTheme.Tiny(new Rect(list.width - 156f, yy + 10f, 148f, 16f),
                    when, FxTheme.Muted, TextAnchor.MiddleRight);
                FxTheme.HLine(0f, list.width, yy + rowH - 1f, FxTheme.Line);
            }
            GUI.EndGroup();

            float frac = content.height / Mathf.Max(content.height, viewH);
            float maxScroll = Mathf.Max(0f, viewH - content.height);
            FxWidgets.ScrollBar(new Rect(content.xMax - 8f, content.y, 6f, content.height),
                maxScroll > 0.01f ? uiBottomScroll / maxScroll : 0f, frac);
        }

        private static void HandleUiScroll(Rect content, ref float scroll, float viewH)
        {
            Event e = Event.current;
            if (Mouse.IsOver(content) && e.type == EventType.ScrollWheel)
            {
                scroll += e.delta.y * 24f;
                e.Use();
            }
            scroll = Mathf.Clamp(scroll, 0f, Mathf.Max(0f, viewH - content.height));
        }

    }
}
