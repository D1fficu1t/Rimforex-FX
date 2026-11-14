using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 0.3.3 新界面的基础控件。
    ///
    /// 全部自绘：底色、1px 边框、悬停高亮、禁用态。
    /// 不调用 Widgets.ButtonText / DrawMenuSection，避免混入原版米色皮肤。
    /// </summary>
    public static class FxWidgets
    {
        public static bool Button(Rect r, string label, bool enabled = true,
            bool primary = false, GameFont font = GameFont.Small)
        {
            bool hover = enabled && Mouse.IsOver(r);
            Color bg = primary ? new Color32(28, 62, 50, 255) : FxTheme.PanelBg;
            if (hover) bg = primary ? new Color32(36, 82, 66, 255) : FxTheme.PanelSel;
            if (!enabled) bg = new Color32(18, 22, 28, 255);

            FxTheme.Fill(r, bg);
            FxTheme.Border(r, primary ? FxTheme.Long : FxTheme.Line);

            Color text = enabled ? (primary ? FxTheme.Long : FxTheme.TextColor) : FxTheme.Muted;
            FxTheme.Label(r, label, font, text, TextAnchor.MiddleCenter);

            return enabled && Widgets.ButtonInvisible(r);
        }

        public static bool Tab(Rect r, string label, bool active)
        {
            bool hover = Mouse.IsOver(r);
            if (active)
            {
                FxTheme.Fill(r, FxTheme.PanelSel);
                FxTheme.VLine(r.x, r.y, r.yMax, FxTheme.Accent);
            }
            else if (hover)
            {
                FxTheme.Fill(r, FxTheme.PanelBg);
            }

            FxTheme.Label(r, label, GameFont.Small,
                active ? FxTheme.TextColor : FxTheme.Muted, TextAnchor.MiddleCenter);
            return Widgets.ButtonInvisible(r);
        }

        public static void Panel(Rect r)
        {
            FxTheme.Fill(r, FxTheme.PanelBg);
            FxTheme.Border(r, FxTheme.Line);
        }

        public static void SectionHeader(Rect r, string title)
        {
            FxTheme.Label(new Rect(r.x + 12f, r.y + 8f, r.width - 24f, FxTheme.LhS + 2f),
                title, GameFont.Small, FxTheme.Muted, TextAnchor.UpperLeft);
            FxTheme.HLine(r.x, r.xMax, r.y + 30f, FxTheme.Line);
        }

        public static void Right(Rect r, string s, Color c, GameFont font = GameFont.Small)
        {
            FxTheme.Label(r, s, font, c, TextAnchor.MiddleRight);
        }

        public static void Center(Rect r, string s, Color c, GameFont font = GameFont.Small)
        {
            FxTheme.Label(r, s, font, c, TextAnchor.MiddleCenter);
        }

        /// <summary>竖向分隔条：1px 线 + 三个抓握点，可拖动。</summary>
        public static void SplitterV(Rect hit, Rect line, bool hot)
        {
            FxTheme.VLine(line.x, line.y, line.yMax,
                hot ? FxTheme.LineHi : FxTheme.Line);
            float cy = line.center.y;
            Color dot = hot ? FxTheme.TextColor : FxTheme.Muted;
            for (int i = -1; i <= 1; i++)
            {
                FxTheme.Fill(new Rect(line.x - 1f, cy + i * 7f - 1f, 3f, 3f), dot);
            }
        }

        public static void SplitterH(Rect hit, Rect line, bool hot)
        {
            FxTheme.HLine(line.x, line.xMax, line.y,
                hot ? FxTheme.LineHi : FxTheme.Line);
            float cx = line.center.x;
            Color dot = hot ? FxTheme.TextColor : FxTheme.Muted;
            for (int i = -1; i <= 1; i++)
            {
                FxTheme.Fill(new Rect(cx + i * 7f - 1f, line.y - 1f, 3f, 3f), dot);
            }
        }

        public static void ScrollBar(Rect track, float scroll, float viewFraction)
        {
            FxTheme.Fill(track, new Color32(17, 21, 27, 255));
            float h = Mathf.Max(24f, track.height * Mathf.Clamp01(viewFraction));
            float maxScroll = Mathf.Max(0.0001f, 1f - viewFraction);
            float t = Mathf.Clamp01(scroll / maxScroll);
            float y = track.y + (track.height - h) * t;
            FxTheme.Fill(new Rect(track.x, y, track.width, h),
                new Color32(70, 82, 98, 255));
        }
    }
}
