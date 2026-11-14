using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 统一的深色 tooltip。窗口在绘制末尾调用 Draw()，避免被后续内容盖住。
    /// </summary>
    public static class FxTooltip
    {
        private static string pending;
        private static Vector2 mouse;

        public static void Tip(Rect r, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!Mouse.IsOver(r)) return;
            pending = text;
            mouse = Event.current != null ? Event.current.mousePosition : Vector2.zero;
        }

        public static void Draw()
        {
            if (string.IsNullOrEmpty(pending)) return;

            float width = 330f;
            GameFont oldFont = Text.Font;
            bool oldWrap = Text.WordWrap;
            Text.Font = GameFont.Tiny;
            Text.WordWrap = true;
            float h = Mathf.Max(24f, Text.CalcHeight(pending, width - 20f) + 14f);
            Text.Font = oldFont;
            Text.WordWrap = oldWrap;

            float x = Mathf.Clamp(mouse.x + 16f, 4f, UI.screenWidth - width - 4f);
            float y = Mathf.Clamp(mouse.y + 16f, 4f, UI.screenHeight - h - 4f);
            Rect box = new Rect(x, y, width, h);

            FxTheme.Fill(box, new Color32(16, 20, 27, 248));
            FxTheme.Border(box, new Color32(90, 106, 126, 255));
            FxTheme.Label(new Rect(box.x + 10f, box.y + 7f,
                    box.width - 20f, box.height - 14f),
                pending, GameFont.Tiny, FxTheme.TextColor,
                TextAnchor.UpperLeft, true);

            pending = null;
        }
    }
}
