# 更新记录 / Changelog

## 0.2.2

### 中文

**平衡**

- **强平线从「剩 50% 保证金」下调到「剩 30%」，预警线从 60% 调到 45%。**
  仓位能活更久，穿仓（负余额）也会更常见 —— 这是有意的。
- **修复部分高波动货币选不了 100x 的问题。** 之前杠杆会自己跳回 50x。

**新增**

- **悬浮盈亏窗**：关掉交易终端去干别的时，屏幕角落会显示当前持仓的浮动盈亏。
  左键可拖动，位置会记住；在设置页可关闭。
- **设置页顶部显示版本号**，报错时附上这一行就行。
- **设置页测试区新增「清理幽灵货币」按钮**，读完旧档后可以立刻清掉历史遗留的临时派系货币。

**修复**

- **做任务时生成的临时派系不再出现在货币列表里。** 之前 boss 团、朝圣者之类的临时派系
  会多出几种"没有实际派系"的货币，而且任务结束后也一直留着。
- **K 线的爆仓线画得比实际近得多**，会出现"价格越过线却没被强平"。
  现在线与真实强平点一致。
- **殖民地交易盈亏的心情不再影响囚犯、访客与任务寄宿者。**
- **货币列表的盈亏列不再被持仓数量挤掉。**

### English

**Balance**

- **Stop-out moved from 50% of margin left to 30%; the margin-call warning from 60% to 45%.**
  Positions survive longer, and a negative balance becomes more common — intended.
- **Fixed high-volatility currencies being unable to use 100x.** Leverage used to snap back to 50x.

**Added**

- **Floating P/L window**: shows the floating P/L of your open positions in a screen corner
  while the terminal is closed. Drag with the left mouse button; the position is remembered.
  Can be turned off in the settings.
- **Version string at the top of the settings page** — include it in bug reports.
- **A "Clear ghost currencies" button in the settings test section**, for cleaning old saves immediately.

**Fixed**

- **Temporary factions created by quests no longer appear in the market list.** Boss groups,
  pilgrims and similar temporary factions used to add "currencies" for factions that no longer
  existed, and those entries stayed forever.
- **The stop-out line on the chart was drawn far closer than the real one**, so prices could
  sail past it without triggering liquidation. It now matches the actual stop-out point.
- **Colony P/L mood no longer affects prisoners, guests or quest lodgers.**
- **The P/L column is no longer pushed out by large position sizes.**

---

## 0.2.1

### 中文

**修复**

- **每日结算不再把「存入 / 提取白银」当成交易盈亏。**
  之前提现之后，结算会报出一笔并不存在的亏损；存入则相反，会报出假盈利。
- **修复余额提取到 0 后，之后每天的结算盈亏恒为 0。**
  原因是结算基线用 0 当作「尚未建立」的标记，而 0 本身是合法的权益值。
- **修复大额保证金输入时终端卡顿。**
  下单量估算原先逐个递减，百万级单位时要跑上百万次；改为二分。
- **修复平仓后可能弹出针对已平仓仓位的「保证金不足」窗口。**
- **修复图表价格轴偶发错位。**

**改进**

- **结算信新增手续费明细**：列出当日手续费与「扣除手续费前的盈亏」，
  信里的净额可以直接和交易历史对上账。

**升级提示**

旧存档升级后的**第一次结算可能显示 0**，那一次用于重建结算基线；
从第二次结算开始恢复正常。

---

### English

**Fixed**

- **The daily settlement no longer counts deposits and withdrawals as trading profit or loss.**
  Withdrawing silver used to make the settlement report a loss that never happened;
  depositing did the opposite.
- **Fixed the daily P/L being stuck at 0 on every day after the balance was withdrawn down to zero.**
  The settlement baseline used 0 as its "not set yet" marker, but 0 is a legitimate equity value.
- **Fixed the terminal freezing when a large margin amount is entered.**
  Position sizing decremented one unit at a time — millions of iterations at large sizes; it now uses binary search.
- **Fixed a margin-call window occasionally appearing for a position that had already been closed.**
- **Fixed the chart price axis occasionally being drawn in the wrong place.**

**Improved**

- **The settlement letter now breaks down fees.** It lists the fees charged during the
  period and the P/L before fees, so the net figure can be reconciled against the trade history.

**Upgrade note**

The first settlement after upgrading an old save may read 0 — that settlement re-establishes
the baseline. Everything is normal from the second settlement onward.

---

## 0.2.0

### 中文

首个公开版本。

### English

First public release.
