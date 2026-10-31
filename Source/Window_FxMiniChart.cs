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

            // ---- 图 ----
            Rect plot = new Rect(0f, lhS + 6f, inRect.width, inRect.height - lhS - 6f);
            if (plot.height < 60f) return;
            Widgets.DrawBoxSolid(plot, new Color(0.10f, 0.11f, 0.13f, 1f));

            List<FxCandle> candles = fx.market.GetCandles(FactionId, 40, FxChartSpan.Hour);
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

            // 该货币持仓的开仓线（只画最新一笔）
            if (account != null)
            {
                for (int i = account.positions.Count - 1; i >= 0; i--)
                {
                    var p = account.positions[i];
                    if (p == null || p.currencyFactionLoadID != FactionId) continue;
                    if (p.entryRate >= min && p.entryRate <= max)
                    {
                        float ey = PriceToY(p.entryRate, min, max, area);
                        Widgets.DrawBoxSolid(new Rect(area.x, ey, area.width, 1f),
                            new Color(0.85f, 0.85f, 0.55f, 0.8f));
                    }
                    break;
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

        private static float PriceToY(float price, float min, float max, Rect area)
        {
            float t = Mathf.InverseLerp(min, max, price);
            return area.yMax - t * area.height;
        }
    }
}
