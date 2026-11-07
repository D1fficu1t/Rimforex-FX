using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 临近爆仓预警窗口。
    ///
    /// 暂停由窗口自己持有（forcePause = true），而不是依赖某一时刻的 TogglePaused 调用 ——
    /// 这样暂停状态与窗口的存在严格绑定：窗口在 → 游戏暂停 → 下一 tick 不执行
    /// → 健康度不会变化 → 预警不会被自动作废。
    /// 早期版本用 TogglePaused + 无 forcePause，结果是窗口弹出后下一 tick 又把预警清掉了，
    /// 表现就是"弹出来一秒就被自动关掉，而且时间没暂停"。
    ///
    /// 另外加了一个短暂的输入保护期：触发预警的那一次点击/按键不会立刻把窗口关掉。
    /// </summary>
    public class Window_MarginCall : Window
    {
        /// <summary>窗口刚弹出后的输入保护时长（真实秒）。</summary>
        private const float InputGuardSeconds = 0.4f;

        private readonly FxGameComponent fx;
        private readonly FxPosition position;
        private readonly float openedAt;

        private int addAmount = 0;
        private string addBuffer = "0";
        private bool bufferDirty = true;

        public override Vector2 InitialSize => new Vector2(560f, 356f);

        public Window_MarginCall(FxGameComponent fx, FxPosition position)
        {
            this.fx = fx;
            this.position = position;
            openedAt = Time.realtimeSinceStartup;

            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = true;
            // 关键：由窗口持有暂停权，而不是靠一次性的 TogglePaused
            forcePause = true;
            preventCameraMotion = true;
            onlyOneOfTypeAllowed = false;
            closeOnClickedOutside = false;
        }

        private bool InputGuarded => Time.realtimeSinceStartup - openedAt < InputGuardSeconds;

        private MarginWarning Current
        {
            get
            {
                MarginWarning w;
                return fx != null && fx.pendingWarnings.TryGetValue(position, out w) ? w : null;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            MarginWarning warn = Current;
            if (warn == null || warn.position == null || warn.account == null)
            {
                // 仓位已平掉 / 已处理 / 已恢复安全
                Close();
                return;
            }

            FxPosition p = warn.position;
            FxAccount acc = warn.account;
            Rect r = inRect.ContractedBy(4f);

            Text.Font = GameFont.Medium;
            GUI.color = new Color(1f, 0.55f, 0.5f);
            Widgets.Label(new Rect(r.x, r.y, r.width, 30f), "RimForex_MarginCallTitle".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            float y = r.y + 32f;

            string health = (p.MarginHealth * 100f).ToString("F0");
            // 正文按实际需要的高度预留（最少 3 行）。早期固定只给 62px（约 3 行），
            // 中文/英文稍长就把最后一行裁掉一半 —— 截图里那行"只出现一半"就是这个原因。
            string body = "RimForex_MarginCallBody".Translate(p.currencyName, health, p.Pnl.ToString("F0"));
            float bodyH = Mathf.Max(64f, Text.CalcHeight(body, r.width));
            Widgets.Label(new Rect(r.x, y, r.width, bodyH), body);
            y += bodyH + 8f;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.78f, 0.80f, 0.82f);
            Widgets.Label(new Rect(r.x, y, r.width, 18f),
                "RimForex_MarginCallDetail".Translate(
                    p.margin.ToString("F0"),
                    acc.balance.ToString("F0"),
                    p.markRate.ToString("F2"),
                    p.leverage.ToString("F0")));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 26f;

            if (bufferDirty)
            {
                addAmount = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(acc.balance, p.margin)));
                addBuffer = addAmount.ToString();
                bufferDirty = false;
            }

            Widgets.Label(new Rect(r.x, y + 5f, 96f, 24f), "RimForex_MarginAdd".Translate());
            Rect field = new Rect(r.x + 100f, y, 110f, 28f);
            Widgets.TextFieldNumeric(field, ref addAmount, ref addBuffer, 0f, 100000000f);

            Rect allBtn = new Rect(field.xMax + 4f, y, 52f, 28f);
            if (Widgets.ButtonText(allBtn, "RimForex_All".Translate()))
            {
                addAmount = Mathf.Max(0, Mathf.FloorToInt(acc.balance));
                addBuffer = addAmount.ToString();
            }

            TooltipHandler.TipRegion(new Rect(r.x, y, field.xMax - r.x, 28f),
                "RimForex_MarginAddTip".Translate(acc.balance.ToString("F0")));
            y += 34f;

            // ── 三档一键补足：45% / 50% / 60%（2026-11-07）──
            // 补到 45% 只比强平线高 0.15 个初始保证金，价格抖一下就回去了；
            // 50% / 60% 才是真正能把仓位救回来的量。
            // 需要金额 = 目标 × 初始保证金 − 当前净值（健康度的分母是 BaseMargin）
            Widgets.Label(new Rect(r.x, y + 5f, 96f, 24f), "RimForex_MarginTo".Translate());
            float bx = r.x + 100f;
            float bw = Mathf.Clamp((r.width - 100f - 8f) / 3f, 84f, 130f);
            float[] presets = { 0.45f, 0.50f, 0.60f };
            for (int k = 0; k < presets.Length; k++)
            {
                float target = presets[k];
                Rect btn = new Rect(bx + (bw + 4f) * k, y, bw, 28f);
                float need = target * p.BaseMargin - p.Equity;
                int needInt = Mathf.CeilToInt(Mathf.Max(0f, need));
                bool affordable = needInt <= acc.balance;

                GUI.color = affordable ? Color.white : new Color(0.62f, 0.62f, 0.62f);
                string label = "RimForex_MarginPreset".Translate(
                    (target * 100f).ToString("F0"), needInt.ToString("F0"));
                if (Widgets.ButtonText(btn, label))
                {
                    if (affordable)
                    {
                        addAmount = needInt;
                        addBuffer = addAmount.ToString();
                    }
                    else
                    {
                        Messages.Message(
                            "RimForex_MarginPresetShort".Translate((needInt - Mathf.FloorToInt(acc.balance)).ToString("F0")),
                            MessageTypeDefOf.RejectInput, false);
                    }
                }
                GUI.color = Color.white;
            }
            y += 36f;

            bool canAdd = addAmount > 0 && addAmount <= acc.balance;

            float halfW = (r.width - 8f) / 2f;
            Rect addBtnRect = new Rect(r.x, y, halfW, 34f);
            Rect laterBtn = new Rect(r.x + halfW + 8f, y, halfW, 34f);

            if (!canAdd) GUI.color = Color.gray;
            if (Widgets.ButtonText(addBtnRect, "RimForex_MarginDoAdd".Translate()) && canAdd)
            {
                DoAddMargin(warn, addAmount);
            }
            GUI.color = Color.white;

            if (Widgets.ButtonText(laterBtn, "RimForex_MarginLater".Translate()))
            {
                Dismiss();
            }
        }

        private void DoAddMargin(MarginWarning warn, int amount)
        {
            FxPosition p = warn.position;
            FxAccount acc = warn.account;
            if (p == null || acc == null) return;

            int clamped = Mathf.Clamp(amount, 0, Mathf.FloorToInt(acc.balance));
            if (clamped <= 0) return;

            acc.balance -= clamped;
            p.margin += clamped;
            p.addedMargin += clamped;

            // 补仓后健康度回到预警线以上就复位标记，将来再跌破才会重新提醒。
            // 若补仓后仍在警戒区，保持标记不变 —— 避免玩家刚补完就被同一条提醒反复打扰。
            if (p.MarginHealth > FxGameComponent.CriticalThreshold)
            {
                p.ResetWarnState();
            }

            Messages.Message(
                "RimForex_MsgMarginAdded".Translate(p.currencyName, clamped, (p.MarginHealth * 100f).ToString("F0")),
                MessageTypeDefOf.PositiveEvent, false);

            Dismiss();
        }

        private void Dismiss()
        {
            // 只把窗口从队列里摘掉。
            // 千万不要在这里重置 warnedHealth / criticalWarned ——
            // 那样下一个 tick 的 MaybeWarn 会立刻重新入队，窗口无限重弹。
            // 告警水位的复位交给 MaybeWarn：只有健康度回到安全区才复位。
            if (fx != null) fx.pendingWarnings.Remove(position);
            Close();
        }

        /// <summary>X 按钮 / Esc 走的路径。清理队列，但不重置告警水位。</summary>
        public override void PreClose()
        {
            base.PreClose();
            if (fx != null) fx.pendingWarnings.Remove(position);
        }

        public override void OnCancelKeyPressed()
        {
            // 刚弹出时的按键事件属于"触发预警的那次操作"，忽略掉
            if (InputGuarded) return;
            Dismiss();
        }

        public override void OnAcceptKeyPressed()
        {
            if (InputGuarded) return;
            Dismiss();
        }
    }
}
