using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 神秘商人（0.3.2，D2 换新界面）。
    /// 付款前不知道方向；60% 真、40% 反。
    /// </summary>
    public class Window_FxMerchant : Window
    {
        private readonly FxAccount account;
        private readonly FxCurrency currency;
        private readonly string factionName;
        private readonly bool predictedUp;
        private readonly float driftLog;
        private readonly float price;
        private readonly System.Action onClosed;
        private bool revealed;

        public Window_FxMerchant(FxAccount account, FxCurrency currency, string factionName,
            bool predictedUp, float driftLog, float price, System.Action onClosed = null)
        {
            this.onClosed = onClosed;
            this.account = account;
            this.currency = currency;
            this.factionName = factionName;
            this.predictedUp = predictedUp;
            this.driftLog = driftLog;
            this.price = price;

            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = true;
            preventCameraMotion = false;
            FxWindowChrome.Prepare(this);
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(560f, 330f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (currency == null || account == null) { Close(); return; }

            Rect r = FxWindowChrome.Draw(this, inRect,
                "RimForex_MerchantTitle".Translate());

            if (!revealed)
            {
                string offer = "RimForex_MerchantOffer".Translate(
                    factionName, currency.PairLabel, price.ToString("F0"));
                GameFont oldFont = Text.Font;
                bool oldWrap = Text.WordWrap;
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                float h = Text.CalcHeight(offer, r.width);
                Text.Font = oldFont;
                Text.WordWrap = oldWrap;

                FxTheme.Label(new Rect(r.x, r.y, r.width, h), offer,
                    GameFont.Small, FxTheme.TextColor, TextAnchor.UpperLeft, true);

                float y = r.y + h + 8f;
                bool canPay = account.balance >= price;
                if (!canPay)
                {
                    FxTheme.Tiny(new Rect(r.x, y, r.width, 20f),
                        "RimForex_MerchantNoMoney".Translate(
                            (price - Mathf.Floor(account.balance)).ToString("F0")),
                        FxTheme.Short);
                }

                float by = r.yMax - 34f;
                if (FxWidgets.Button(new Rect(r.x, by, 150f, 34f),
                        "RimForex_MerchantPay".Translate(), canPay, true))
                {
                    PayAndReveal();
                    return;
                }
                if (FxWidgets.Button(new Rect(r.x + 160f, by, 150f, 34f),
                        "RimForex_MerchantDecline".Translate(), true, false, true))
                {
                    if (onClosed != null) onClosed();
                    Close();
                    return;
                }
                if (FxWidgets.Button(new Rect(r.x + 320f, by, 150f, 34f),
                        "RimForex_MerchantLater".Translate()))
                {
                    Close();
                }
                return;
            }

            string dir = predictedUp
                ? "RimForex_MerchantRise".Translate()
                : "RimForex_MerchantFall".Translate();
            string text = "RimForex_MerchantReveal".Translate(currency.PairLabel, dir);
            GameFont f0 = Text.Font;
            bool w0 = Text.WordWrap;
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float hh = Text.CalcHeight(text, r.width);
            Text.Font = f0;
            Text.WordWrap = w0;

            FxTheme.Label(new Rect(r.x, r.y, r.width, hh), text,
                GameFont.Small, FxTheme.TextColor, TextAnchor.UpperLeft, true);

            if (FxWidgets.Button(new Rect(r.x, r.yMax - 34f, 150f, 34f),
                    "RimForex_MerchantClose".Translate()))
            {
                Close();
            }
        }

        private void PayAndReveal()
        {
            if (account.balance < price) return;

            account.balance -= price;
            account.RecordFee("RimForex_FeeMerchant".Translate(), price);

            currency.newsDriftLog += driftLog;
            currency.newsDriftUntilTick = GenTicks.TicksGame + 5 * FxGameComponent.TicksPerDay;

            revealed = true;
            if (onClosed != null) onClosed();
            Messages.Message("RimForex_MsgMerchantBought".Translate(price.ToString("F0")),
                MessageTypeDefOf.NeutralEvent, false);
            FxGameComponent worldFx = Find.World != null
                ? Find.World.GetComponent<FxGameComponent>() : null;
            if (worldFx != null)
            {
                string dir = predictedUp
                    ? "RimForex_MerchantRise".Translate()
                    : "RimForex_MerchantFall".Translate();
                worldFx.RecordEvent(
                    "RimForex_LogMerchantIntel".Translate(currency.PairLabel),
                    "RimForex_LogMerchantDetail".Translate(
                        dir, price.ToString("F0")));
            }
        }
    }
}
