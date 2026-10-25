using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 「官方借贷服务」窗口（0.3.0）。
    ///
    /// 四家借贷方固定在这里，数值与文案见 <c>FxGameComponent.LenderCap / LenderRate / LenderTerm</c>
    /// 以及 Keyed 里的 <c>RimForex_Loan{0..3}Name / Desc</c>。
    ///
    /// 设计要点（作者已拍板）：
    /// · **单利**：每季按本金收固定比例，利息不滚进本金（见 FxLoan）
    /// · **期限**：越便宜越短（1 季 / 2 季 / 3 季 / 无期限）
    /// · **上限随净值缩放**：min(固定 × 3, 固定 + 净值 × 0.25)
    /// · 界面必须显示「本季应付利息」，不让玩家自己算
    /// </summary>
    public class Window_FxLoans : Window
    {
        private readonly FxGameComponent fx;
        private readonly FxAccount account;

        /// <summary>四家的借款输入。用缓冲字符串是因为 TextFieldNumeric 需要它。</summary>
        private readonly int[] amount = new int[FxGameComponent.LenderCount];
        private readonly string[] amountBuf = new string[FxGameComponent.LenderCount];

        private const float Gap = 8f;

        public Window_FxLoans(FxGameComponent fx, FxAccount account)
        {
            this.fx = fx;
            this.account = account;

            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;

            for (int i = 0; i < amount.Length; i++) amountBuf[i] = "0";
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(940f, 660f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (fx == null || account == null)
            {
                Widgets.Label(inRect, "RimForex_NoMarket".Translate());
                return;
            }

            // ---- 标题 ----
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f),
                "RimForex_LoanTitle".Translate());
            Text.Font = GameFont.Small;

            // ---- 账户摘要 ----
            float owed = fx.TotalOwed(account);
            string summary = "RimForex_LoanSummary".Translate(
                account.balance.ToString("F0"),
                owed.ToString("F0"),
                (account.Equity - owed).ToString("F0"));
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.75f, 0.78f, 0.82f);
            Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width, 18f), summary);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // ---- 2×2 四格 ----
            float top = inRect.y + 58f;
            float h = (inRect.height - 58f - Gap - 24f) * 0.5f;
            float w = (inRect.width - Gap) * 0.5f;

            for (int i = 0; i < FxGameComponent.LenderCount; i++)
            {
                int col = i % 2;
                int row = i / 2;
                Rect cell = new Rect(inRect.x + col * (w + Gap), top + row * (h + Gap), w, h);
                DrawLender(cell, i);
            }

            // ---- 页脚：规则说明 ----
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.66f, 0.70f, 0.75f);
            Widgets.Label(new Rect(inRect.x, inRect.yMax - 22f, inRect.width, 18f),
                "RimForex_LoanFooter".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawLender(Rect cell, int idx)
        {
            Widgets.DrawMenuSection(cell);
            Rect r = cell.ContractedBy(8f);

            // ── 第一行：图标（左）+ 名称与三条数值（右）──
            const float Icon = 78f;
            Rect iconRect = new Rect(r.x, r.y, Icon, Icon);
            Texture2D tex = ContentFinder<Texture2D>.Get("UI/Loans/Lender" + idx, false);
            if (tex != null)
            {
                GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
            }
            else
            {
                // 作者还没做图时画占位块 —— 绝不能因为缺贴图就崩窗口
                Widgets.DrawBoxSolid(iconRect, new Color(0.20f, 0.22f, 0.26f));
                Widgets.DrawBox(iconRect, 1);
                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(iconRect, (idx + 1).ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }

            float tx = iconRect.xMax + 8f;
            float tw = r.xMax - tx;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(tx, r.y - 2f, tw, 26f), FxGameComponent.LenderNameKeys[idx].Translate());
            Text.Font = GameFont.Tiny;

            float cap = fx.LenderLimit(idx, account);
            float borrowed = fx.LenderBorrowed(idx, account);
            float available = Mathf.Max(0f, cap - borrowed);
            float owed = 0f;
            float quarterInterest = 0f;
            for (int i = 0; i < account.loans.Count; i++)
            {
                var l = account.loans[i];
                if (l == null || l.lender != idx) continue;
                owed += l.Owed;
                quarterInterest += l.principal * FxGameComponent.LenderRate[idx];
            }

            Widgets.Label(new Rect(tx, r.y + 22f, tw, 16f), "RimForex_LoanLimit".Translate(
                cap.ToString("F0"), borrowed.ToString("F0"), available.ToString("F0")));

            string term = FxGameComponent.LenderTerm[idx] > 0
                ? "RimForex_LoanTerm".Translate(FxGameComponent.LenderTerm[idx].ToString())
                : "RimForex_LoanTermNone".Translate();
            Widgets.Label(new Rect(tx, r.y + 38f, tw, 16f), "RimForex_LoanRate".Translate(
                (FxGameComponent.LenderRate[idx] * 100f).ToString("0"), term));

            GUI.color = quarterInterest >= 0.5f ? new Color(1f, 0.78f, 0.55f) : Color.white;
            Widgets.Label(new Rect(tx, r.y + 54f, tw, 16f),
                "RimForex_LoanQuarterDue".Translate(quarterInterest.ToString("F0")));
            GUI.color = Color.white;

            Widgets.Label(new Rect(tx, r.y + 70f, tw, 16f),
                "RimForex_LoanOwed".Translate(owed.ToString("F0")));
            Text.Font = GameFont.Small;

            // ── 第二行：简介（整行宽度，约 4 行小字）──
            float descY = r.y + Icon + 6f;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.80f, 0.82f, 0.86f);
            Widgets.Label(new Rect(r.x, descY, r.width, 74f),
                FxGameComponent.LenderDescKeys[idx].Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // ── 第三行：输入 + 借款 / 还款 ──
            float by = r.yMax - 26f;
            const float InputW = 90f;
            float btnW = (r.width - InputW - Gap * 2f) * 0.5f;

            Rect inputRect = new Rect(r.x, by, InputW, 24f);
            Widgets.TextFieldNumeric(inputRect, ref amount[idx], ref amountBuf[idx], 0f, 1000000f);

            Rect borrowRect = new Rect(inputRect.xMax + Gap, by, btnW, 24f);
            Rect repayRect = new Rect(borrowRect.xMax + Gap, by, btnW, 24f);

            bool canBorrow = available >= 1f && account.balance >= 0f;
            if (!canBorrow) GUI.color = new Color(0.55f, 0.55f, 0.55f);
            if (Widgets.ButtonText(borrowRect, "RimForex_LoanBorrow".Translate()) && canBorrow)
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
            GUI.color = Color.white;

            bool canRepay = owed >= 0.5f && account.balance >= 0.5f;
            if (!canRepay) GUI.color = new Color(0.55f, 0.55f, 0.55f);
            if (Widgets.ButtonText(repayRect, "RimForex_LoanRepay".Translate()) && canRepay)
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
                Messages.Message("RimForex_LoanRepaid".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
            GUI.color = Color.white;

            TooltipHandler.TipRegion(borrowRect, "RimForex_LoanBorrowTip".Translate());
            TooltipHandler.TipRegion(repayRect, "RimForex_LoanRepayTip".Translate());
        }
    }
}
