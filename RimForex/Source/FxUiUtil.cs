using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 界面共用的小工具。原本 <c>ColorizeBySign</c> 在 Window_FxTerminal 与
    /// Window_TradeHistory 里各写了一份（完全相同的实现），抽到这里避免两处漂移。
    /// </summary>
    public static class FxUiUtil
    {
        /// <summary>正数染绿、负数染红、接近零保持原色。用于盈亏 / 涨跌幅显示。</summary>
        public static string ColorizeBySign(string text, float value)
        {
            if (value > 0.5f) return text.Colorize(new Color(0.45f, 0.85f, 0.50f));
            if (value < -0.5f) return text.Colorize(new Color(0.95f, 0.45f, 0.40f));
            return text;
        }
    }
}
