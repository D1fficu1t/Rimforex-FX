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
        private static GUIStyle fieldStyle;
        private static readonly System.Collections.Generic.Dictionary<string, Rect> fieldRects
            = new System.Collections.Generic.Dictionary<string, Rect>();

        private static GUIStyle FieldStyle
        {
            get
            {
                if (fieldStyle == null)
                {
                    fieldStyle = new GUIStyle(GUI.skin.textField);
                    fieldStyle.normal.background = null;
                    fieldStyle.hover.background = null;
                    fieldStyle.active.background = null;
                    fieldStyle.focused.background = null;
                    fieldStyle.border = new RectOffset(0, 0, 0, 0);
                    fieldStyle.padding = new RectOffset(6, 6, 2, 2);
                    fieldStyle.alignment = TextAnchor.MiddleLeft;
                    fieldStyle.normal.textColor = FxTheme.TextColor;
                    fieldStyle.hover.textColor = FxTheme.TextColor;
                    fieldStyle.active.textColor = FxTheme.TextColor;
                    fieldStyle.focused.textColor = FxTheme.TextColor;
                }
                return fieldStyle;
            }
        }

        public static string TextField(Rect r, string text, int maxLength = 24,
            string controlName = null)
        {
            if (!string.IsNullOrEmpty(controlName))
            {
                GUI.SetNextControlName(controlName);
                fieldRects[controlName] = r;
            }
            FxTheme.Fill(r, new Color32(12, 15, 20, 255));
            FxTheme.Border(r, FxTheme.LineHi);
            GameFont oldFont = Text.Font;
            Color oldColor = GUI.color;
            Text.Font = GameFont.Small;
            GUI.color = FxTheme.TextColor;
            string result = GUI.TextField(new Rect(r.x + 1f, r.y + 1f,
                r.width - 2f, r.height - 2f), text ?? "", maxLength, FieldStyle);
            Text.Font = oldFont;
            GUI.color = oldColor;
            return result;
        }

        public static bool Button(Rect r, string label, bool enabled = true,
            bool primary = false, bool danger = false, bool warning = false,
            bool accent = false, GameFont font = GameFont.Small)
        {
            bool hover = enabled && Mouse.IsOver(r);

            Color bg;
            if (!enabled) bg = new Color32(18, 22, 28, 255);
            else if (danger) bg = hover ? new Color32(96, 38, 42, 255) : new Color32(58, 26, 28, 255);
            else if (warning) bg = hover ? new Color32(104, 70, 28, 255) : new Color32(62, 42, 18, 255);
            else if (accent) bg = hover ? new Color32(36, 76, 120, 255) : new Color32(24, 50, 82, 255);
            else if (primary) bg = hover ? new Color32(36, 82, 66, 255) : new Color32(28, 62, 50, 255);
            else bg = hover ? new Color32(40, 50, 64, 255) : new Color32(28, 36, 48, 255);

            Color border;
            if (!enabled) border = FxTheme.Line;
            else if (danger) border = FxTheme.Danger;
            else if (warning) border = FxTheme.Warning;
            else if (accent) border = FxTheme.Accent;
            else if (primary) border = FxTheme.Long;
            else border = hover ? FxTheme.LineHi : new Color32(60, 74, 90, 255);

            FxTheme.Fill(r, bg);
            FxTheme.Border(r, border);

            Color text;
            if (!enabled) text = FxTheme.Muted;
            else if (danger) text = new Color32(246, 170, 170, 255);
            else if (warning) text = new Color32(246, 206, 130, 255);
            else if (accent) text = new Color32(150, 196, 255, 255);
            else if (primary) text = FxTheme.Long;
            else text = FxTheme.TextColor;

            FxTheme.Label(r, label, font, text, TextAnchor.MiddleCenter);
            return enabled && Widgets.ButtonInvisible(r);
        }

        /// <summary>
        /// 只画按钮，不处理事件。
        /// 用于 HoldInt/HoldFloat：事件必须留给长按逻辑，否则 MouseDown 会被按钮吃掉。
        /// </summary>
        public static void ButtonVisual(Rect r, string label)
        {
            bool hover = Mouse.IsOver(r);
            FxTheme.Fill(r, hover ? new Color32(40, 50, 64, 255)
                : new Color32(28, 36, 48, 255));
            FxTheme.Border(r, hover ? FxTheme.LineHi : new Color32(60, 74, 90, 255));
            FxTheme.Label(r, label, GameFont.Small, FxTheme.TextColor,
                TextAnchor.MiddleCenter);
        }

        /// <summary>点击输入框外部时取消焦点，避免数字热键被输入框吞掉。</summary>
        public static void DefocusIfClickedOutside()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.MouseDown) return;
            string focused = GUI.GetNameOfFocusedControl();
            if (string.IsNullOrEmpty(focused)) return;
            Rect r;
            if (fieldRects.TryGetValue(focused, out r) && r.Contains(e.mousePosition))
            {
                return;
            }
            GUI.FocusControl(null);
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

        public static bool Toggle(Rect r, bool on)
        {
            FxTheme.Fill(r, new Color32(12, 15, 20, 255));
            FxTheme.Border(r, on ? FxTheme.Accent : FxTheme.LineHi);
            if (on)
            {
                FxTheme.Fill(new Rect(r.x + 4f, r.y + 4f, r.width - 8f, r.height - 8f),
                    FxTheme.Accent);
            }
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
