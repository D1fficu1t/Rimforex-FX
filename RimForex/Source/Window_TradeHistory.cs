using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 交易历史窗口。显示最近若干次已平仓交易：盈亏、保证金、手续费。
    /// 全部使用原版 Widgets 控件，与主终端保持一致的画风。
    /// </summary>
    public class Window_TradeHistory : Window
    {
        private const float RowH = 30f;

        private readonly FxAccount account;
        private Vector2 scroll;
        /// <summary>显示最近多少笔。0 = 全部。默认 10。</summary>
        private int showCount = 10;
        /// <summary>是否正在等待"清除全部记录"的二次确认。</summary>
        private bool confirmClear;

        public override Vector2 InitialSize => new Vector2(880f, 560f);

        public Window_TradeHistory(FxAccount account)
        {
            this.account = account;
            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = true;
            onlyOneOfTypeAllowed = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (account == null)
            {
                Widgets.Label(inRect, "RimForex_NoMarket".Translate());
                return;
            }

            // 标题栏（窗口自带关闭按钮占位）
            Rect content = new Rect(inRect.x, inRect.y + 30f, inRect.width, inRect.height - 30f);

            // ---- 汇总区 ----
            // 高度要能容纳：标题(26) + 汇总文字(28起) + 展开按钮 + 复制按钮(48起+24)
            Rect summary = new Rect(content.x, content.y, content.width, 84f);
            Widgets.DrawMenuSection(summary);
            Rect sr = summary.ContractedBy(8f);

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(sr.x, sr.y, 220f, 26f), "RimForex_HistoryTitle".Translate());
            Text.Font = GameFont.Small;

            // 累计统计（全部历史，不只显示的那几条）
            float totalPnl = 0f, totalFee = 0f, totalSpread = 0f;
            int wins = 0, losses = 0;
            List<FxTradeRecord> all = account.history;
            for (int i = 0; i < all.Count; i++)
            {
                FxTradeRecord t = all[i];
                if (t == null) continue;
                totalPnl += t.pnl;
                totalFee += t.totalFee;
                totalSpread += t.spreadCost;
                if (t.pnl > 0.5f) wins++;
                else if (t.pnl < -0.5f) losses++;
            }

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.80f, 0.82f, 0.84f);
            Widgets.Label(new Rect(sr.x, sr.y + 28f, sr.width - 160f, 20f),
                "RimForex_HistorySummary".Translate(
                    all.Count.ToString(),
                    wins.ToString(),
                    losses.ToString(),
                    totalPnl.ToString("F0"),
                    totalFee.ToString("F2"),
                    totalSpread.ToString("F1")).ToString()
                .Colorize(totalPnl >= 0f ? new Color(0.55f, 0.85f, 0.60f) : new Color(1f, 0.62f, 0.55f)));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // ---- 显示条数：5 / 10 / 20 / 全部 ----
            // 放在汇总区右侧（原先"展开/收起"的位置），改成四个互斥的档位按钮，
            // 当前档位高亮。比原来的两态切换更直接。
            Text.Font = GameFont.Tiny;
            float btnW = 56f;
            float bx = sr.xMax - (btnW + 4f) * 4f + 4f;
            int[] counts = { 5, 10, 20, 0 };            // 0 = 全部
            for (int i = 0; i < counts.Length; i++)
            {
                int n = counts[i];
                Rect br = new Rect(bx, sr.y + 26f, btnW, 24f);
                bool active = showCount == n;
                if (active) Widgets.DrawHighlightSelected(br);

                string label = n == 0
                    ? "RimForex_HistoryAll".Translate(all.Count).ToString()
                    : "RimForex_HistoryN".Translate(n).ToString();

                if (Widgets.ButtonText(br, label)) showCount = n;

                string tip = n == 0
                    ? "RimForex_HistoryTipAll".Translate(all.Count).ToString()
                    : "RimForex_HistoryTipN".Translate(n).ToString();
                TooltipHandler.TipRegion(br, tip);
                bx += btnW + 4f;
            }
            Text.Font = GameFont.Small;

            // ---- 复制交易报告 ----
            // 复制【当前显示范围】内的交易 —— 选"最近 5 笔"就只复制这 5 笔，
            // 与列表所见即所得。早期无论选几笔都导出全部，和界面显示的数量不一致。
            Rect copyBtn = new Rect(sr.x, sr.y + 48f, 168f, 24f);
            int copyCount = showCount == 0 ? all.Count : Mathf.Min(showCount, all.Count);
            if (Widgets.ButtonText(copyBtn, "RimForex_HistoryCopy".Translate()))
            {
                CopyReport(all, copyCount);
            }
            TooltipHandler.TipRegion(copyBtn, "RimForex_HistoryCopyTip".Translate(copyCount));
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(copyBtn.xMax + 8f, copyBtn.y + 4f, sr.width - copyBtn.width - 8f, 18f),
                "RimForex_HistoryCopyHint".Translate(copyCount));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // ---- 清除全部记录 ----
            // 二次确认：清空是不可逆的，误点代价太大。
            // 只清 history；余额、持仓、手续费累计都不受影响。
            Rect clearBtn = new Rect(copyBtn.xMax + 8f, copyBtn.y, 150f, 24f);
            if (Widgets.ButtonText(clearBtn, "RimForex_HistoryClear".Translate()))
            {
                confirmClear = true;
            }
            TooltipHandler.TipRegion(clearBtn, "RimForex_HistoryClearTip".Translate(all.Count));

            if (confirmClear)
            {
                Rect warn = new Rect(sr.x, sr.y + 76f, sr.width, 64f);
                Widgets.DrawBoxSolid(warn, new Color(0.30f, 0.12f, 0.10f, 0.9f));
                Rect wr = warn.ContractedBy(6f);
                Text.Font = GameFont.Small;
                GUI.color = new Color(1f, 0.72f, 0.66f);
                Widgets.Label(new Rect(wr.x, wr.y, wr.width, 20f),
                    "RimForex_HistoryClearConfirm".Translate(all.Count));
                GUI.color = Color.white;

                Rect yes = new Rect(wr.x, wr.y + 24f, 110f, 26f);
                Rect no = new Rect(yes.xMax + 6f, yes.y, 110f, 26f);
                if (Widgets.ButtonText(yes, "RimForex_HistoryClearYes".Translate()))
                {
                    all.Clear();
                    confirmClear = false;
                }
                if (Widgets.ButtonText(no, "RimForex_HistoryClearNo".Translate()))
                {
                    confirmClear = false;
                }
            }

            // ---- 列表 ----
            float listY = summary.yMax + (confirmClear ? 76f : 8f);
            Rect listZone = new Rect(content.x, listY, content.width, content.yMax - listY);
            Widgets.DrawMenuSection(listZone);
            Rect lr = listZone.ContractedBy(8f);

            // 列头
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(lr.x + 2f, lr.y, 130f, 18f), "RimForex_HColPair".Translate());
            Widgets.Label(new Rect(lr.x + 136f, lr.y, 52f, 18f), "RimForex_HColDir".Translate());
            Widgets.Label(new Rect(lr.x + 192f, lr.y, 72f, 18f), "RimForex_HColUnits".Translate());
            Widgets.Label(new Rect(lr.x + 268f, lr.y, 120f, 18f), "RimForex_HColRates".Translate());
            Widgets.Label(new Rect(lr.x + 392f, lr.y, 82f, 18f), "RimForex_HColMargin".Translate());
            Widgets.Label(new Rect(lr.x + 478f, lr.y, 72f, 18f), "RimForex_HColFee".Translate());
            Widgets.Label(new Rect(lr.x + 554f, lr.y, 78f, 18f), "RimForex_HColSpread".Translate());
            Widgets.Label(new Rect(lr.x + 636f, lr.y, 90f, 18f), "RimForex_HColPnl".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect outRect = new Rect(lr.x, lr.y + 20f, lr.width, lr.height - 20f);
            if (outRect.height <= 0f) return;

            int shown = showCount == 0 ? all.Count : Mathf.Min(showCount, all.Count);
            float viewH = Mathf.Max(outRect.height, shown * RowH + 4f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 18f, viewH);

            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            if (shown == 0)
            {
                Widgets.Label(new Rect(2f, 4f, viewRect.width, 20f), "RimForex_HistoryEmpty".Translate());
            }

            // 最新的在最上面
            float y = 0f;
            for (int n = 0; n < shown; n++)
            {
                FxTradeRecord t = all[all.Count - 1 - n];
                if (t == null) continue;

                Rect row = new Rect(0f, y, viewRect.width, RowH);
                if (n % 2 == 1) Widgets.DrawLightHighlight(row);

                string dir = t.isLong ? "RimForex_Long".Translate() : "RimForex_Short".Translate();
                if (t.wasLiquidation)
                {
                    dir = dir.Colorize(new Color(1f, 0.5f, 0.45f));
                }

                Widgets.Label(new Rect(row.x + 2f, row.y + 6f, 132f, 20f), t.pairLabel.NullOrEmpty() ? t.currencyName : t.pairLabel);
                Widgets.Label(new Rect(row.x + 136f, row.y + 6f, 52f, 20f), dir);
                Widgets.Label(new Rect(row.x + 192f, row.y + 6f, 72f, 20f), t.units.ToString("N0"));
                Widgets.Label(new Rect(row.x + 268f, row.y + 6f, 120f, 20f),
                    t.entryRate.ToString("F2") + " → " + t.exitRate.ToString("F2"));
                Widgets.Label(new Rect(row.x + 392f, row.y + 6f, 82f, 20f), t.margin.ToString("F0"));
                Widgets.Label(new Rect(row.x + 478f, row.y + 6f, 72f, 20f), t.totalFee.ToString("F2"));
                // 点差成本（橘色，与手续费区分开）
                Widgets.Label(new Rect(row.x + 554f, row.y + 6f, 78f, 20f),
                    t.spreadCost.ToString("F1").Colorize(new Color(0.95f, 0.75f, 0.45f)));
                Widgets.Label(new Rect(row.x + 636f, row.y + 6f, 90f, 20f),
                    ColorizeBySign(t.pnl.ToString("+0;-0;0"), t.pnl));

                y += RowH;
            }
            Widgets.EndScrollView();
        }

        /// <summary>
        /// 把交易历史导出为纯文本并写入系统剪贴板。
        ///
        /// 只导出<b>最近 count 笔</b>，与列表当前显示的范围一致（所见即所得）。
        /// 顺序也是最新的在最上面，和界面一致。
        ///
        /// 用制表符分隔，这样粘进表格软件会直接分列；同时每行自成一个完整记录，
        /// 直接贴进聊天窗口也可读。
        /// 全部数据都来自 FxTradeRecord 里已持久化的字段，不依赖运行时状态。
        /// </summary>
        private void CopyReport(List<FxTradeRecord> all, int count)
        {
            var sb = new System.Text.StringBuilder();

            if (count < 0) count = 0;
            if (count > all.Count) count = all.Count;
            // 取最近 count 笔，并翻转成"最新在前"，与列表顺序一致
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

            // 标题里注明"本次导出多少笔 / 历史共多少笔"，避免误以为丢数据
            sb.AppendLine("# RimWorld RimForex - trade report (last " + picked.Count
                + " of " + all.Count + ")");

            sb.AppendLine("# exported\t" + picked.Count + "\tof\t" + all.Count + "\ttrades");
            sb.AppendLine("# trades\t" + picked.Count
                + "\twins\t" + wins
                + "\tlosses\t" + losses
                + "\tliquidations\t" + liquidations
                + "\tblowups\t" + blowUps);
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

                sb.Append(t.pairLabel.NullOrEmpty() ? t.currencyName : t.pairLabel).Append('\t')
                  .Append(t.isLong ? "LONG" : "SHORT").Append('\t')
                  .Append(t.units.ToString("0")).Append('\t')
                  .Append(t.entryRate.ToString("F4")).Append('\t')
                  .Append(t.exitRate.ToString("F4")).Append('\t')
                  .Append(t.margin.ToString("F2")).Append('\t')
                  .Append(t.pnl.ToString("F2")).Append('\t')
                  .Append(t.totalFee.ToString("F2")).Append('\t')
                  .Append(t.spreadCost.ToString("F2")).Append('\t')
                  .Append(result).AppendLine();
            }

            // 剪贴板操作在某些平台可能受限，失败时给出提示而不是静默什么都没发生
            try
            {
                GUIUtility.systemCopyBuffer = sb.ToString();
                Messages.Message(
                    "RimForex_MsgReportCopied".Translate(picked.Count),
                    MessageTypeDefOf.TaskCompletion, false);
            }
            catch (System.Exception e)
            {
                Messages.Message(
                    "RimForex_MsgReportCopyFailed".Translate(e.Message),
                    MessageTypeDefOf.RejectInput, false);
            }
        }

        private static string ColorizeBySign(string text, float value)
            => FxUiUtil.ColorizeBySign(text, value);
    }
}
