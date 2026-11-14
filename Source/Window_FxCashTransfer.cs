using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 新版终端的存取白银小窗。
    /// 逻辑仍走 Window_FxTerminal 里原有的 DoDeposit / DoWithdraw，
    /// 这里只负责输入金额与显示可用额度。
    /// </summary>
    public class Window_FxCashTransfer : Window
    {
        private readonly Window_FxTerminal owner;
        private readonly bool deposit;
        private string buffer = "";

        public Window_FxCashTransfer(Window_FxTerminal owner, bool deposit)
        {
            this.owner = owner;
            this.deposit = deposit;

            doWindowBackground = false;
            doCloseX = false;
            doCloseButton = false;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = false;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(380f, 230f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            FxTheme.Fill(inRect, FxTheme.WindowBg);
            FxTheme.Border(inRect, FxTheme.Line);

            Rect close = new Rect(inRect.xMax - 34f, inRect.y + 8f, 26f, 24f);
            if (FxWidgets.Button(close, "×", true, false, true)) { Close(); return; }

            FxTheme.Medium(new Rect(inRect.x + 16f, inRect.y + 10f, inRect.width - 60f, 24f),
                (deposit ? "RimForex_UiCashDepositTitle" : "RimForex_UiCashWithdrawTitle")
                    .Translate(), FxTheme.TextColor);

            int available = deposit
                ? owner.CountMapSilverForUi()
                : Mathf.FloorToInt(owner.AccountForUi != null ? owner.AccountForUi.balance : 0f);
            FxTheme.Tiny(new Rect(inRect.x + 16f, inRect.y + 42f, inRect.width - 32f, 16f),
                (deposit ? "RimForex_UiAvailableSilver" : "RimForex_UiAvailableBalance")
                    .Translate(available.ToString("N0")), FxTheme.Muted);

            FxTheme.Tiny(new Rect(inRect.x + 16f, inRect.y + 72f, 60f, 16f),
                "RimForex_UiAmount".Translate(), FxTheme.Muted);
            buffer = FxWidgets.TextField(new Rect(inRect.x + 16f, inRect.y + 90f,
                inRect.width - 32f, 30f), buffer, 12, "fx-cash-amount");

            int amount;
            if (!int.TryParse(buffer, out amount)) amount = 0;
            amount = Mathf.Max(0, amount);
            bool canDo = amount > 0 && amount <= available;

            if (FxWidgets.Button(new Rect(inRect.x + 16f, inRect.yMax - 48f, 120f, 32f),
                    "RimForex_UiConfirm".Translate(), canDo, true))
            {
                if (deposit) owner.DepositSilverForUi(amount);
                else owner.WithdrawSilverForUi(amount);
                Close();
            }
            if (FxWidgets.Button(new Rect(inRect.x + 144f, inRect.yMax - 48f, 100f, 32f),
                    "RimForex_UiCancel".Translate()))
            {
                Close();
            }
        }
    }
}
