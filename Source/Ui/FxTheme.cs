using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 0.3.3 新界面的色板与基础绘制。
    ///
    /// 和原版界面完全分开：不读取 Widgets 的默认底色，所有面板、分隔线、
    /// 文字颜色都从这里取。尺寸一律按字体行高推导，适配 1x/2x UI 缩放。
    /// </summary>
    public static class FxTheme
    {
        public static readonly Color WindowBg = new Color32(14, 17, 22, 255);
        public static readonly Color PanelBg = new Color32(21, 26, 33, 255);
        public static readonly Color PanelSel = new Color32(28, 36, 48, 255);
        public static readonly Color Line = new Color32(36, 44, 54, 255);
        public static readonly Color LineHi = new Color32(58, 70, 84, 255);
        public static readonly Color TextColor = new Color32(215, 222, 232, 255);
        public static readonly Color Muted = new Color32(124, 135, 151, 255);
        public static readonly Color Long = new Color32(63, 182, 139, 255);
        public static readonly Color Short = new Color32(217, 83, 79, 255);
        public static readonly Color Warning = new Color32(217, 164, 65, 255);
        public static readonly Color Danger = new Color32(224, 90, 90, 255);
        public static readonly Color Accent = new Color32(59, 130, 246, 255);

        public static float LhS
        {
            get { return Text.LineHeightOf(GameFont.Small); }
        }

        public static float LhT
        {
            get { return Text.LineHeightOf(GameFont.Tiny); }
        }

        public static float LhM
        {
            get { return Text.LineHeightOf(GameFont.Medium); }
        }

        public static void Fill(Rect r, Color c)
        {
            Widgets.DrawBoxSolid(r, c);
        }

        public static void Border(Rect r, Color c)
        {
            Fill(new Rect(r.x, r.y, r.width, 1f), c);
            Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
            Fill(new Rect(r.x, r.y, 1f, r.height), c);
            Fill(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
        }

        public static void HLine(float x1, float x2, float y, Color c)
        {
            Fill(new Rect(x1, y, x2 - x1, 1f), c);
        }

        public static void VLine(float x, float y1, float y2, Color c)
        {
            Fill(new Rect(x, y1, 1f, y2 - y1), c);
        }

        /// <summary>画一段文字，自动保存/恢复字体、颜色、对齐与换行。</summary>
        public static void Label(Rect r, string s, GameFont font, Color color,
            TextAnchor anchor = TextAnchor.UpperLeft, bool wrap = false)
        {
            GameFont oldFont = Text.Font;
            Color oldColor = GUI.color;
            TextAnchor oldAnchor = Text.Anchor;
            bool oldWrap = Text.WordWrap;

            try
            {
                Text.Font = font;
                GUI.color = color;
                Text.Anchor = anchor;
                Text.WordWrap = wrap;
                Widgets.Label(r, s);
            }
            finally
            {
                Text.Font = oldFont;
                GUI.color = oldColor;
                Text.Anchor = oldAnchor;
                Text.WordWrap = oldWrap;
            }
        }

        public static void Tiny(Rect r, string s, Color color,
            TextAnchor anchor = TextAnchor.UpperLeft)
        {
            Label(r, s, GameFont.Tiny, color, anchor);
        }

        public static void Small(Rect r, string s, Color color,
            TextAnchor anchor = TextAnchor.UpperLeft)
        {
            Label(r, s, GameFont.Small, color, anchor);
        }

        public static void Medium(Rect r, string s, Color color,
            TextAnchor anchor = TextAnchor.UpperLeft)
        {
            Label(r, s, GameFont.Medium, color, anchor);
        }

        public static Color PnlColor(float v)
        {
            if (v > 1f) return Long;
            if (v < -1f) return Short;
            return TextColor;
        }
    }
}
