using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 「官方借贷服务」窗口（0.3.0，D2 换新界面）。
    /// 四家固定借贷方，2×2 卡片；单利、期限、上限规则不变。
    /// </summary>
    public class Window_FxLoans : Window
    {
        private readonly FxGameComponent fx;
        private readonly FxAccount account;
        private readonly Building_FXTradingTerminal terminal;

        private readonly int[] amount = new int[FxGameComponent.LenderCount];
        private readonly string[] amountBuf = new string[FxGameComponent.LenderCount];

        private const float Gap = 10f;

        public Window_FxLoans(FxGameComponent fx, FxAccount account,
            Building_FXTradingTerminal terminal)
        {
            this.fx = fx;
            this.account = account;
            this.terminal = terminal;

            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;
            FxWindowChrome.Prepare(this);

            for (int i = 0; i < amount.Length; i++) amountBuf[i] = "0";
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(960f, 680f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (terminal == null || !terminal.Usable)
            {
                Close();
                return;
            }
            if (fx == null || account == null)
            {
                FxWidgets.Center(inRect, "RimForex_NoMarket".Translate(), FxTheme.Muted);
                return;
            }

            Rect r = FxWindowChrome.Draw(this, inRect,
                "RimForex_LoanTitle".Translate());

            float owed = fx.TotalOwed(account);
            FxTheme.Tiny(new Rect(r.x, r.y, r.width, 18f),
                "RimForex_LoanSummary".Translate(
                    account.balance.ToString("F0"),
                    owed.ToString("F0"),
                    (account.Equity - owed).ToString("F0")),
                FxTheme.Muted);

            float top = r.y + 28f;
            float h = (r.yMax - top - Gap) * 0.5f;
            float w = (r.width - Gap) * 0.5f;
            for (int i = 0; i < FxGameComponent.LenderCount; i++)
            {
                int col = i % 2;
                int row = i / 2;
                Rect cell = new Rect(r.x + col * (w + Gap), top + row * (h + Gap), w, h);
                DrawLender(cell, i);
            }
        }

        private void DrawLender(Rect cell, int idx)
        {
            FxWidgets.Panel(cell);
            Rect r = cell.ContractedBy(12f);

            const float Icon = 64f;
            Rect iconRect = new Rect(r.x, r.y, Icon, Icon);
            Texture2D tex = ContentFinder<Texture2D>.Get("UI/Loans/Lender" + idx, false);
            if (tex != null)
            {
                GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
            }
            else
            {
                FxTheme.Fill(iconRect, new Color32(24, 30, 38, 255));
                FxTheme.Border(iconRect, FxTheme.LineHi);
                FxWidgets.Center(iconRect, (idx + 1).ToString(), FxTheme.Muted,
                    GameFont.Medium);
            }

            float tx = iconRect.xMax + 10f;
            float tw = r.xMax - tx;
            FxTheme.Small(new Rect(tx, r.y, tw, 22f),
                FxGameComponent.LenderNameKeys[idx].Translate(), FxTheme.TextColor);

            float cap = fx.LenderLimit(idx, account);
            float borrowed = fx.LenderBorrowed(idx, account);
            float available = Mathf.Max(0f, cap - borrowed);
            float owed = 0f;
            float dailyInterest = 0f;
            int remainDays = -1;
            int now = GenTicks.TicksGame;
            for (int i = 0; i < account.loans.Count; i++)
            {
                var l = account.loans[i];
                if (l == null || l.lender != idx) continue;
                owed += l.Owed;
                dailyInterest += l.principal * FxGameComponent.LenderDailyRate(idx);
                if (l.termDays > 0)
                {
                    int left = l.termDays - (now - l.startTick) / FxGameComponent.TicksPerDay;
                    if (left < 0) left = 0;
                    if (remainDays < 0 || left < remainDays) remainDays = left;
                }
            }

            FxTheme.Tiny(new Rect(tx, r.y + 24f, tw, 16f),
                "RimForex_LoanLimit".Translate(cap.ToString("F0"),
                    borrowed.ToString("F0"), available.ToString("F0")), FxTheme.Muted);

            string term = FxGameComponent.LenderTermDays[idx] > 0
                ? "RimForex_LoanTerm".Translate(FxGameComponent.LenderTermDays[idx].ToString())
                : "RimForex_LoanTermNone".Translate();
            FxTheme.Tiny(new Rect(tx, r.y + 40f, tw, 16f),
                "RimForex_LoanRate".Translate(
                    (FxGameComponent.LenderDailyRate(idx) * 100f).ToString("0.00"), term),
                FxTheme.Muted);
            FxTheme.Tiny(new Rect(tx, r.y + 56f, tw, 16f),
                "RimForex_LoanDailyDue".Translate(dailyInterest.ToString("F0")),
                dailyInterest >= 0.5f ? FxTheme.Warning : FxTheme.TextColor);
            FxTheme.Tiny(new Rect(tx, r.y + 72f, tw, 16f), remainDays >= 0
                ? "RimForex_LoanOwedRemain".Translate(owed.ToString("F0"),
                    remainDays.ToString())
                : "RimForex_LoanOwed".Translate(owed.ToString("F0")),
                FxTheme.Muted);

            float descY = r.y + Icon + 6f;
            FxTheme.Tiny(new Rect(r.x, descY, r.width, 64f),
                FxGameComponent.LenderDescKeys[idx].Translate(), FxTheme.Muted);

            float by = r.yMax - 28f;
            const float InputW = 92f;
            float btnW = (r.width - InputW - Gap * 2f) * 0.5f;
            Rect inputRect = new Rect(r.x, by, InputW, 26f);
            amountBuf[idx] = FxWidgets.TextField(inputRect, amountBuf[idx], 10,
                "fx-loan-" + idx);
            int parsed;
            if (!int.TryParse(amountBuf[idx], out parsed)) parsed = 0;
            amount[idx] = Mathf.Clamp(parsed, 0, 1000000);

            Rect borrowRect = new Rect(inputRect.xMax + Gap, by, btnW, 26f);
            Rect repayRect = new Rect(borrowRect.xMax + Gap, by, btnW, 26f);

            bool canBorrow = available >= 1f
                && (account.balance >= 0f || FxGameComponent.LoansOpenWhileFrozen(account));
            if (FxWidgets.Button(borrowRect, "RimForex_LoanBorrow".Translate(),
                    canBorrow, true))
            {
                string err;
                int want = amount[idx] > 0 ? amount[idx] : Mathf.FloorToInt(available);
                if (fx.TryBorrow(account, idx, want, out err))
                {
                    Messages.Message("RimForex_LoanBorrowed".Translate(want.ToString("F0")),
                        MessageTypeDefOf.PositiveEvent, false);
                    amount[idx] = 0;
                    amountBuf[idx] = "0";
                }
                else if (err != null)
                {
                    Messages.Message(err.Translate(), MessageTypeDefOf.RejectInput, false);
                }
            }

            bool canRepay = owed >= 0.5f && account.balance >= 0.5f;
            if (FxWidgets.Button(repayRect, "RimForex_LoanRepay".Translate(),
                    canRepay))
            {
                float pay = Mathf.Min(account.balance, owed);
                for (int i = account.loans.Count - 1; i >= 0 && pay > 0.01f; i--)
                {
                    var l = account.loans[i];
                    if (l == null || l.lender != idx) continue;
                    float take = Mathf.Min(pay, l.Owed);
                    fx.RepayLoan(account, l, take);
                    pay -= take;
                }
                Messages.Message("RimForex_LoanRepaid".Translate(),
                    MessageTypeDefOf.NeutralEvent, false);
            }

            TooltipHandler.TipRegion(borrowRect, "RimForex_LoanBorrowTip".Translate());
            TooltipHandler.TipRegion(repayRect, "RimForex_LoanRepayTip".Translate());
        }
    }
}
