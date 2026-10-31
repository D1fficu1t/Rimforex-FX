using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 编辑某一笔持仓的止盈 / 止损（0.3.1）。
    ///
    /// 判定口径：**方向价**（markRate），和强平线、持仓线一致；
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

            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(420f, 260f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (pos == null || account == null || !account.positions.Contains(pos))
            {
                Close();
                return;
            }

            float lhS = Text.LineHeightOf(GameFont.Small);
            float y = 0f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, inRect.width, Text.LineHeightOf(GameFont.Medium) + 2f),
                "RimForex_TpSlTitle".Translate());
            Text.Font = GameFont.Small;
            y += Text.LineHeightOf(GameFont.Medium) + 8f;

            Widgets.Label(new Rect(0f, y, inRect.width, lhS + 2f),
                "RimForex_TpSlPosLine".Translate(
                    pos.currencyName,
                    (pos.isLong ? "RimForex_Long" : "RimForex_Short").Translate(),
                    pos.entryRate.ToString("F2"),
                    pos.markRate.ToString("F2")));
            y += lhS + 10f;

            Widgets.Label(new Rect(0f, y + 4f, 44f, lhS), "RimForex_TpShort".Translate());
            Widgets.TextFieldNumeric(new Rect(48f, y, 100f, 24f), ref tp, ref tpBuf, 0f, 1000000f);
            Widgets.Label(new Rect(154f, y + 4f, 120f, lhS), PctText(tp));
            y += 30f;

            Widgets.Label(new Rect(0f, y + 4f, 44f, lhS), "RimForex_SlShort".Translate());
            Widgets.TextFieldNumeric(new Rect(48f, y, 100f, 24f), ref sl, ref slBuf, 0f, 1000000f);
            Widgets.Label(new Rect(154f, y + 4f, 120f, lhS), PctText(sl));
            y += 36f;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.72f, 0.74f, 0.78f);
            Widgets.Label(new Rect(0f, y, inRect.width, lhS * 2f), "RimForex_TpSlHint".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            float by = inRect.yMax - 30f;
            if (Widgets.ButtonText(new Rect(0f, by, 110f, 28f), "RimForex_TpSlSave".Translate()))
            {
                Apply();
                Close();
            }
            if (Widgets.ButtonText(new Rect(118f, by, 110f, 28f), "RimForex_TpSlClear".Translate()))
            {
                pos.tpRate = 0f;
                pos.slRate = 0f;
                Close();
            }
            if (Widgets.ButtonText(new Rect(236f, by, 110f, 28f), "RimForex_TpSlClose".Translate()))
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

        private string PctText(float price)
        {
            if (price <= 0f) return "";
            float mark = pos.markRate > 0f ? pos.markRate : pos.entryRate;
            if (mark <= 0.001f) return "";
            return ((price / mark - 1f) * 100f).ToString("+0.0;-0.0;0.0") + "%";
        }
    }
}
