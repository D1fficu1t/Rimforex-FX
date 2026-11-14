using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 编辑某一笔持仓的止盈 / 止损（0.3.1，0.3.3 换新界面）。
    ///
    /// 判定口径：方向价（markRate），和强平线、持仓线一致；
    /// 触发后按市价平仓，不按设定价成交 —— 跳空越过设定价时的滑点是真实的。
    /// </summary>
    public class Window_FxTpSl : Window
    {
        private readonly FxGameComponent fx;
        private readonly FxAccount account;
        private readonly FxPosition pos;

        private float tp;
        private float sl;
        private string tpBuf = "";
        private string slBuf = "";

        // 长按连发状态：2 = 止盈，3 = 止损
        private int holdTarget;
        private int holdDir;
        private float holdTime;
        private float holdAcc;

        public Window_FxTpSl(FxGameComponent fx, FxAccount account, FxPosition pos)
        {
            this.fx = fx;
            this.account = account;
            this.pos = pos;

            tp = pos != null ? pos.tpRate : 0f;
            sl = pos != null ? pos.slRate : 0f;
            tpBuf = tp > 0f ? tp.ToString("F2") : "";
            slBuf = sl > 0f ? sl.ToString("F2") : "";

            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = false;
            FxWindowChrome.Prepare(this);
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(460f, 300f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (pos == null || account == null || !account.positions.Contains(pos))
            {
                Close();
                return;
            }

            Rect r = FxWindowChrome.Draw(this, inRect, "RimForex_TpSlTitle".Translate());

            FxTheme.Small(new Rect(r.x, r.y, r.width, 20f),
                "RimForex_TpSlPosLine".Translate(
                    pos.currencyName,
                    (pos.isLong ? "RimForex_Long" : "RimForex_Short").Translate(),
                    pos.entryRate.ToString("F2"),
                    pos.markRate.ToString("F2")),
                FxTheme.Muted);

            float y = r.y + 30f;
            const float fieldW = 112f;
            const float btnW = 22f;
            const float rowH = 30f;
            float mark = pos.markRate > 0f ? pos.markRate : pos.entryRate;

            FxTheme.Tiny(new Rect(r.x, y, 50f, 18f), "RimForex_TpShort".Translate(),
                FxTheme.Muted);
            Rect tpMinus = new Rect(r.x + 50f, y, btnW, 26f);
            Rect tpPlus = new Rect(r.x + 50f + btnW + 4f + fieldW + 4f, y, btnW, 26f);
            FxWidgets.ButtonVisual(tpMinus, "−");
            FxWidgets.ButtonVisual(tpPlus, "+");
            tpBuf = FxWidgets.TextField(new Rect(r.x + 50f + btnW + 4f, y, fieldW, 26f),
                tpBuf, 12, "fx-tpsl-tp");
            tp = Parse(tpBuf);
            HoldTpSl(tpMinus, tpPlus, 2, delegate(int dir, float step)
            {
                float b = tp > 0f ? tp : mark;
                tp = Mathf.Max(0f, b + dir * step);
                tpBuf = tp.ToString("F2");
            });
            DrawPct(new Rect(r.x + 222f, y + 4f, r.width - 222f, 18f), tp);
            y += rowH;

            FxTheme.Tiny(new Rect(r.x, y, 50f, 18f), "RimForex_SlShort".Translate(),
                FxTheme.Muted);
            Rect slMinus = new Rect(r.x + 50f, y, btnW, 26f);
            Rect slPlus = new Rect(r.x + 50f + btnW + 4f + fieldW + 4f, y, btnW, 26f);
            FxWidgets.ButtonVisual(slMinus, "−");
            FxWidgets.ButtonVisual(slPlus, "+");
            slBuf = FxWidgets.TextField(new Rect(r.x + 50f + btnW + 4f, y, fieldW, 26f),
                slBuf, 12, "fx-tpsl-sl");
            sl = Parse(slBuf);
            HoldTpSl(slMinus, slPlus, 3, delegate(int dir, float step)
            {
                float b = sl > 0f ? sl : mark;
                sl = Mathf.Max(0f, b + dir * step);
                slBuf = sl.ToString("F2");
            });
            DrawPct(new Rect(r.x + 222f, y + 4f, r.width - 222f, 18f), sl);
            y += rowH + 4f;

            FxTheme.Tiny(new Rect(r.x, y, r.width, 34f), "RimForex_TpSlHint".Translate(),
                FxTheme.Muted);

            float by = r.yMax - 32f;
            if (FxWidgets.Button(new Rect(r.x, by, 110f, 32f),
                    "RimForex_TpSlSave".Translate(), true, true))
            {
                if (Apply()) Close();
                return;
            }
            if (FxWidgets.Button(new Rect(r.x + 118f, by, 100f, 32f),
                    "RimForex_TpSlClear".Translate()))
            {
                // 只清输入框，是否写回仓位交给保存按钮
                tp = 0f;
                sl = 0f;
                tpBuf = "";
                slBuf = "";
            }
            if (FxWidgets.Button(new Rect(r.x + 226f, by, 90f, 32f),
                    "RimForex_TpSlClose".Translate()))
            {
                Close();
            }
        }

        /// <summary>
        /// 和下单区的 HoldFloat 同一套手感：短按 0.01，按住后 0.05 / 0.5 / 5 / 20 加速。
        /// </summary>
        private void HoldTpSl(Rect minus, Rect plus, int target,
            System.Action<int, float> apply)
        {
            Event e = Event.current;
            if (e == null) return;
            Vector2 m = e.mousePosition;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (minus.Contains(m))
                {
                    holdTarget = target; holdDir = -1; holdTime = 0f; holdAcc = 0f;
                    apply(-1, 0.01f);
                    e.Use();
                }
                else if (plus.Contains(m))
                {
                    holdTarget = target; holdDir = 1; holdTime = 0f; holdAcc = 0f;
                    apply(1, 0.01f);
                    e.Use();
                }
                else
                {
                    holdTarget = 0;
                }
                return;
            }

            if (e.type == EventType.MouseUp)
            {
                if (holdTarget == target) holdTarget = 0;
                return;
            }

            if (holdTarget != target || holdDir == 0) return;

            holdTime += UnityEngine.Time.unscaledDeltaTime;
            if (holdTime > 30f) { holdTarget = 0; return; }
            if (holdTime < 0.30f) return;

            holdAcc += UnityEngine.Time.unscaledDeltaTime;
            float interval = holdTime > 6f ? 0.04f
                : holdTime > 4f ? 0.05f
                : holdTime > 2f ? 0.08f : 0.12f;
            if (holdAcc < interval) return;
            holdAcc = 0f;

            float step = holdTime > 6f ? 20f
                : holdTime > 4f ? 5f
                : holdTime > 2f ? 0.5f : 0.05f;
            apply(holdDir, step);
        }

        private bool Apply()
        {
            if (pos == null) return false;

            float mark = pos.markRate > 0f ? pos.markRate : pos.entryRate;
            bool badTp = tp > 0f && (pos.isLong ? tp <= mark : tp >= mark);
            bool badSl = sl > 0f && (pos.isLong ? sl >= mark : sl <= mark);

            if (badTp || badSl)
            {
                Messages.Message("RimForex_TpSlInvalid".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return false;
            }

            pos.tpRate = tp;
            pos.slRate = sl;
            return true;
        }

        private static float Parse(string buf)
        {
            if (buf == null) return 0f;
            buf = buf.Trim();
            if (buf.Length == 0) return 0f;
            float v;
            if (!float.TryParse(buf, out v)) return 0f;
            return v > 0f ? v : 0f;
        }

        private void DrawPct(Rect r, float price)
        {
            if (price <= 0f) return;
            float mark = pos.markRate > 0f ? pos.markRate : pos.entryRate;
            if (mark <= 0.001f) return;
            float pct = (price / mark - 1f) * 100f;
            FxTheme.Tiny(r, pct.ToString("+0.0;-0.0;0.0") + "%",
                pct >= 0f ? FxTheme.Long : FxTheme.Short, TextAnchor.MiddleLeft);
        }
    }
}
