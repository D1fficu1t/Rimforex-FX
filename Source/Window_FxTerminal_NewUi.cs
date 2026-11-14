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

        private float uiLeftRatio = 0.27f;
        private float uiRightRatio = 0.25f;
        private float uiBottomRatio = 0.22f;
        /// <summary>正在拖的分隔条：0 无，1 左，2 右，3 底。</summary>
        private int uiDragSplit;

        private void ResetUiLayout()
        {
            uiLeftRatio = 0.27f;
            uiRightRatio = 0.25f;
            uiBottomRatio = 0.22f;
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
            DrawNewOrderPlaceholder(rightRect);
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

            int dateAbs = GenDate.TickGameToAbs(GenTicks.TicksGame);
            string date = GenDate.DateFullStringAt(dateAbs, Vector2.zero);
            FxTheme.Tiny(new Rect(bar.xMax - 760f, bar.y + 16f, 300f, 18f),
                date, FxTheme.Muted, TextAnchor.MiddleRight);

            float right = bar.xMax - 12f;
            const float bh = 26f;
            float by = bar.y + 9f;

            Rect close = new Rect(right - 28f, by, 28f, bh);
            if (FxWidgets.Button(close, "×"))
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
                FxAccount acc = ColonyAccountForUi();
                if (acc != null && fx != null)
                {
                    Find.WindowStack.Add(new Window_FxLoans(fx, acc, terminal));
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
            float colW = (r.width - pad * 2f) / 5f;
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
            FxTheme.HLine(r.x, r.xMax, r.yMax - 1f, FxTheme.Line);
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
                FxTheme.Tiny(new Rect(x + 6f, y + 18f, w * 0.54f, 14f),
                    c.factionName + (status.Length > 0 ? "  " + status : ""),
                    status.Length > 0 ? FxTheme.Warning : FxTheme.Muted);

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
            FxWidgets.SectionHeader(r, "RimForex_UiChart".Translate());

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

            Rect plot = new Rect(r.x + 10f, r.y + 40f,
                r.width - 76f, r.height - 76f);
            if (plot.height < 80f) return;
            if (selectedFactionId < 0)
            {
                FxWidgets.Center(plot, "RimForex_UiSelectCurrency".Translate(), FxTheme.Muted);
                return;
            }

            List<FxCandle> view = fx.market.GetCandles(selectedFactionId, 60, chartSpan);
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

            float last = view[view.Count - 1].close;
            float lastY = plot.y + (max - last) / (max - min) * plot.height;
            FxTheme.HLine(plot.x, plot.xMax, lastY, FxTheme.Warning);
            Rect tag = new Rect(plot.xMax + 4f, lastY - 9f, 58f, 18f);
            FxTheme.Fill(tag, FxTheme.Warning);
            FxTheme.Tiny(tag, last.ToString("F2"), new Color32(20, 20, 20, 255),
                TextAnchor.MiddleCenter);

            int dateAbs = GenDate.TickGameToAbs(GenTicks.TicksGame);
            FxTheme.Tiny(new Rect(plot.x, plot.yMax + 4f, 240f, 16f),
                GenDate.DateFullStringAt(dateAbs, Vector2.zero), FxTheme.Muted);
        }

        private void DrawNewOrderPlaceholder(Rect r)
        {
            FxWidgets.Panel(r);
            FxWidgets.SectionHeader(r, "RimForex_UiOrder".Translate());

            FxTheme.Small(new Rect(r.x + 12f, r.y + 44f, r.width - 24f, 22f),
                "RimForex_UiOrderWip".Translate(), FxTheme.TextColor);
            FxTheme.Tiny(new Rect(r.x + 12f, r.y + 70f, r.width - 24f, 60f),
                "RimForex_UiOrderWipTip".Translate(),
                FxTheme.Muted);
        }

        private void DrawNewBottom(Rect r, FxGameComponent fx)
        {
            FxWidgets.Panel(r);
            float tx = r.x + 12f;
            string[] tabs = { "RimForex_UiPositions".Translate(), "RimForex_UiHistory".Translate(), "RimForex_UiLog".Translate() };
            for (int i = 0; i < tabs.Length; i++)
            {
                Rect tab = new Rect(tx, r.y + 2f, 74f, 28f);
                FxWidgets.Tab(tab, tabs[i], i == 0);
                tx += 76f;
            }
            FxTheme.HLine(r.x, r.xMax, r.y + 30f, FxTheme.Line);

            int count = account != null && account.positions != null
                ? account.positions.Count : 0;
            FxTheme.Tiny(new Rect(r.x + 14f, r.y + 40f, r.width - 28f, 18f),
                "RimForex_UiBottomHint".Translate(count.ToString()), FxTheme.Muted);
        }
    }
}
