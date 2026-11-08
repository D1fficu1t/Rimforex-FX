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

            doCloseX = true;
            // 关闭时通知调用方（让商人自己走掉）
            doCloseButton = false;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            // 谈生意时暂停，避免玩家一边读报价一边被行情/袭击打断
            forcePause = true;
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

                bool declined = Widgets.ButtonText(
                    new Rect(160f, by, 150f, 30f), "RimForex_MerchantDecline".Translate());
                bool later = Widgets.ButtonText(
                    new Rect(320f, by, 150f, 30f), "RimForex_MerchantLater".Translate());

                if (declined)
                {
                    if (onClosed != null) onClosed();   // 不感兴趣 → 商人走
                    Close();
                }
                else if (later)
                {
                    // 等会再谈 → 只关窗，商人和殖民者都不动，玩家可以去凑钱
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
                Close();   // 收钱后商人本来就要走，这次关闭不再重复赶人
            }
        }

        private void PayAndReveal()
        {
            if (account.balance < price) return;

            account.balance -= price;
            // 写进交易历史，玩家才知道这笔钱是从终端余额扣的
            account.RecordFee("RimForex_FeeMerchant".Translate(), price);

            currency.newsDriftLog += driftLog;
            currency.newsDriftUntilTick = GenTicks.TicksGame + 5 * FxGameComponent.TicksPerDay;

            revealed = true;
            if (onClosed != null) onClosed();   // 付完钱商人就走了
            Messages.Message("RimForex_MsgMerchantBought".Translate(price.ToString("F0")),
                MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
