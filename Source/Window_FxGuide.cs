using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 新手教程（0.3.0）。
    ///
    /// 作者要求：**整合进交易终端、常驻**，不是首次弹一次就没了的提示。
    /// 所以它是一个随时能重新打开的窗口，入口在终端底部。
    ///
    /// 内容作者已指定五块：什么时候做多 / 做空 / 为什么会爆仓穿仓 /
    /// K 线怎么看 / 小白怎么盈利。另外补了借贷与术语两节。
    ///
    /// 文风要求（作者定）：**简洁明了，不写技术细节**。
    /// </summary>
    public class Window_FxGuide : Window
    {
        private Vector2 scroll;
        private const float Pad = 16f;
        private const float GapAfterSection = 18f;

        public Window_FxGuide()
        {
            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(780f, 660f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            float width = inRect.width - 24f;
            float height = CalcHeight(width);

            Rect view = new Rect(0f, 0f, width, height);
            Widgets.BeginScrollView(inRect, ref scroll, view);

            float y = 0f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, width, 32f), "RimForex_GuideTitle".Translate());
            Text.Font = GameFont.Small;
            y += 36f;

            y = Section(view, y, width, null, "RimForex_GuideIntro");
            y = Section(view, y, width, "RimForex_GuideLongTitle", "RimForex_GuideLong");
            y = Section(view, y, width, "RimForex_GuideShortTitle", "RimForex_GuideShort");
            y = Section(view, y, width, "RimForex_GuideLiquidationTitle", "RimForex_GuideLiquidation");

            // K 线一节：文字 + 一张示意图
            y = Section(view, y, width, "RimForex_GuideCandleTitle", "RimForex_GuideCandle");
            DrawCandleDiagram(view, ref y, width);

            y = Section(view, y, width, "RimForex_GuideProfitTitle", "RimForex_GuideProfit");
            y = Section(view, y, width, "RimForex_GuideLoanTitle", "RimForex_GuideLoan");
            y = Section(view, y, width, "RimForex_GuideGlossaryTitle", "RimForex_GuideGlossary");

            Widgets.EndScrollView();
        }

        /// <summary>画一节（可选标题 + 正文），返回下一节的 y。</summary>
        private float Section(Rect view, float y, float width, string titleKey, string bodyKey)
        {
            if (titleKey != null)
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, y, width, 28f), titleKey.Translate());
                Text.Font = GameFont.Small;
                y += 30f;
            }

            string body = bodyKey.Translate();
            float h = Text.CalcHeight(body, width);
            Widgets.Label(new Rect(0f, y, width, h), body);
            return y + h + GapAfterSection;
        }

        /// <summary>
        /// K 线示意：四根蜡烛，现画。
        /// 不依赖任何贴图 —— 作者不希望为教程专门做图。
        /// </summary>
        private void DrawCandleDiagram(Rect view, ref float y, float width)
        {
            const float H = 120f;
            Rect box = new Rect(0f, y, Mathf.Min(width, 360f), H);
            Widgets.DrawBoxSolid(box, new Color(0.12f, 0.13f, 0.16f));
            Widgets.DrawBox(box, 1);

            // (开盘, 收盘, 最高, 最低)，都是 0~1 的相对高度
            float[][] candles =
            {
                new[] { 0.55f, 0.70f, 0.78f, 0.50f },
                new[] { 0.70f, 0.62f, 0.74f, 0.58f },
                new[] { 0.62f, 0.80f, 0.88f, 0.60f },
                new[] { 0.80f, 0.72f, 0.84f, 0.66f },
            };

            Color up = new Color(0.45f, 0.80f, 0.50f);
            Color down = new Color(0.88f, 0.45f, 0.45f);
            const float BodyW = 22f;
            float step = (box.width - 40f) / candles.Length;

            for (int i = 0; i < candles.Length; i++)
            {
                float open = candles[i][0];
                float close = candles[i][1];
                float high = candles[i][2];
                float low = candles[i][3];
                bool rising = close >= open;

                float cx = box.x + 20f + i * step + step * 0.5f;
                float top = box.yMax - 12f - high * (H - 24f);
                float bottom = box.yMax - 12f - low * (H - 24f);
                float bodyTop = box.yMax - 12f - Mathf.Max(open, close) * (H - 24f);
                float bodyBottom = box.yMax - 12f - Mathf.Min(open, close) * (H - 24f);

                Color c = rising ? up : down;
                // 上下影线
                Widgets.DrawBoxSolid(new Rect(cx - 1f, top, 2f, bottom - top), c);
                // 实体
                Widgets.DrawBoxSolid(new Rect(cx - BodyW * 0.5f, bodyTop, BodyW,
                    Mathf.Max(2f, bodyBottom - bodyTop)), c);
            }

            y = box.yMax + GapAfterSection;
        }

        private float CalcHeight(float width)
        {
            float h = 36f;
            h += Measure(width, null, "RimForex_GuideIntro");
            h += Measure(width, "RimForex_GuideLongTitle", "RimForex_GuideLong");
            h += Measure(width, "RimForex_GuideShortTitle", "RimForex_GuideShort");
            h += Measure(width, "RimForex_GuideLiquidationTitle", "RimForex_GuideLiquidation");
            h += Measure(width, "RimForex_GuideCandleTitle", "RimForex_GuideCandle");
            h += 120f + GapAfterSection;
            h += Measure(width, "RimForex_GuideProfitTitle", "RimForex_GuideProfit");
            h += Measure(width, "RimForex_GuideLoanTitle", "RimForex_GuideLoan");
            h += Measure(width, "RimForex_GuideGlossaryTitle", "RimForex_GuideGlossary");
            return h + 40f;
        }

        private static float Measure(float width, string titleKey, string bodyKey)
        {
            float h = 0f;
            if (titleKey != null) h += 30f;
            Text.Font = GameFont.Small;
            h += Text.CalcHeight(bodyKey.Translate(), width);
            return h + GapAfterSection;
        }
    }
}
