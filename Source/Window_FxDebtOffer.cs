using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 债务转让报价（0.3.3，D2 换新界面）。
    /// 三选一：接受 / 拒绝 / 让我想想；窗口暂停，关窗后倒计时继续。
    /// </summary>
    public class Window_FxDebtOffer : Window
    {
        private readonly FxGameComponent fx;

        public Window_FxDebtOffer(FxGameComponent fx)
        {
            this.fx = fx;

            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = true;
            preventCameraMotion = false;
            FxWindowChrome.Prepare(this);
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(600f, 400f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (fx == null || !fx.debtOfferActive || fx.debtOfferPawn == null)
            {
                Close();
                return;
            }

            Rect r = FxWindowChrome.Draw(this, inRect,
                "RimForex_DebtOfferTitle".Translate());

            string body = "RimForex_DebtOfferText".Translate(
                fx.debtOfferPawn.LabelShortCap,
                fx.debtOfferAmount.ToString("F0"));
            GameFont oldFont = Text.Font;
            bool oldWrap = Text.WordWrap;
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float h = Text.CalcHeight(body, r.width);
            Text.Font = oldFont;
            Text.WordWrap = oldWrap;

            FxTheme.Label(new Rect(r.x, r.y, r.width, h), body,
                GameFont.Small, FxTheme.TextColor, TextAnchor.UpperLeft, true);

            float y = r.y + h + 10f;
            SkillRecord top = FxGameComponent.TopSkillOf(fx.debtOfferPawn);
            if (top != null)
            {
                FxTheme.Tiny(new Rect(r.x, y, r.width, 20f),
                    "RimForex_DebtOfferTopSkill".Translate(top.def.LabelCap),
                    FxTheme.Warning);
            }

            float by = r.yMax - 34f;
            if (FxWidgets.Button(new Rect(r.x, by, 150f, 34f),
                    "RimForex_DebtOfferAccept".Translate(), true, true))
            {
                fx.AcceptDebtOffer();
                Close();
                return;
            }
            if (FxWidgets.Button(new Rect(r.x + 160f, by, 150f, 34f),
                    "RimForex_DebtOfferReject".Translate(), true, false, true))
            {
                fx.RejectDebtOffer();
                Close();
                return;
            }
            if (FxWidgets.Button(new Rect(r.x + 320f, by, 150f, 34f),
                    "RimForex_DebtOfferLater".Translate()))
            {
                Close();
            }
        }
    }
}
