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
            float fieldW = 150f;
            float rowH = 30f;

            FxTheme.Tiny(new Rect(r.x, y, 80f, 18f), "RimForex_TpShort".Translate(),
                FxTheme.Muted);
            tpBuf = FxWidgets.TextField(new Rect(r.x + 50f, y, fieldW, 26f), tpBuf, 12,
                "fx-tpsl-tp");
            tp = Parse(tpBuf);
            DrawPct(new Rect(r.x + 210f, y + 4f, r.width - 210f, 18f), tp);
            y += rowH;

            FxTheme.Tiny(new Rect(r.x, y, 80f, 18f), "RimForex_SlShort".Translate(),
                FxTheme.Muted);
            slBuf = FxWidgets.TextField(new Rect(r.x + 50f, y, fieldW, 26f), slBuf, 12,
                "fx-tpsl-sl");
            sl = Parse(slBuf);
            DrawPct(new Rect(r.x + 210f, y + 4f, r.width - 210f, 18f), sl);
            y += rowH + 4f;

            FxTheme.Tiny(new Rect(r.x, y, r.width, 34f), "RimForex_TpSlHint".Translate(),
                FxTheme.Muted);

            float by = r.yMax - 32f;
            if (FxWidgets.Button(new Rect(r.x, by, 110f, 32f),
                    "RimForex_TpSlSave".Translate(), true, true))
            {
                Apply();
                Close();
                return;
            }
            if (FxWidgets.Button(new Rect(r.x + 118f, by, 100f, 32f),
                    "RimForex_TpSlClear".Translate()))
            {
                pos.tpRate = 0f;
                pos.slRate = 0f;
                Close();
                return;
            }
            if (FxWidgets.Button(new Rect(r.x + 226f, by, 90f, 32f),
                    "RimForex_TpSlClose".Translate()))
            {
                Close();
            }
        }

        private void Apply()
        {
            if (pos == null) return;

            float mark = pos.markRate > 0f ? pos.markRate : pos.entryRate;
            bool okTp = tp > 0f && (pos.isLong ? tp > mark : tp < mark);
            bool okSl = sl > 0f && (pos.isLong ? sl < mark : sl > mark);

            pos.tpRate = okTp ? tp : 0f;
            pos.slRate = okSl ? sl : 0f;

            if ((tp > 0f && !okTp) || (sl > 0f && !okSl))
            {
                Messages.Message("RimForex_TpSlInvalid".Translate(),
                    MessageTypeDefOf.RejectInput, false);
            }
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
