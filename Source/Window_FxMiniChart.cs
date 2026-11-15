using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 小 K 线窗口（0.3.1，D2 换新界面）。
    /// 只画图：周期、三条参考线、最新价、十字光标与悬停详情。
    /// </summary>
    public class Window_FxMiniChart : Window
    {
        private readonly FxGameComponent fx;
        private readonly FxAccount account;

        public int FactionId { get; private set; }

        private FxChartSpan span = FxChartSpan.Hour;
        private int candleCount = 40;
        private bool showEntry = true;
        private bool showBreakeven = true;
        private bool showStopOut = true;

        public Window_FxMiniChart(FxGameComponent fx, FxAccount account, int factionId)
        {
            this.fx = fx;
            this.account = account;
            FactionId = factionId;

            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = false;
            FxWindowChrome.Prepare(this);
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(540f, 360f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (fx == null || fx.market == null) { Close(); return; }
            if (!AnyTerminalUsable()) { Close(); return; }
            FxCurrency cur = fx.market.Get(FactionId);
            if (cur == null) { Close(); return; }

            Rect r = FxWindowChrome.Draw(this, inRect, cur.PairLabel);
            float lhS = FxTheme.LhS;

            // 最新价与重置
            FxTheme.Medium(new Rect(r.x, r.y, 150f, 24f),
                cur.rate.ToString("F2"), FxTheme.TextColor);
            Rect resetBtn = new Rect(r.xMax - 82f, r.y, 82f, 24f);
            if (FxWidgets.Button(resetBtn, "RimForex_ChartReset".Translate()))
            {
                candleCount = 40;
            }
            FxTooltip.Tip(resetBtn, "RimForex_MiniResetTip".Translate());

            float plotTop = lhS + 8f;
            const float StripW = 56f;
            Rect strip = new Rect(r.x, r.y + plotTop, StripW,
                Mathf.Max(80f, r.height - plotTop));
            DrawSideButtons(strip);

            Rect plot = new Rect(r.x + StripW + 6f, r.y + plotTop,
                r.width - StripW - 6f, r.height - plotTop);
            if (plot.height < 60f) return;
            FxTheme.Fill(plot, FxTheme.WindowBg);
            FxTheme.Border(plot, FxTheme.Line);

            List<FxCandle> candles = fx.market.GetCandles(FactionId, candleCount, span);
            if (candles == null || candles.Count == 0)
            {
                FxWidgets.Center(plot.ContractedBy(6f),
                    "RimForex_ChartNoData".Translate(), FxTheme.Muted);
                return;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < candles.Count; i++)
            {
                if (candles[i].low < min) min = candles[i].low;
                if (candles[i].high > max) max = candles[i].high;
            }
            if (max - min < 0.0001f) { max += 0.5f; min -= 0.5f; }

            Rect area = new Rect(plot.x + 6f, plot.y + 8f,
                plot.width - 58f, plot.height - 22f);
            if (area.width < 20f || area.height < 20f) return;

            Color grid = new Color32(27, 33, 41, 255);
            for (int g = 0; g <= 4; g++)
            {
                float gy = area.y + area.height * g / 4f;
                FxTheme.HLine(area.x, area.xMax, gy, grid);
            }

            Event eScroll = Event.current;
            if (eScroll != null && eScroll.type == EventType.ScrollWheel
                && plot.Contains(eScroll.mousePosition))
            {
                int delta = Mathf.RoundToInt(eScroll.delta.y) * 5;
                int changed = Mathf.Clamp(candleCount + delta, 10, 120);
                if (changed != candleCount)
                {
                    candleCount = changed;
                    eScroll.Use();
                }
            }

            int n = candles.Count;
            float slot = area.width / n;
            float bodyW = Mathf.Max(2f, slot * 0.62f);
            for (int i = 0; i < n; i++)
            {
                FxCandle c = candles[i];
                float cx = area.x + slot * i + slot * 0.5f;
                Color col = c.close >= c.open ? FxTheme.Long : FxTheme.Short;
                float yHigh = PriceToY(c.high, min, max, area);
                float yLow = PriceToY(c.low, min, max, area);
                FxTheme.VLine(cx, yHigh, yLow, col);
                float yOpen = PriceToY(c.open, min, max, area);
                float yClose = PriceToY(c.close, min, max, area);
                float top = Mathf.Min(yOpen, yClose);
                FxTheme.Fill(new Rect(cx - bodyW * 0.5f, top, bodyW,
                    Mathf.Max(1f, Mathf.Abs(yClose - yOpen))), col);
            }

            float lastRate = cur.rate > 0.001f ? cur.rate : candles[n - 1].close;
            if (lastRate >= min && lastRate <= max)
            {
                float curY = PriceToY(lastRate, min, max, area);
                FxTheme.HLine(area.x, area.xMax, curY, FxTheme.Warning);
                FxTheme.Fill(new Rect(area.xMax + 4f, curY - 8f, 48f, 16f),
                    FxTheme.Warning);
                FxTheme.Tiny(new Rect(area.xMax + 4f, curY - 8f, 48f, 16f),
                    lastRate.ToString("F2"), new Color32(20, 20, 20, 255),
                    TextAnchor.MiddleCenter);
            }

            FxPosition pos = LatestPosition();
            if (pos != null)
            {
                float units = Mathf.Abs((float)pos.units);
                float spreadRate = pos.entrySpreadRate > 0.0001f
                    ? pos.entrySpreadRate : FxMarket.SpreadRateFor(cur);
                float half = spreadRate * 0.5f;
                float denom = pos.isLong ? (1f - half) : (1f + half);
                if (denom > 0.0001f)
                {
                    float mid0 = pos.entryMid > 0f ? pos.entryMid : pos.entryRate / denom;
                    if (showEntry)
                    {
                        DrawLine(area, min, max, mid0,
                            new Color(FxTheme.Warning.r, FxTheme.Warning.g,
                                FxTheme.Warning.b, 0.75f),
                            false, 1f, true);
                    }
                    if (showBreakeven)
                    {
                        float beMid = mid0 * (pos.isLong ? (1f + half) : (1f - half)) / denom;
                        DrawLine(area, min, max, beMid,
                            new Color(FxTheme.Accent.r, FxTheme.Accent.g,
                                FxTheme.Accent.b, 0.9f), false, 2f);
                    }
                    if (showStopOut && units > 0.0001f)
                    {
                        float baseM = pos.BaseMargin;
                        if (baseM < 0.01f) baseM = pos.margin;
                        float dist = ((1f - FxGameComponent.StopOutFraction) * baseM
                            + pos.addedMargin) / units;
                        float markStop = pos.isLong
                            ? pos.entryRate - dist : pos.entryRate + dist;
                        float soMid = markStop / denom;
                        DrawLine(area, min, max, soMid,
                            new Color(FxTheme.Short.r, FxTheme.Short.g,
                                FxTheme.Short.b, 0.9f),
                            true, 1f, true);

                        if (soMid < min || soMid > max)
                        {
                            bool above = soMid > max;
                            float edgeY = above ? area.y : area.yMax;
                            float offPct = (soMid / cur.rate - 1f) * 100f;
                            string txt = "RimForex_ChartStopOutOff".Translate(
                                soMid.ToString("F2"),
                                offPct.ToString("+0.00;-0.00;0.00"));
                            FxTheme.Tiny(new Rect(area.x + 2f,
                                above ? edgeY + 1f : edgeY - 15f,
                                area.width - 4f, 14f), txt, FxTheme.Short);
                        }
                    }
                }
            }

            for (int g = 0; g <= 4; g++)
            {
                float gy = area.y + area.height * g / 4f;
                float p = max - (max - min) * g / 4f;
                FxTheme.Tiny(new Rect(area.xMax + 4f, gy - 8f, 50f, 16f),
                    p.ToString("F2"), FxTheme.Muted, TextAnchor.MiddleLeft);
            }

            Event e = Event.current;
            if (e != null && area.Contains(e.mousePosition))
            {
                Vector2 m = e.mousePosition;
                int idx = Mathf.Clamp((int)((m.x - area.x) / Mathf.Max(0.01f, slot)),
                    0, n - 1);
                float cx = area.x + slot * idx + slot * 0.5f;
                FxTheme.VLine(cx, area.y, area.yMax, new Color(1f, 1f, 1f, 0.3f));
                FxTheme.HLine(area.x, area.xMax, m.y, new Color(1f, 1f, 1f, 0.18f));

                float hoverPrice = Mathf.Lerp(max, min,
                    Mathf.Clamp01((m.y - area.y) / Mathf.Max(1f, area.height)));
                FxTheme.Fill(new Rect(area.xMax + 4f, m.y - 8f, 48f, 16f),
                    new Color32(40, 48, 60, 255));
                FxTheme.Tiny(new Rect(area.xMax + 4f, m.y - 8f, 48f, 16f),
                    hoverPrice.ToString("F2"), FxTheme.TextColor,
                    TextAnchor.MiddleCenter);

                FxCandle hc = candles[idx];
                string date = hc.startTick > 0
                    ? GenDate.DateFullStringAt(GenDate.TickGameToAbs(hc.startTick),
                        Vector2.zero) : "";
                string title = (hc.close >= hc.open
                    ? "RimForex_HoverUp" : "RimForex_HoverDown").Translate();
                if (date.Length > 0) title = date + "  " + title;
                string detail = "RimForex_UiCandleDetail".Translate(
                    hc.open.ToString("F2"), hc.high.ToString("F2"),
                    hc.low.ToString("F2"), hc.close.ToString("F2"));
                float pct = hc.open > 0.0001f
                    ? (hc.close / hc.open - 1f) * 100f : 0f;
                string range = "RimForex_HoverRange".Translate(
                    (hc.high - hc.low).ToString("F2"),
                    pct.ToString("+0.00;-0.00;0.00") + "%");

                float boxW = 300f;
                float bx = Mathf.Clamp(m.x + 14f, plot.x, plot.xMax - boxW - 4f);
                float by = Mathf.Clamp(m.y + 14f, plot.y + 4f, plot.yMax - 64f);
                Rect tip = new Rect(bx, by, boxW, 58f);
                FxTheme.Fill(tip, new Color32(18, 23, 30, 248));
                FxTheme.Border(tip, FxTheme.LineHi);
                FxTheme.Tiny(new Rect(tip.x + 8f, tip.y + 5f, tip.width - 16f, 16f),
                    title, FxTheme.TextColor);
                FxTheme.Tiny(new Rect(tip.x + 8f, tip.y + 21f, tip.width - 16f, 16f),
                    detail, FxTheme.Muted);
                FxTheme.Tiny(new Rect(tip.x + 8f, tip.y + 37f, tip.width - 16f, 16f),
                    range, pct >= 0f ? FxTheme.Long : FxTheme.Short);
            }
            FxTooltip.Draw();
        }

        private void DrawSideButtons(Rect strip)
        {
            float bh = 24f;
            float y = strip.y;
            if (FxWidgets.Button(new Rect(strip.x, y, strip.width, bh),
                    "RimForex_SpanMinuteShort".Translate(),
                    true, accent: span == FxChartSpan.Minute))
            {
                span = FxChartSpan.Minute;
            }
            y += bh + 4f;
            if (FxWidgets.Button(new Rect(strip.x, y, strip.width, bh),
                    "RimForex_SpanHour".Translate(),
                    true, accent: span == FxChartSpan.Hour))
            {
                span = FxChartSpan.Hour;
            }
            y += bh + 4f;
            if (FxWidgets.Button(new Rect(strip.x, y, strip.width, bh),
                    "RimForex_SpanDay".Translate(),
                    true, accent: span == FxChartSpan.Day))
            {
                span = FxChartSpan.Day;
            }
            y += bh + 8f;
            if (FxWidgets.Button(new Rect(strip.x, y, strip.width, bh),
                    (showEntry ? "RimForex_LineOnEntry" : "RimForex_LineOffEntry").Translate(),
                    true, accent: showEntry))
            {
                showEntry = !showEntry;
            }
            y += bh + 4f;
            if (FxWidgets.Button(new Rect(strip.x, y, strip.width, bh),
                    (showBreakeven ? "RimForex_LineOnBe" : "RimForex_LineOffBe").Translate(),
                    true, accent: showBreakeven))
            {
                showBreakeven = !showBreakeven;
            }
            y += bh + 4f;
            if (FxWidgets.Button(new Rect(strip.x, y, strip.width, bh),
                    (showStopOut ? "RimForex_LineOnSo" : "RimForex_LineOffSo").Translate(),
                    true, accent: showStopOut))
            {
                showStopOut = !showStopOut;
            }
        }

        private static bool AnyTerminalUsable()
        {
            var maps = Find.Maps;
            if (maps == null) return false;
            for (int i = 0; i < maps.Count; i++)
            {
                var buildings = maps[i].listerBuildings
                    .AllBuildingsColonistOfClass<Building_FXTradingTerminal>();
                foreach (var b in buildings)
                {
                    if (b != null && b.Usable) return true;
                }
            }
            return false;
        }

        private FxPosition LatestPosition()
        {
            if (account == null) return null;
            for (int i = account.positions.Count - 1; i >= 0; i--)
            {
                var p = account.positions[i];
                if (p != null && p.currencyFactionLoadID == FactionId) return p;
            }
            return null;
        }

        private static void DrawLine(Rect area, float min, float max, float price,
            Color col, bool clampToEdge = false, float thickness = 1f,
            bool dashed = false)
        {
            float y;
            if (price < min)
            {
                if (!clampToEdge) return;
                y = area.yMax - 0.5f;
            }
            else if (price > max)
            {
                if (!clampToEdge) return;
                y = area.y + 0.5f;
            }
            else
            {
                y = PriceToY(price, min, max, area);
            }
            if (dashed)
            {
                for (float x = area.x; x < area.xMax; x += 6f)
                {
                    FxTheme.Fill(new Rect(x, y, Mathf.Min(3f, area.xMax - x),
                        thickness), col);
                }
            }
            else
            {
                FxTheme.Fill(new Rect(area.x, y - (thickness - 1f) * 0.5f,
                    area.width, thickness), col);
            }
        }

        private static float PriceToY(float price, float min, float max, Rect area)
        {
            float t = Mathf.InverseLerp(min, max, price);
            return area.yMax - t * area.height;
        }
    }
}
