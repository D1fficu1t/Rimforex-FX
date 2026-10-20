using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 屏幕角落的浮动盈亏小窗（HUD）。
    ///
    /// ── 为什么需要它 ──
    /// 交易终端里本来就有"浮动盈亏"，但它只在窗口打开时可见。
    /// 而玩家真正需要盯盘的时刻，恰恰是**关掉终端、在基地里忙别的事**的时候 ——
    /// 矿挖到一半行情跑了，还得专门点开终端看一眼，很割裂。
    ///
    /// ── 为什么单独一个文件 ──
    /// Core.cs 已经三千多行，再往里塞 UI 绘制会越来越难找。
    /// 而且这块逻辑和行情/账户完全无关，只是"读一个数字然后画出来"。
    ///
    /// ── 尺寸为什么要动态算（2026-10-20 修）──
    /// 第一版把窗口写死成 132×30，结果标签和数字挤在一起、
    /// 数字下半截被框底切掉（作者实测截图）。
    /// 现在宽高都按实际文字量算：标签一行、数字一行，互不重叠。
    /// </summary>
    public static class FxHud
    {
        /// <summary>左右内边距。</summary>
        private const float PadX = 9f;

        /// <summary>贴边留白，用于默认位置。</summary>
        private const float Margin = 12f;

        /// <summary>宽度上下限。上限防止极端数字（几十万）把窗口拉到半屏。</summary>
        private const float MinW = 116f;
        private const float MaxW = 260f;

        /// <summary>
        /// 画悬浮窗。
        /// </summary>
        /// <param name="acc">当前地图的账户；null 则不画。</param>
        public static void DrawFloatingPnl(FxAccount acc)
        {
            if (acc == null || acc.positions.Count == 0) return;

            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg == null || !cfg.showFloatingPnl) return;

            // 终端开着的时候不画 —— 那里面有同样的数字，重复显示只是碍事。
            if (Find.WindowStack != null && Find.WindowStack.IsOpen(typeof(Window_FxTerminal))) return;

            string label = "RimForex_HudFloat".Translate();
            string value = acc.FloatingPnl.ToString("+0;-0;0");

            // 先按实际文字量量出尺寸，再定位 —— 这样任何数字都不会被切。
            Text.Font = GameFont.Tiny;
            float labelW = Text.CalcSize(label).x;
            Text.Font = GameFont.Small;
            float valueW = Text.CalcSize(value).x;

            float w = Mathf.Clamp(Mathf.Max(labelW, valueW) + PadX * 2f, MinW, MaxW);
            float h = 40f;

            Rect box = CurrentRect(cfg, w, h);

            // 半透明底 + 细边框，尽量贴近原版 HUD 的观感
            Widgets.DrawBoxSolid(box, new Color(0.10f, 0.11f, 0.13f, 0.72f));
            Widgets.DrawBox(box, 1);

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.72f, 0.76f, 0.82f);
            Widgets.Label(new Rect(box.x + PadX, box.y + 4f, box.width - PadX * 2f, 14f), label);
            GUI.color = Color.white;

            Text.Font = GameFont.Small;
            GUI.color = Window_FxTerminal.PnlGuiColor(acc.FloatingPnl);
            Widgets.Label(new Rect(box.x + PadX, box.y + 18f, box.width - PadX * 2f, 20f), value);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            HandleDrag(box, cfg, w, h);
        }

        /// <summary>
        /// 当前应该画在哪。没被拖过（-1）时用默认位置：右上角、信件图标附近。
        ///
        /// 结果会钳制在屏幕内 —— 换分辨率或改 UI 缩放后，
        /// 原本合法的坐标可能跑到屏幕外，那样窗口就再也抓不回来了。
        /// </summary>
        private static Rect CurrentRect(RimForexSettings cfg, float w, float h)
        {
            float x = cfg.floatPnlX;
            float y = cfg.floatPnlY;

            if (x < 0f)
            {
                // 右上角。左侧留出信件/通知那一列的空间。
                x = UI.screenWidth - w - Margin - 210f;
                y = Margin + 80f;
            }

            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, UI.screenWidth - w));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, UI.screenHeight - h));

            return new Rect(x, y, w, h);
        }

        /// <summary>
        /// 拖动。用 IMGUI 的原生事件，不依赖任何输入库。
        ///
        /// 只响应左键：右键在 RimWorld 里是"取消"，中键则被各种 mod
        /// 抢得很厉害（本项目里还因此删掉过图表的中键平移）。
        /// </summary>
        private static void HandleDrag(Rect box, RimForexSettings cfg, float w, float h)
        {
            Event e = Event.current;
            if (e == null) return;

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && box.Contains(e.mousePosition))
                    {
                        dragging = true;
                        dragOffset = e.mousePosition - new Vector2(box.x, box.y);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (dragging)
                    {
                        Vector2 pos = e.mousePosition - dragOffset;
                        cfg.floatPnlX = Mathf.Clamp(pos.x, 0f, Mathf.Max(0f, UI.screenWidth - w));
                        cfg.floatPnlY = Mathf.Clamp(pos.y, 0f, Mathf.Max(0f, UI.screenHeight - h));
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (dragging)
                    {
                        dragging = false;
                        e.Use();
                    }
                    break;
            }
        }

        /// <summary>拖动中标记。静态的，因为拖拽状态天然跨帧。</summary>
        private static bool dragging;

        /// <summary>按下时鼠标相对窗口左上角的偏移，避免拖起来"跳一下"。</summary>
        private static Vector2 dragOffset;
    }
}
