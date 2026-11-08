using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 神秘商人（0.3.2）。
    ///
    /// 两个状态：
    ///   ① 报价：告诉你"我能透露 X 的未来走势"，先收钱
    ///   ② 揭晓：收完钱再说方向，并真的给该货币挂一段漂移
    ///
    /// 60% 是真的（照他说的方向走），40% 是反的。
    /// 玩家在付款前拿不到方向，所以不存在"白嫖情报"。
    /// </summary>
    public class Window_FxMerchant : Window
    {
        private readonly FxAccount account;
        private readonly FxCurrency currency;
        private readonly string factionName;
        private readonly bool predictedUp;
        private readonly float driftLog;
        private readonly float price;

        private bool revealed;

        public Window_FxMerchant(FxAccount account, FxCurrency currency, string factionName,
            bool predictedUp, float driftLog, float price)
        {
            this.account = account;
            this.currency = currency;
            this.factionName = factionName;
            this.predictedUp = predictedUp;
            this.driftLog = driftLog;
            this.price = price;

            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(520f, revealed ? 260f : 300f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (currency == null || account == null) { Close(); return; }

            float lhS = Text.LineHeightOf(GameFont.Small);
            float y = 0f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, inRect.width, Text.LineHeightOf(GameFont.Medium) + 2f),
                "RimForex_MerchantTitle".Translate());
            Text.Font = GameFont.Small;
            y += Text.LineHeightOf(GameFont.Medium) + 10f;

            if (!revealed)
            {
                string offer = "RimForex_MerchantOffer".Translate(
                    factionName, currency.PairLabel, price.ToString("F0"));
                float h = Text.CalcHeight(offer, inRect.width);
                Widgets.Label(new Rect(0f, y, inRect.width, h), offer);
                y += h + 12f;

                bool canPay = account.balance >= price;
                if (!canPay)
                {
                    GUI.color = new Color(1f, 0.62f, 0.55f);
                    Widgets.Label(new Rect(0f, y, inRect.width, lhS + 2f),
                        "RimForex_MerchantNoMoney".Translate(
                            (price - Mathf.Floor(account.balance)).ToString("F0")));
                    GUI.color = Color.white;
                    y += lhS + 6f;
                }

                float by = inRect.yMax - 32f;
                GUI.color = canPay ? Color.white : Color.gray;
                if (Widgets.ButtonText(new Rect(0f, by, 150f, 30f), "RimForex_MerchantPay".Translate())
                    && canPay)
                {
                    PayAndReveal();
                }
                GUI.color = Color.white;

                if (Widgets.ButtonText(new Rect(160f, by, 150f, 30f), "RimForex_MerchantDecline".Translate()))
                {
                    Close();
                }
                return;
            }

            // ② 揭晓
            string dir = predictedUp ? "RimForex_MerchantRise".Translate() : "RimForex_MerchantFall".Translate();
            string text = "RimForex_MerchantReveal".Translate(currency.PairLabel, dir);
            float hh = Text.CalcHeight(text, inRect.width);
            Widgets.Label(new Rect(0f, y, inRect.width, hh), text);

            if (Widgets.ButtonText(new Rect(0f, inRect.yMax - 32f, 150f, 30f),
                    "RimForex_MerchantClose".Translate()))
            {
                Close();
            }
        }

        private void PayAndReveal()
        {
            if (account.balance < price) return;

            account.balance -= price;

            currency.newsDriftLog += driftLog;
            currency.newsDriftUntilTick = GenTicks.TicksGame + 5 * FxGameComponent.TicksPerDay;

            revealed = true;
            Messages.Message("RimForex_MsgMerchantBought".Translate(price.ToString("F0")),
                MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
