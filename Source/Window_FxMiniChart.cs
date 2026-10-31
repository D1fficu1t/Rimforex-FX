using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 悬浮窗里点「K」打开的小 K 线窗口（0.3.1）。
    ///
    /// 只画图：标题 + 蜡烛 + 价格轴 + 最新价 + 该货币持仓的开仓线。
    /// 完整功能（周期切换、持仓线、爆仓线、十字光标）仍在终端里。
    /// </summary>
    public class Window_FxMiniChart : Window
    {
        private readonly FxGameComponent fx;
        private readonly FxAccount account;

        public int FactionId { get; private set; }

        private FxChartSpan span = FxChartSpan.Hour;
        private bool showEntry = true;
        private bool showBreakeven = true;
        private bool showStopOut = true;

        public Window_FxMiniChart(FxGameComponent fx, FxAccount account, int factionId)
        {
            this.fx = fx;
            this.account = account;
            FactionId = factionId;

            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(460f, 300f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (fx == null || fx.market == null) { Close(); return; }

            // 终端断电后这个窗口必须自己消失（作者 2026-10-31 实测：断电还能看）
            if (!AnyTerminalUsable()) { Close(); return; }
            FxCurrency cur = fx.market.Get(FactionId);
            if (cur == null) { Close(); return; }

            float lhS = Text.LineHeightOf(GameFont.Small);

            // ---- 标题 + 最新价 ----
            Text.Font = GameFont.Small;
            string title = cur.PairLabel;
            Widgets.Label(new Rect(0f, 0f, inRect.width - 90f, lhS + 2f), title);
            float tw = Text.CalcSize(title).x;
            GUI.color = new Color(0.90f, 0.93f, 0.96f);
            Widgets.Label(new Rect(tw + 10f, 0f, 110f, lhS + 2f), cur.rate.ToString("F2"));
            GUI.color = Color.white;

            // ---- 左侧竖排开关（与终端一致）----
            float plotTop = lhS + 6f;
            const float StripW = 52f;
            Rect strip = new Rect(0f, plotTop, StripW, Mathf.Max(80f, inRect.height - plotTop));
            DrawSideButtons(strip);

            // ---- 图 ----
            Rect plot = new Rect(StripW + 6f, plotTop,
                inRect.width - StripW - 6f, inRect.height - plotTop);
            if (plot.height < 60f) return;
            Widgets.DrawBoxSolid(plot, new Color(0.10f, 0.11f, 0.13f, 1f));

            List<FxCandle> candles = fx.market.GetCandles(FactionId, 40, span);
            if (candles == null || candles.Count == 0)
            {
                Widgets.Label(plot.ContractedBy(6f), "RimForex_ChartNoData".Translate());
                return;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < candles.Count; i++)
            {
                if (candles[i].low < min) min = candles[i].low;
                if (candles[i].high > max) max = candles[i].high;
            }
            if (max - min < 0.0001f) { max += 0.5f; min -= 0.5f; }

            Rect area = new Rect(plot.x + 4f, plot.y + 6f, plot.width - 52f, plot.height - 16f);
            if (area.width < 20f || area.height < 20f) return;

            Color grid = new Color(1f, 1f, 1f, 0.07f);
            for (int g = 0; g <= 4; g++)
            {
                float gy = area.y + area.height * g / 4f;
                Widgets.DrawBoxSolid(new Rect(area.x, gy, area.width, 1f), grid);
            }

            int n = candles.Count;
            float slot = area.width / n;
            float bodyW = Mathf.Max(2f, slot * 0.62f);
            Color up = new Color(0.42f, 0.82f, 0.48f, 1f);
            Color down = new Color(0.90f, 0.38f, 0.34f, 1f);

            for (int i = 0; i < n; i++)
            {
                FxCandle c = candles[i];
                float cx = area.x + slot * i + slot * 0.5f;
                Color col = c.close >= c.open ? up : down;

                float yHigh = PriceToY(c.high, min, max, area);
                float yLow = PriceToY(c.low, min, max, area);
                Widgets.DrawBoxSolid(new Rect(cx - 0.5f, yHigh, 1f, Mathf.Max(1f, yLow - yHigh)), col);

                float yOpen = PriceToY(c.open, min, max, area);
                float yClose = PriceToY(c.close, min, max, area);
                float top = Mathf.Min(yOpen, yClose);
                Widgets.DrawBoxSolid(new Rect(cx - bodyW * 0.5f, top,
                    bodyW, Mathf.Max(1f, Mathf.Abs(yClose - yOpen))), col);
            }

            // 三条参考线：开仓 / 盈亏平衡 / 爆仓（只取该货币最新一笔持仓）
            //
            // 全部照抄终端 Window_FxTerminal.DrawPositionLines / DrawBreakevenLine /
            // DrawStopOutLine 的换算，保证两边画在同一个价位上：
            //   蜡烛是**中间价**，而持仓的 entryRate 是**方向价**，
            //   必须先按入场时的半价差换回中间价，否则线会整体偏半个点差。
            FxPosition pos = LatestPosition();
            if (pos != null && pos.entrySpreadRate > 0.0001f)
            {
                float units = Mathf.Abs((float)pos.units);
                float half = pos.entrySpreadRate * 0.5f;
                float denom = pos.isLong ? (1f - half) : (1f + half);
                if (denom > 0.0001f)
                {
                    float mid0 = pos.entryMid > 0f ? pos.entryMid : pos.entryRate / denom;

                    if (showEntry)
                    {
                        DrawLine(area, min, max, mid0, new Color(0.85f, 0.85f, 0.55f, 0.85f));
                    }
                    if (showBreakeven)
                    {
                        float beMid = mid0 * (pos.isLong ? (1f + half) : (1f - half)) / denom;
                        DrawLine(area, min, max, beMid, new Color(0.55f, 0.72f, 0.98f, 0.95f));
                    }
                    if (showStopOut && units > 0.0001f)
                    {
                        float baseM = pos.BaseMargin;
                        if (baseM < 0.01f) baseM = pos.margin;
                        float dist = ((1f - FxGameComponent.StopOutFraction) * baseM + pos.addedMargin) / units;
                        float markStop = pos.isLong ? pos.entryRate - dist : pos.entryRate + dist;
                        // 爆仓线经常落在可视区外（100x 时距现价只有 0.7%），
                        // 贴边画一条，别让它凭空消失 —— 与终端一致
                        DrawLine(area, min, max, markStop / denom,
                            new Color(1f, 0.45f, 0.40f, 0.9f), true);
                    }
                }
            }

            // 价格轴
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            for (int g = 0; g <= 4; g++)
            {
                float p = max - (max - min) * g / 4f;
                float gy = area.y + area.height * g / 4f;
                Widgets.Label(new Rect(area.xMax + 4f, gy - 8f, 46f, 16f), p.ToString("F2"));
            }
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        /// <summary>任意地图上是否存在通电的终端。</summary>
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

        private static void DrawLine(Rect area, float min, float max, float price, Color col,
            bool clampToEdge = false)
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
            Widgets.DrawBoxSolid(new Rect(area.x, y, area.width, 1f), col);
        }

        private void DrawSideButtons(Rect strip)
        {
            float bh = 24f;
            float y = strip.y;

            GUI.color = span == FxChartSpan.Minute ? new Color(0.70f, 0.80f, 0.95f) : Color.white;
            if (Widgets.ButtonText(new Rect(strip.x, y, strip.width, bh), "RimForex_SpanMinuteShort".Translate()))
            {
                span = FxChartSpan.Minute;
            }
            GUI.color = Color.white;
            y += bh + 4f;

            GUI.color = span == FxChartSpan.Hour ? new Color(0.70f, 0.80f, 0.95f) : Color.white;
            if (Widgets.ButtonText(new Rect(strip.x, y, strip.width, bh), "RimForex_SpanHour".Translate()))
            {
                span = FxChartSpan.Hour;
            }
            GUI.color = Color.white;
            y += bh + 4f;

            GUI.color = span == FxChartSpan.Day ? new Color(0.70f, 0.80f, 0.95f) : Color.white;
            if (Widgets.ButtonText(new Rect(strip.x, y, strip.width, bh), "RimForex_SpanDay".Translate()))
            {
                span = FxChartSpan.Day;
            }
            GUI.color = Color.white;
            y += bh + 8f;

            if (Widgets.ButtonText(new Rect(strip.x, y, strip.width, bh),
                    showEntry ? "RimForex_LineOnEntry".Translate() : "RimForex_LineOffEntry".Translate()))
            {
                showEntry = !showEntry;
            }
            y += bh + 4f;

            if (Widgets.ButtonText(new Rect(strip.x, y, strip.width, bh),
                    showBreakeven ? "RimForex_LineOnBe".Translate() : "RimForex_LineOffBe".Translate()))
            {
                showBreakeven = !showBreakeven;
            }
            y += bh + 4f;

            if (Widgets.ButtonText(new Rect(strip.x, y, strip.width, bh),
                    showStopOut ? "RimForex_LineOnSo".Translate() : "RimForex_LineOffSo".Translate()))
            {
                showStopOut = !showStopOut;
            }
        }

        private static float PriceToY(float price, float min, float max, Rect area)
        {
            float t = Mathf.InverseLerp(min, max, price);
            return area.yMax - t * area.height;
        }
    }
}
