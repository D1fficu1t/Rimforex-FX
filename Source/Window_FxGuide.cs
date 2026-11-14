using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 新手教程（0.3.0，D2 换新界面）。
    /// 内容与语气保持不变，只换外壳、滚动与蜡烛示意图风格。
    /// </summary>
    public class Window_FxGuide : Window
    {
        private float scroll;
        private bool showHonest;
        private const float GapAfterSection = 18f;

        public Window_FxGuide()
        {
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;
            FxWindowChrome.Prepare(this);
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(820f, 680f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect r = FxWindowChrome.Draw(this, inRect,
                "RimForex_GuideTitle".Translate());
            float width = r.width - 12f;
            float contentH = CalcHeight(width);

            Event e = Event.current;
            if (Mouse.IsOver(r) && e.type == EventType.ScrollWheel)
            {
                scroll += e.delta.y * 28f;
                e.Use();
            }
            scroll = Mathf.Clamp(scroll, 0f, Mathf.Max(0f, contentH - r.height));

            GUI.BeginGroup(r);
            float y = -scroll;

            y = Section(y, width, null, "RimForex_GuideIntro");
            y = Section(y, width, "RimForex_GuideLongTitle", "RimForex_GuideLong");
            y = Section(y, width, "RimForex_GuideShortTitle", "RimForex_GuideShort");
            y = Section(y, width, "RimForex_GuideLiquidationTitle", "RimForex_GuideLiquidation");
            y = Section(y, width, "RimForex_GuideCandleTitle", "RimForex_GuideCandle");
            DrawCandleDiagram(ref y, width);
            y = Section(y, width, "RimForex_GuideProfitTitle", "RimForex_GuideProfit");

            string eggLabel = "RimForex_GuideHonestButton".Translate();
            float eggW = Text.CalcSize(eggLabel).x + 24f;
            Rect egg = new Rect(0f, y, eggW, 26f);
            if (FxWidgets.Button(egg, eggLabel))
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

            GUI.EndGroup();

            float frac = r.height / Mathf.Max(r.height, contentH);
            FxWidgets.ScrollBar(new Rect(r.xMax + 2f, r.y, 5f, r.height), scroll, frac);
        }

        private float Section(float y, float width, string titleKey, string bodyKey)
        {
            if (titleKey != null)
            {
                FxTheme.Medium(new Rect(0f, y, width, 26f), titleKey.Translate(),
                    FxTheme.TextColor);
                y += 28f;
            }

            string body = bodyKey.Translate();
            GameFont f0 = Text.Font;
            bool w0 = Text.WordWrap;
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float h = Text.CalcHeight(body, width);
            Text.Font = f0;
            Text.WordWrap = w0;

            FxTheme.Label(new Rect(0f, y, width, h), body, GameFont.Small,
                FxTheme.Muted, TextAnchor.UpperLeft, true);
            return y + h + GapAfterSection;
        }

        private void DrawCandleDiagram(ref float y, float width)
        {
            const float H = 120f;
            Rect box = new Rect(0f, y, Mathf.Min(width, 360f), H);
            FxTheme.Fill(box, FxTheme.PanelBg);
            FxTheme.Border(box, FxTheme.Line);

            float[][] candles =
            {
                new[] { 0.55f, 0.70f, 0.78f, 0.50f },
                new[] { 0.70f, 0.62f, 0.74f, 0.58f },
                new[] { 0.62f, 0.80f, 0.88f, 0.60f },
                new[] { 0.80f, 0.72f, 0.84f, 0.66f },
            };

            const float BodyW = 22f;
            float step = (box.width - 40f) / candles.Length;
            for (int i = 0; i < candles.Length; i++)
            {
                float open = candles[i][0];
                float close = candles[i][1];
                float high = candles[i][2];
                float low = candles[i][3];
                Color c = close >= open ? FxTheme.Long : FxTheme.Short;

                float cx = box.x + 20f + i * step + step * 0.5f;
                float top = box.yMax - 12f - high * (H - 24f);
                float bottom = box.yMax - 12f - low * (H - 24f);
                float bodyTop = box.yMax - 12f - Mathf.Max(open, close) * (H - 24f);
                float bodyBottom = box.yMax - 12f - Mathf.Min(open, close) * (H - 24f);

                FxTheme.VLine(cx, top, bottom, c);
                FxTheme.Fill(new Rect(cx - BodyW * 0.5f, bodyTop, BodyW,
                    Mathf.Max(2f, bodyBottom - bodyTop)), c);
            }

            y = box.yMax + GapAfterSection;
        }

        private float CalcHeight(float width)
        {
            float h = Measure(width, null, "RimForex_GuideIntro");
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
            return h + 30f;
        }

        private static float Measure(float width, string titleKey, string bodyKey)
        {
            float h = 0f;
            if (titleKey != null) h += 28f;
            GameFont f0 = Text.Font;
            bool w0 = Text.WordWrap;
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            h += Text.CalcHeight(bodyKey.Translate(), width);
            Text.Font = f0;
            Text.WordWrap = w0;
            return h + GapAfterSection;
        }
    }
}
