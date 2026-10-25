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
    /// ── 文风（作者 2026-10-25 定）──
    /// 这份教程的设定是**星际交易平台写给用户的宣传材料**，
    /// 所以正经的建议要用夸张的推销腔说，真话藏在彩蛋里：
    /// 「五、新手怎么盈利」先教人梭哈，下面一个小按钮写着
    /// "（老油条的忠告：别听他的）"，点开才是真话。
    ///
    /// 另外**不要泛滥使用破折号**、不要"先说清楚""一句话讲清"这种 AI 腔。
    /// </summary>
    public class Window_FxGuide : Window
    {
        private Vector2 scroll;
        private bool showHonest;
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
            Rect view = new Rect(0f, 0f, width, CalcHeight(width));
            Widgets.BeginScrollView(inRect, ref scroll, view);

            float y = 0f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, width, 32f), "RimForex_GuideTitle".Translate());
            Text.Font = GameFont.Small;
            y += 36f;

            y = Section(y, width, null, "RimForex_GuideIntro");
            y = Section(y, width, "RimForex_GuideLongTitle", "RimForex_GuideLong");
            y = Section(y, width, "RimForex_GuideShortTitle", "RimForex_GuideShort");
            y = Section(y, width, "RimForex_GuideLiquidationTitle", "RimForex_GuideLiquidation");

            y = Section(y, width, "RimForex_GuideCandleTitle", "RimForex_GuideCandle");
            DrawCandleDiagram(ref y, width);

            y = Section(y, width, "RimForex_GuideProfitTitle", "RimForex_GuideProfit");

            // ── 彩蛋：老油条的忠告 ──
            string eggLabel = "RimForex_GuideHonestButton".Translate();
            float eggW = Text.CalcSize(eggLabel).x + 24f;
            Rect egg = new Rect(0f, y, eggW, 24f);
            if (Widgets.ButtonText(egg, eggLabel))
            {
                showHonest = !showHonest;
            }
            y += 32f;

            if (showHonest)
            {
                y = Section(y, width, "RimForex_GuideHonestTitle", "RimForex_GuideHonest");
            }

            y = Section(y, width, "RimForex_GuideLoanTitle", "RimForex_GuideLoan");
            Section(y, width, "RimForex_GuideGlossaryTitle", "RimForex_GuideGlossary");

            Widgets.EndScrollView();
        }

        /// <summary>画一节（可选标题 + 正文），返回下一节的 y。</summary>
        private float Section(float y, float width, string titleKey, string bodyKey)
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
        /// K 线示意：四根蜡烛，现画。不依赖任何贴图 —— 教程不该为配图再去做资源。
        /// </summary>
        private void DrawCandleDiagram(ref float y, float width)
        {
            const float H = 120f;
            Rect box = new Rect(0f, y, Mathf.Min(width, 360f), H);
            Widgets.DrawBoxSolid(box, new Color(0.12f, 0.13f, 0.16f));
            Widgets.DrawBox(box, 1);

            // (开盘, 收盘, 最高, 最低)，0~1 相对高度
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
                Widgets.DrawBoxSolid(new Rect(cx - 1f, top, 2f, bottom - top), c);
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
            h += 32f;
            if (showHonest)
            {
                h += Measure(width, "RimForex_GuideHonestTitle", "RimForex_GuideHonest");
            }
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
