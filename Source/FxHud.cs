using System.Collections.Generic;
using RimWorld;
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
    /// ── 三个交互（2026-10-23 加）──
    /// 1. **点击头部**展开 / 收起持仓明细（默认收起，只显示总浮盈）
    /// 2. 右上角小箭头 **▶** 直接打开交易终端
    /// 3. 展开后每行右侧有「平仓」按钮，走的是和终端完全相同的记账
    ///    （`FxGameComponent.ClosePositionFromUi`）
    ///
    /// 为什么"展开"和"打开终端"要分成两个热区：如果点一下就弹出一个大窗口，
    /// 玩家只想瞄一眼持仓时会很烦；反过来，只显示总数又不够用。
    /// 所以头部负责展开，箭头负责开窗。
    /// </summary>
    public static class FxHud
    {
        /// <summary>左右内边距。</summary>
        private const float PadX = 9f;

        /// <summary>贴边留白，用于默认位置。</summary>
        private const float Margin = 12f;

        /// <summary>宽度下限，避免短数字时窗口窄得像条线。</summary>
        private const float MinW = 116f;

        /// <summary>
        /// 宽度上限。设得比较宽（420），因为**宁可窗口宽一点，也不能让数字被横向截断** ——
        /// 玩家看这个窗就是为了那个数字。真正会把宽度撑大的只有极端盈亏（七八位数）。
        /// </summary>
        private const float MaxW = 420f;

        /// <summary>展开后每一行的高度（含间隔）。</summary>
        private const float RowH = 22f;

        /// <summary>头部右上角"打开终端"按钮的宽度。</summary>
        private const float OpenBtnW = 26f;

        /// <summary>每行"平仓"按钮的宽度。</summary>
        private const float CloseBtnW = 44f;

        /// <summary>展开状态。静态的 —— 它是 UI 偏好，不需要进存档。</summary>
        private static bool expanded;

        /// <summary>拖动中标记。静态的，因为拖拽状态天然跨帧。</summary>
        private static bool dragging;

        /// <summary>按下时鼠标相对窗口左上角的偏移，避免拖起来"跳一下"。</summary>
        private static Vector2 dragOffset;

        /// <summary>本次按下的位置，用来区分"点击"和"拖动"。</summary>
        private static Vector2 pressPos;

        /// <summary>本帧画过的按钮，拖动/点击判定要避开它们。</summary>
        private static readonly List<Rect> buttonRects = new List<Rect>();

        /// <summary>
        /// 画悬浮窗。
        /// </summary>
        /// <param name="acc">殖民地账户；null 则不画。</param>
        /// <param name="map">当前地图，用于找终端建筑。</param>
        public static void DrawFloatingPnl(FxAccount acc, Map map)
        {
            if (acc == null || acc.positions == null || acc.positions.Count == 0)
            {
                expanded = false;
                return;
            }

            RimForexSettings cfg = RimForexMod.Settings;
            if (cfg == null || !cfg.showFloatingPnl) return;

            // 终端开着的时候不画 —— 那里面有同样的数字，重复显示只是碍事。
            if (Find.WindowStack != null && Find.WindowStack.IsOpen(typeof(Window_FxTerminal))) return;

            buttonRects.Clear();

            // ── 中文标签不能用 GameFont.Tiny（2026-10-20 二次修）──
            //
            // 第一版把标签放在 Tiny 字号、14px 高的格子里，结果
            // "浮动盈亏"四个字**纵向被切掉一半**（作者截图确认）。
            // 数字倒是完整的 —— 说明不是宽度不够，而是中文字形在 Tiny 下
            // 超出该字号的标称行高，被 Rect 裁掉了。
            //
            // 改成【单行布局】+ 全部 GameFont.Small 来彻底绕开这类行高问题。
            Text.Font = GameFont.Small;

            string label = "RimForex_HudFloat".Translate();
            string value = acc.FloatingPnl.ToString("+0;-0;0");

            float labelW = Text.CalcSize(label).x;
            float valueW = Text.CalcSize(value).x;
            float lineH = Text.LineHeight;

            float headerH = lineH + 12f;

            // 收起时宽度沿用原来的紧凑算法；展开时按行内容算，并留出平仓按钮的位置。
            float w = Mathf.Clamp(labelW + 8f + valueW + PadX * 2f + OpenBtnW + 4f, MinW, MaxW);

            if (expanded)
            {
                float widest = 0f;
                for (int i = 0; i < acc.positions.Count; i++)
                {
                    var p = acc.positions[i];
                    if (p == null) continue;
                    string rowLabel = RowLabel(p);
                    float rw = Text.CalcSize(rowLabel).x + 6f + Text.CalcSize(p.Pnl.ToString("+0;-0;0")).x;
                    if (rw > widest) widest = rw;
                }
                float need = widest + CloseBtnW + PadX * 2f + 8f;
                if (need > w) w = Mathf.Clamp(need, MinW, MaxW);
            }

            float h = headerH + (expanded ? acc.positions.Count * RowH + 4f : 0f);

            Rect box = CurrentRect(cfg, w, h);

            // 半透明底 + 细边框，尽量贴近原版 HUD 的观感
            Widgets.DrawBoxSolid(box, new Color(0.10f, 0.11f, 0.13f, 0.72f));
            Widgets.DrawBox(box, 1);

            Text.Font = GameFont.Small;

            // ---- 头部：标签 + 总浮盈 ----
            GUI.color = new Color(0.72f, 0.76f, 0.82f);
            Widgets.Label(new Rect(box.x + PadX, box.y + 6f, labelW + 2f, lineH), label);
            GUI.color = Color.white;

            GUI.color = Window_FxTerminal.PnlGuiColor(acc.FloatingPnl);
            Widgets.Label(new Rect(box.x + PadX + labelW + 8f, box.y + 6f, valueW + 2f, lineH), value);
            GUI.color = Color.white;

            // ---- 右上角：打开终端 ----
            Rect openBtn = new Rect(box.xMax - OpenBtnW - 4f, box.y + 3f, OpenBtnW, lineH + 2f);
            buttonRects.Add(openBtn);
            if (Widgets.ButtonText(openBtn, ">>"))
            {
                if (!OpenTerminal(map)) Messages.Message("RimForex_MsgNoTerminal".Translate(),
                    MessageTypeDefOf.RejectInput, false);
            }
            TooltipHandler.TipRegion(openBtn, "RimForex_OpenTerminal".Translate());

            // ---- 持仓明细 ----
            if (expanded)
            {
                float y = box.y + headerH;
                Widgets.DrawLineHorizontal(box.x + 4f, y - 2f, box.width - 8f);

                FxGameComponent fx = Find.World != null ? Find.World.GetComponent<FxGameComponent>() : null;

                // ── 平仓同样要有"通电的终端"（2026-10-23 补）──
                //
                // 交易终端需要 250W 才允许交易，窗口在断电时会自动关闭（R11）。
                // 但悬浮窗曾经是条后门：终端断电了，照样能从这里平仓 ——
                // 等于绕过电力门槛。作者实测发现（"断电的情况下依然可以在悬浮窗口里平仓"）。
                //
                // 注意**只禁用平仓按钮，不隐藏悬浮窗**：
                // 断电时玩家更需要盯着浮亏，看是应该修电还是认赔，只是不能操作。
                bool hasPower = HasUsableTerminal(map);

                for (int i = 0; i < acc.positions.Count; i++)
                {
                    var p = acc.positions[i];
                    if (p == null) continue;

                    float rowY = y + i * RowH;

                    // 名称 + 方向
                    GUI.color = p.isLong
                        ? new Color(0.62f, 0.86f, 0.62f)
                        : new Color(0.92f, 0.62f, 0.62f);
                    Widgets.Label(new Rect(box.x + PadX, rowY + 2f, box.width - CloseBtnW - PadX * 2f - 60f, lineH),
                        RowLabel(p));
                    GUI.color = Color.white;

                    // 该笔盈亏（右对齐，压住平仓按钮左侧）
                    string pnlStr = p.Pnl.ToString("+0;-0;0");
                    float pw = Text.CalcSize(pnlStr).x;
                    Rect pnlR = new Rect(box.xMax - CloseBtnW - 8f - pw, rowY + 2f, pw, lineH);
                    GUI.color = Window_FxTerminal.PnlGuiColor(p.Pnl);
                    Widgets.Label(pnlR, pnlStr);
                    GUI.color = Color.white;

                    // 平仓：需要 ①通电的终端 ②跨过一次报价 ③组件在
                    Rect closeBtn = new Rect(box.xMax - CloseBtnW - 4f, rowY + 1f, CloseBtnW, RowH - 4f);
                    buttonRects.Add(closeBtn);
                    bool closable = hasPower && fx != null && fx.CanClose(p);
                    if (!closable) GUI.color = new Color(0.5f, 0.5f, 0.5f);
                    if (Widgets.ButtonText(closeBtn, "RimForex_HudClose".Translate()) && closable)
                    {
                        // 与终端完全同一套记账（手续费 / 点差 / 历史 / 心情）
                        fx.ClosePositionFromUi(acc, p);
                        break;   // 列表已被修改，本帧不再继续画
                    }
                    GUI.color = Color.white;

                    TooltipHandler.TipRegion(closeBtn, !hasPower
                        ? "RimForex_HudCloseNoPower".Translate()
                        : (closable ? "RimForex_HudCloseTip".Translate()
                                    : "RimForex_HudCloseLocked".Translate()));
                }
            }
            else
            {
                // 收起状态下整块头部都能点开
                TooltipHandler.TipRegion(box, "RimForex_HudExpandTip".Translate());
            }

            Text.Font = GameFont.Small;

            HandleInput(box, cfg, w, h, headerH);
        }

        /// <summary>一行里显示的文字：货币名 + 多/空。</summary>
        private static string RowLabel(FxPosition p)
        {
            return p.currencyName + " " +
                (p.isLong ? "RimForex_Long".Translate() : "RimForex_Short".Translate());
        }

        /// <summary>找当前地图上第一台可用的终端并打开。返回是否成功。</summary>
        private static bool OpenTerminal(Map map)
        {
            if (map == null) return false;

            foreach (var b in map.listerBuildings.AllBuildingsColonistOfClass<Building_FXTradingTerminal>())
            {
                // 这里只挑可用终端：断电 / 已拆除的直接跳过。
                if (b != null && b.TryOpenTerminalWindow()) return true;
            }
            return false;
        }

        /// <summary>
        /// 这张地图上有没有**通电可用**的终端。
        ///
        /// 悬浮窗的"平仓"按钮要用它把关：交易需要 250W，
        /// 窗口断电会自动关闭（R11），悬浮窗也就不能变成绕开电力的后门。
        /// </summary>
        private static bool HasUsableTerminal(Map map)
        {
            if (map == null || map.listerBuildings == null) return false;

            foreach (var b in map.listerBuildings.AllBuildingsColonistOfClass<Building_FXTradingTerminal>())
            {
                if (b != null && b.Usable) return true;
            }
            return false;
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
        /// 拖动 + 点击。
        ///
        /// 只响应左键：右键在 RimWorld 里是"取消"，中键则被各种 mod
        /// 抢得很厉害（本项目里还因此删掉过图表的中键平移）。
        ///
        /// 按钮自己的点击由 IMGUI 处理，这里在开始拖动前会跳过按钮矩形，
        /// 否则"点平仓"会同时把窗口拖走。
        /// </summary>
        private static void HandleInput(Rect box, RimForexSettings cfg, float w, float h, float headerH)
        {
            Event e = Event.current;
            if (e == null) return;

            bool OverButton(Vector2 p)
            {
                for (int i = 0; i < buttonRects.Count; i++)
                    if (buttonRects[i].Contains(p)) return true;
                return false;
            }

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !box.Contains(e.mousePosition) || OverButton(e.mousePosition)) break;
                    dragging = true;
                    pressPos = e.mousePosition;
                    dragOffset = e.mousePosition - new Vector2(box.x, box.y);
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (!dragging) break;
                    {
                        Vector2 pos = e.mousePosition - dragOffset;
                        cfg.floatPnlX = Mathf.Clamp(pos.x, 0f, Mathf.Max(0f, UI.screenWidth - w));
                        cfg.floatPnlY = Mathf.Clamp(pos.y, 0f, Mathf.Max(0f, UI.screenHeight - h));
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (!dragging) break;
                    dragging = false;
                    // 位移很小 = 点击，而不是拖动。
                    // 只有点在头部（不展开时是整块）才切换展开状态。
                    if ((e.mousePosition - pressPos).sqrMagnitude < 25f
                        && pressPos.y <= box.y + headerH)
                    {
                        expanded = !expanded;
                    }
                    e.Use();
                    break;
            }
        }
    }
}
