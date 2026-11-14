using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 交易历史窗口（0.3.0，D2 换新界面）。
    /// 汇总、显示条数、复制报告、清除记录、逐笔列表。
    /// </summary>
    public class Window_TradeHistory : Window
    {
        private const float RowH = 28f;
        private readonly FxAccount account;
        private float scroll;
        private int showCount = 10;
        private bool confirmClear;

        public override Vector2 InitialSize => new Vector2(940f, 620f);

        public Window_TradeHistory(FxAccount account)
        {
            this.account = account;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = true;
            onlyOneOfTypeAllowed = true;
            FxWindowChrome.Prepare(this);
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (account == null)
            {
                FxWidgets.Center(inRect, "RimForex_NoMarket".Translate(), FxTheme.Muted);
                return;
            }

            Rect r = FxWindowChrome.Draw(this, inRect,
                "RimForex_HistoryTitle".Translate());
            List<FxTradeRecord> all = account.history;

            float totalPnl = 0f, totalFee = 0f, totalSpread = 0f;
            int wins = 0, losses = 0;
            for (int i = 0; i < all.Count; i++)
            {
                FxTradeRecord t = all[i];
                if (t == null) continue;
                totalPnl += t.pnl;
                if (t.isFee) continue;
                totalFee += t.totalFee;
                totalSpread += t.spreadCost;
                if (t.pnl > 0.5f) wins++;
                else if (t.pnl < -0.5f) losses++;
            }

            float summaryH = confirmClear ? 104f : 74f;
            Rect summary = new Rect(r.x, r.y, r.width, summaryH);
            FxWidgets.Panel(summary);

            FxTheme.Tiny(new Rect(summary.x + 10f, summary.y + 8f,
                    summary.width - 380f, 18f),
                "RimForex_HistorySummary".Translate(
                    all.Count.ToString(), wins.ToString(), losses.ToString(),
                    totalPnl.ToString("F0"), totalFee.ToString("F2"),
                    totalSpread.ToString("F1")),
                totalPnl >= 0f ? FxTheme.Long : FxTheme.Short);

            float btnW = 84f;
            float bx = summary.xMax - (btnW + 4f) * 4f + 4f;
            int[] counts = { 5, 10, 20, 0 };
            for (int i = 0; i < counts.Length; i++)
            {
                int n = counts[i];
                Rect br = new Rect(bx, summary.y + 8f, btnW, 24f);
                string label = n == 0
                    ? "RimForex_HistoryAll".Translate(all.Count).ToString()
                    : "RimForex_HistoryN".Translate(n).ToString();
                if (FxWidgets.Button(br, label, true, accent: showCount == n))
                {
                    showCount = n;
                }
                string tip = n == 0
                    ? "RimForex_HistoryTipAll".Translate(all.Count).ToString()
                    : "RimForex_HistoryTipN".Translate(n).ToString();
                FxTooltip.Tip(br, tip);
                bx += btnW + 4f;
            }

            int copyCount = showCount == 0 ? all.Count : Mathf.Min(showCount, all.Count);
            Rect copyBtn = new Rect(summary.x + 10f, summary.y + 40f, 168f, 24f);
            if (FxWidgets.Button(copyBtn, "RimForex_HistoryCopy".Translate()))
            {
                CopyReport(all, copyCount);
            }
            FxTooltip.Tip(copyBtn, "RimForex_HistoryCopyTip".Translate(copyCount));
            Rect clearBtn = new Rect(copyBtn.xMax + 8f, copyBtn.y, 130f, 24f);
            FxTheme.Tiny(new Rect(clearBtn.xMax + 8f, copyBtn.y + 4f,
                    Mathf.Max(40f, summary.xMax - clearBtn.xMax - 18f), 18f),
                "RimForex_HistoryCopyHint".Translate(copyCount), FxTheme.Muted);
            if (FxWidgets.Button(clearBtn, "RimForex_HistoryClear".Translate(),
                    true, false, true))
            {
                confirmClear = true;
            }
            FxTooltip.Tip(clearBtn,
                "RimForex_HistoryClearTip".Translate(all.Count));

            if (confirmClear)
            {
                Rect warn = new Rect(summary.x + 10f, summary.y + 70f,
                    summary.width - 20f, 28f);
                FxTheme.Tiny(new Rect(warn.x, warn.y, warn.width - 250f, 24f),
                    "RimForex_HistoryClearConfirm".Translate(all.Count), FxTheme.Short);
                if (FxWidgets.Button(new Rect(warn.xMax - 230f, warn.y, 110f, 26f),
                        "RimForex_HistoryClearYes".Translate(), true, false, true))
                {
                    all.Clear();
                    confirmClear = false;
                }
                if (FxWidgets.Button(new Rect(warn.xMax - 114f, warn.y, 110f, 26f),
                        "RimForex_HistoryClearNo".Translate()))
                {
                    confirmClear = false;
                }
            }

            float listY = summary.yMax + 8f;
            Rect listZone = new Rect(r.x, listY, r.width, r.yMax - listY);
            FxWidgets.Panel(listZone);

            float hx = listZone.x + 10f;
            FxTheme.Tiny(new Rect(hx, listZone.y + 8f, 130f, 18f),
                "RimForex_HColPair".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(hx + 136f, listZone.y + 8f, 52f, 18f),
                "RimForex_HColDir".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(hx + 192f, listZone.y + 8f, 72f, 18f),
                "RimForex_HColUnits".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(hx + 268f, listZone.y + 8f, 120f, 18f),
                "RimForex_HColRates".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(hx + 392f, listZone.y + 8f, 82f, 18f),
                "RimForex_HColMargin".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(hx + 478f, listZone.y + 8f, 72f, 18f),
                "RimForex_HColFee".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(hx + 554f, listZone.y + 8f, 78f, 18f),
                "RimForex_HColSpread".Translate(), FxTheme.Muted);
            FxTheme.Tiny(new Rect(hx + 636f, listZone.y + 8f, 100f, 18f),
                "RimForex_HColAmountPnl".Translate(), FxTheme.Muted);
            FxTheme.HLine(listZone.x, listZone.xMax, listZone.y + 28f, FxTheme.Line);

            Rect rows = new Rect(listZone.x + 10f, listZone.y + 32f,
                listZone.width - 24f, listZone.height - 38f);
            if (rows.height <= 0f) return;

            int shown = showCount == 0 ? all.Count : Mathf.Min(showCount, all.Count);
            float viewH = Mathf.Max(rows.height, shown * RowH);
            Event e = Event.current;
            if (Mouse.IsOver(rows) && e.type == EventType.ScrollWheel)
            {
                scroll += e.delta.y * 24f;
                e.Use();
            }
            scroll = Mathf.Clamp(scroll, 0f, Mathf.Max(0f, viewH - rows.height));

            GUI.BeginGroup(rows);
            if (shown == 0)
            {
                FxTheme.Tiny(new Rect(2f, 4f, rows.width, 20f),
                    "RimForex_HistoryEmpty".Translate(), FxTheme.Muted);
            }

            for (int n = 0; n < shown; n++)
            {
                FxTradeRecord t = all[all.Count - 1 - n];
                if (t == null) continue;
                float yy = n * RowH - scroll;
                if (yy + RowH < 0f || yy > rows.height) continue;

                if (n % 2 == 1)
                {
                    FxTheme.Fill(new Rect(0f, yy, rows.width, RowH),
                        new Color32(18, 23, 30, 255));
                }

                if (t.isFee)
                {
                    bool isDeposit = t.isCashFlow
                        ? t.pnl >= 0f
                        : t.feeLabel == "RimForex_FeeDeposit".Translate();
                    bool isWithdraw = t.isCashFlow
                        ? t.pnl < 0f
                        : t.feeLabel == "RimForex_FeeWithdraw".Translate();
                    bool isCash = isDeposit || isWithdraw;

                    Color labelColor = isDeposit ? FxTheme.Long
                        : isWithdraw ? FxTheme.Short : FxTheme.Warning;
                    FxTheme.Small(new Rect(2f, yy + 5f, 240f, 18f),
                        t.feeLabel, labelColor);
                    FxTheme.Tiny(new Rect(136f, yy + 6f, 300f, 18f),
                        isCash
                            ? (isDeposit ? "RimForex_HistoryCashIn"
                                        : "RimForex_HistoryCashOut").Translate()
                            : "RimForex_HistoryFeeRow".Translate(),
                        FxTheme.Muted);

                    string amountText = t.pnl == 0f
                        ? "—"
                        : t.pnl.ToString("+0;-0;0");
                    FxTheme.Small(new Rect(636f, yy + 5f, 100f, 18f),
                        amountText, FxTheme.PnlColor(t.pnl),
                        TextAnchor.MiddleRight);
                    FxTheme.HLine(0f, rows.width, yy + RowH - 1f, FxTheme.Line);
                    continue;
                }

                string dir = t.isLong ? "RimForex_Long".Translate()
                    : "RimForex_Short".Translate();
                Color dirColor = t.wasLiquidation ? FxTheme.Danger
                    : t.isLong ? FxTheme.Long : FxTheme.Short;
                FxTheme.Small(new Rect(2f, yy + 5f, 132f, 18f),
                    t.pairLabel.NullOrEmpty() ? t.currencyName : t.pairLabel,
                    FxTheme.TextColor);
                FxTheme.Tiny(new Rect(136f, yy + 6f, 52f, 18f), dir, dirColor);
                FxTheme.Tiny(new Rect(192f, yy + 6f, 72f, 18f),
                    t.units.ToString("N0"), FxTheme.Muted);
                FxTheme.Tiny(new Rect(268f, yy + 6f, 120f, 18f),
                    t.entryRate.ToString("F2") + " → " + t.exitRate.ToString("F2"),
                    FxTheme.Muted);
                FxTheme.Tiny(new Rect(392f, yy + 6f, 82f, 18f),
                    t.margin.ToString("F0"), FxTheme.Muted);
                FxTheme.Tiny(new Rect(478f, yy + 6f, 72f, 18f),
                    t.totalFee.ToString("F2"), FxTheme.Muted);
                FxTheme.Tiny(new Rect(554f, yy + 6f, 78f, 18f),
                    t.spreadCost.ToString("F1"), FxTheme.Warning);
                FxTheme.Small(new Rect(636f, yy + 4f, 100f, 20f),
                    t.pnl.ToString("+0;-0;0"), FxTheme.PnlColor(t.pnl),
                    TextAnchor.MiddleRight);
                FxTheme.HLine(0f, rows.width, yy + RowH - 1f, FxTheme.Line);
            }
            GUI.EndGroup();

            float frac = rows.height / Mathf.Max(rows.height, viewH);
            float maxScroll = Mathf.Max(0f, viewH - rows.height);
            FxWidgets.ScrollBar(new Rect(rows.xMax + 6f, rows.y, 6f, rows.height),
                maxScroll > 0.01f ? scroll / maxScroll : 0f, frac);
            FxTooltip.Draw();
        }

        private void CopyReport(List<FxTradeRecord> all, int count)
        {
            var sb = new System.Text.StringBuilder();
            if (count < 0) count = 0;
            if (count > all.Count) count = all.Count;
            var picked = new List<FxTradeRecord>(count);
            for (int n = 0; n < count; n++)
            {
                FxTradeRecord rec = all[all.Count - 1 - n];
                if (rec != null) picked.Add(rec);
            }

            float totalPnl = 0f, totalFee = 0f, totalSpread = 0f;
            int wins = 0, losses = 0, liquidations = 0, blowUps = 0;
            for (int i = 0; i < picked.Count; i++)
            {
                FxTradeRecord t = picked[i];
                totalPnl += t.pnl;
                totalFee += t.totalFee;
                totalSpread += t.spreadCost;
                if (t.pnl > 0.5f) wins++;
                else if (t.pnl < -0.5f) losses++;
                if (t.wasLiquidation) liquidations++;
                if (t.wasBlowUp) blowUps++;
            }

            sb.AppendLine("# RimWorld RimForex - trade report (last " + picked.Count
                + " of " + all.Count + ")");
            sb.AppendLine("# exported\t" + picked.Count + "\tof\t" + all.Count + "\ttrades");
            sb.AppendLine("# trades\t" + picked.Count
                + "\twins\t" + wins + "\tlosses\t" + losses
                + "\tliquidations\t" + liquidations + "\tblowups\t" + blowUps);
            sb.AppendLine("# totalPnl\t" + totalPnl.ToString("F2")
                + "\ttotalFee\t" + totalFee.ToString("F2")
                + "\ttotalSpreadCost\t" + totalSpread.ToString("F2"));
            sb.AppendLine("# freeMargin\t" + account.balance.ToString("F2")
                + "\tequity\t" + account.Equity.ToString("F2")
                + "\topenPositions\t" + account.positions.Count);
            sb.AppendLine("#");
            sb.AppendLine("pair\tdir\tunits\tentryRate\texitRate\tmargin\tpnl\tfee\tspreadCost\tresult");

            for (int i = 0; i < picked.Count; i++)
            {
                FxTradeRecord t = picked[i];
                string result = t.wasBlowUp ? "BLOWUP"
                    : t.wasLiquidation ? "LIQUIDATED"
                    : t.pnl > 0.5f ? "WIN"
                    : t.pnl < -0.5f ? "LOSS" : "FLAT";
                sb.Append(t.pairLabel.NullOrEmpty() ? t.currencyName : t.pairLabel)
                  .Append('\t').Append(t.isLong ? "LONG" : "SHORT")
                  .Append('\t').Append(t.units.ToString("0"))
                  .Append('\t').Append(t.entryRate.ToString("F4"))
                  .Append('\t').Append(t.exitRate.ToString("F4"))
                  .Append('\t').Append(t.margin.ToString("F2"))
                  .Append('\t').Append(t.pnl.ToString("F2"))
                  .Append('\t').Append(t.totalFee.ToString("F2"))
                  .Append('\t').Append(t.spreadCost.ToString("F2"))
                  .Append('\t').Append(result).AppendLine();
            }

            try
            {
                GUIUtility.systemCopyBuffer = sb.ToString();
                Messages.Message("RimForex_MsgReportCopied".Translate(picked.Count),
                    MessageTypeDefOf.TaskCompletion, false);
            }
            catch (System.Exception ex)
            {
                Messages.Message("RimForex_MsgReportCopyFailed".Translate(ex.Message),
                    MessageTypeDefOf.RejectInput, false);
            }
        }
    }
}
