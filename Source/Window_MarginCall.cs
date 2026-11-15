using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 临近爆仓预警窗口（0.3.0，0.3.3 换新界面）。
    ///
    /// 暂停由窗口自己持有（forcePause = true），窗口在就暂停，
    /// 健康度不会在玩家读提示时继续恶化。
    /// </summary>
    public class Window_MarginCall : Window
    {
        private const float InputGuardSeconds = 0.4f;

        private readonly FxGameComponent fx;
        private readonly FxPosition position;
        private readonly float openedAt;

        private int addAmount = 0;
        private string addBuffer = "0";
        private bool bufferDirty = true;

        public override Vector2 InitialSize => new Vector2(600f, 400f);

        public Window_MarginCall(FxGameComponent fx, FxPosition position)
        {
            this.fx = fx;
            this.position = position;
            openedAt = Time.realtimeSinceStartup;

            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = true;
            forcePause = true;
            preventCameraMotion = true;
            onlyOneOfTypeAllowed = false;
            closeOnClickedOutside = false;
            FxWindowChrome.Prepare(this);
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
                Close();
                return;
            }

            FxPosition p = warn.position;
            FxAccount acc = warn.account;
            Rect r = FxWindowChrome.Draw(this, inRect,
                "RimForex_MarginCallTitle".Translate(), FxTheme.Danger);

            float health = p.MarginHealth * 100f;
            string body = "RimForex_MarginCallBody".Translate(
                p.currencyName, health.ToString("F0"), p.Pnl.ToString("F0"));
            GameFont oldFont = Text.Font;
            bool oldWrap = Text.WordWrap;
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float bodyH = Mathf.Max(64f, Text.CalcHeight(body, r.width));
            Text.Font = oldFont;
            Text.WordWrap = oldWrap;

            FxTheme.Label(new Rect(r.x, r.y, r.width, bodyH), body,
                GameFont.Small, FxTheme.TextColor, TextAnchor.UpperLeft, true);
            float y = r.y + bodyH + 8f;

            FxTheme.Label(new Rect(r.x, y, r.width, 34f),
                "RimForex_MarginCallDetail".Translate(
                    p.margin.ToString("F0"),
                    acc.balance.ToString("F0"),
                    p.markRate.ToString("F2"),
                    p.leverage.ToString("F0")),
                GameFont.Tiny, FxTheme.Muted, TextAnchor.UpperLeft, true);
            y += 38f;

            if (bufferDirty)
            {
                addAmount = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(acc.balance, p.margin)));
                addBuffer = addAmount.ToString();
                bufferDirty = false;
            }

            FxTheme.Tiny(new Rect(r.x, y + 6f, 92f, 18f),
                "RimForex_MarginAdd".Translate(), FxTheme.Muted);
            addBuffer = FxWidgets.TextField(new Rect(r.x + 96f, y, 110f, 28f),
                addBuffer, 12, "fx-margin-add");
            int parsed;
            if (!int.TryParse(addBuffer, out parsed)) parsed = 0;
            addAmount = Mathf.Clamp(parsed, 0, 100000000);

            if (FxWidgets.Button(new Rect(r.x + 214f, y, 52f, 28f),
                    "RimForex_All".Translate()))
            {
                addAmount = Mathf.Max(0, Mathf.FloorToInt(acc.balance));
                addBuffer = addAmount.ToString();
            }
            FxTooltip.Tip(new Rect(r.x, y, 266f, 28f),
                "RimForex_MarginAddTip".Translate(acc.balance.ToString("F0")));
            y += 36f;

            FxTheme.Tiny(new Rect(r.x, y + 6f, 92f, 18f),
                "RimForex_MarginTo".Translate(), FxTheme.Muted);
            float bx = r.x + 96f;
            float bw = Mathf.Clamp((r.width - 100f - 8f) / 3f, 84f, 150f);
            float[] presets = { 0.45f, 0.50f, 0.60f };
            for (int k = 0; k < presets.Length; k++)
            {
                float target = presets[k];
                float need = target * p.BaseMargin - p.Equity;
                int needInt = Mathf.CeilToInt(Mathf.Max(0f, need));
                bool affordable = needInt <= acc.balance;
                Rect btn = new Rect(bx + (bw + 6f) * k, y, bw, 28f);
                string label = "RimForex_MarginPreset".Translate(
                    (target * 100f).ToString("F0"), needInt.ToString("F0"));
                if (FxWidgets.Button(btn, label, true,
                        primary: affordable, danger: !affordable))
                {
                    if (affordable)
                    {
                        addAmount = needInt;
                        addBuffer = addAmount.ToString();
                    }
                    else
                    {
                        Messages.Message(
                            "RimForex_MarginPresetShort".Translate(
                                (needInt - Mathf.FloorToInt(acc.balance)).ToString("F0")),
                            MessageTypeDefOf.RejectInput, false);
                    }
                }
            }
            y += 40f;

            bool canAdd = addAmount > 0 && addAmount <= acc.balance;
            float by = r.yMax - 34f;
            if (FxWidgets.Button(new Rect(r.x, by, (r.width - 8f) / 2f, 34f),
                    "RimForex_MarginDoAdd".Translate(), canAdd, true))
            {
                DoAddMargin(warn, addAmount);
                return;
            }
            if (FxWidgets.Button(new Rect(r.x + (r.width - 8f) / 2f + 8f, by,
                    (r.width - 8f) / 2f, 34f),
                    "RimForex_MarginLater".Translate()))
            {
                Dismiss();
            }
            FxTooltip.Draw(inRect);
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

            Messages.Message(
                "RimForex_MsgMarginAdded".Translate(p.currencyName, clamped,
                    (p.MarginHealth * 100f).ToString("F0")),
                MessageTypeDefOf.PositiveEvent, false);

            Dismiss();
        }

        private void Dismiss()
        {
            RemoveOwnPending();
            Close();
        }

        /// <summary>
        /// 只删除“自己这条”预警。
        /// 窗口开着时若排进了更深档的新预警，直接 Remove(position)
        /// 会把新预警一起删掉。
        /// </summary>
        private void RemoveOwnPending()
        {
            if (fx == null || position == null) return;
            MarginWarning queued;
            if (!fx.pendingWarnings.TryGetValue(position, out queued)
                || ReferenceEquals(queued, Current))
            {
                fx.pendingWarnings.Remove(position);
            }
        }

        public override void PreClose()
        {
            base.PreClose();
            RemoveOwnPending();
        }

        public override void OnCancelKeyPressed()
        {
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
