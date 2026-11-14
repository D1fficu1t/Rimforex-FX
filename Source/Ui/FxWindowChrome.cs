using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 次级窗口的统一外壳：深色底、1px 边框、标题栏、右上角红色关闭按钮。
    /// 各窗口在构造函数里调用 Prepare，再在 DoWindowContents 里调用 Draw。
    /// </summary>
    public static class FxWindowChrome
    {
        public const float TitleH = 40f;

        public static void Prepare(Window w)
        {
            if (w == null) return;
            w.doWindowBackground = false;
            w.doCloseX = false;
            w.doCloseButton = false;
        }

        public static Rect Draw(Window w, Rect inRect, string title,
            Color? titleColor = null)
        {
            FxTheme.Fill(inRect, FxTheme.WindowBg);
            FxTheme.Border(inRect, FxTheme.Line);

            Rect close = new Rect(inRect.xMax - 36f, inRect.y + 7f, 28f, 26f);
            if (FxWidgets.Button(close, "×", true, false, true))
            {
                w.Close();
            }

            FxTheme.Medium(new Rect(inRect.x + 14f, inRect.y + 8f,
                    inRect.width - 60f, 26f),
                title, titleColor ?? FxTheme.TextColor);

            FxTheme.HLine(inRect.x + 1f, inRect.xMax - 1f, inRect.y + TitleH,
                FxTheme.Line);

            return new Rect(inRect.x + 14f, inRect.y + TitleH + 10f,
                inRect.width - 28f, inRect.height - TitleH - 20f);
        }
    }
}
