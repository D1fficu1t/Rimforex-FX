using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 债务转让报价（0.3.3）。
    ///
    /// 清算部把一个欠债人带到殖民地外侧，报价窗口里三选一：
    /// 接受（继承债务、人加入）、拒绝、让我想想（关窗，倒计时继续）。
    ///
    /// 与神秘商人一样 forcePause：读报价时游戏暂停，关窗后时间继续走。
    /// </summary>
    public class Window_FxDebtOffer : Window
    {
        private readonly FxGameComponent fx;

        public Window_FxDebtOffer(FxGameComponent fx)
        {
            this.fx = fx;

            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = true;
            preventCameraMotion = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(560f, 380f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (fx == null || !fx.debtOfferActive || fx.debtOfferPawn == null)
            {
                Close();
                return;
            }

            float y = 0f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, inRect.width, Text.LineHeightOf(GameFont.Medium) + 2f),
                "RimForex_DebtOfferTitle".Translate());
            Text.Font = GameFont.Small;
            y += Text.LineHeightOf(GameFont.Medium) + 12f;

            string body = "RimForex_DebtOfferText".Translate(
                fx.debtOfferPawn.LabelShortCap,
                fx.debtOfferAmount.ToString("F0"));
            float h = Text.CalcHeight(body, inRect.width);
            Widgets.Label(new Rect(0f, y, inRect.width, h), body);

            float by = inRect.yMax - 34f;
            if (Widgets.ButtonText(new Rect(0f, by, 150f, 32f),
                    "RimForex_DebtOfferAccept".Translate()))
            {
                fx.AcceptDebtOffer();
                Close();
                return;
            }

            if (Widgets.ButtonText(new Rect(160f, by, 150f, 32f),
                    "RimForex_DebtOfferReject".Translate()))
            {
                fx.RejectDebtOffer();
                Close();
                return;
            }

            if (Widgets.ButtonText(new Rect(320f, by, 150f, 32f),
                    "RimForex_DebtOfferLater".Translate()))
            {
                // 只关窗，报价与倒计时都留着
                Close();
            }
        }
    }
}
