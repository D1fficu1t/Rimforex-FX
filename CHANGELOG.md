# 更新记录 / Changelog

## 0.2.3

### 中文

**行情与图表**

- **盘中价格现在会逐步移动，K 线影线也会反映盘内走势。**
  之前每一步的价格是瞬间跳到终点的，逐点采样根本没执行 —— 影线只记到步边界那几个点。
- **时 K / 日 K 不再出现"收盘价跑出最高最低价之外"的异常蜡烛。**
- **跳空播报修好了**：向下跳空以前一律被记成 0（价格暴跌却没有任何提示）；
  低波动货币的常规跳空也因为阈值写死 0.5% 而永远播不出来。现在按波动率判定。
- 报价提示改写：明确写出"一步之内还有若干次微调"，倒计时只显示秒数，
  并且不再被「日 K」按钮遮住。

**保证金与风控**

- **追加保证金后，预警线与强平线回到同一标尺。**
  以前可能出现"界面显示健康度 60%、离预警线还很远"却被强平。
- **游戏暂停时连开多笔，预警不再互相覆盖**（同 tick 的仓位以前会顶掉彼此的提示）。
- **预警不再刷屏**：需回到 55% 以上、且间隔 1 游戏小时，才会再次提醒。
- 穿仓之后再次爆仓，心情不再叠加成 −48。

**操作**

- **「梭哈」现在能开到真正买得起的最大量**（以前会少开一点，剩余的白银比该剩的多）。
- 终端断电或被拆除时，已打开的窗口会**自动关闭**。

**其他**

- 设置页新增「测试：常规跳空」，且常规/极端两种跳空的提示文案不再混用。
- 交易历史里的点差成本不再在每次读档后变化（旧存档里没有点差率的记录仍按 0 处理）。

### English

**Market & chart**

- **Intra-quote price movement is now real, and candle wicks reflect the path inside each quote.**
  Previously the price jumped straight to the end of each step and the per-step sampling never ran,
  so wicks only ever recorded the step boundaries.
- **Hour and day candles no longer produce impossible bars** where the close sits outside the high/low.
- **Gap announcements work again**: downward gaps used to be recorded as 0 (a price crash with no
  message at all), and normal gaps on low-volatility currencies never cleared the hard-coded 0.5%
  threshold. The threshold is now based on volatility.
- The quote-pacing hint was rewritten: it now says how many intra-quote steps there are, the
  countdown shows seconds only, and it is no longer hidden behind the Day button.

**Margin & risk**

- **Warning and stop-out now use the same scale after you add margin.**
  Previously the UI could show 60% health while the position was already past the stop-out line.
- **Warnings for several positions opened on the same tick no longer overwrite each other.**
- **Warnings no longer spam**: you have to recover above 55% and wait a game hour before another.
- A margin call right after a negative-balance event no longer stacks both mood penalties (−48).

**Controls**

- **"All in" now buys the true maximum** (it used to stop a little short, leaving more silver behind than it should).
- The terminal window **closes itself** when the building loses power or is destroyed.

**Other**

- New "Test: normal gap" button in the settings, and the two gap kinds no longer share one message text.
- Spread cost in trade history no longer changes on every save load (older records without a spread
  rate are still treated as zero).

---

## 0.2.2

### 中文

**平衡**

- **强平线从「剩 50% 保证金」下调到 30%，预警线从 60% 调到 45%。**
  仓位能活更久，穿仓（负余额）也会更常见 —— 这是有意的。
- **修复部分高波动货币用不了 100x**（杠杆会自己跳回 50x）。

**新增**

- **悬浮盈亏窗**：关掉交易终端也能在屏幕角落看到浮动盈亏。可拖动、可关闭。
- **设置页顶部显示版本号**，报错时附上这一行就行。

**修复**

- **补齐翻译：英文版不再显示中文心情，中文版不再显示英文建筑名。**
  心情的十个档位补齐了英文标签与描述，交易终端补齐了中文名。
- **任务生成的临时派系不再出现在货币列表里。**
  设置页测试区另有「清理幽灵货币」按钮，可清掉旧档里遗留的这类货币。
- **K 线的爆仓线曾画得比实际近得多**，会出现「价格越过线却没被强平」。
  现在与真实强平点一致。
- **殖民地交易盈亏的心情不再影响囚犯、访客与任务寄宿者。**
- **货币列表的盈亏列不再被持仓数量挤掉。**

### English

**Balance**

- **Stop-out moved from 50% of margin left to 30%; the margin-call warning from 60% to 45%.**
  Positions survive longer, and a negative balance becomes more common — intended.
- **Fixed high-volatility currencies being unable to use 100x** (leverage would snap back to 50x).

**Added**

- **Floating P/L window**: shows the floating P/L of your open positions in a screen corner
  while the terminal is closed. Draggable, and can be turned off in the settings.
- **Version string at the top of the settings page** — include it in bug reports.

**Fixed**

- **Localization filled in: English no longer shows Chinese mood text, and Chinese no longer
  shows the English building name.** All ten mood tiers now have English labels and
  descriptions, and the terminal has a Chinese name.
- **Temporary factions created by quests no longer appear in the market list.** A new
  "Clear ghost currencies" button in the settings test section removes leftovers from old saves.
- **The chart's stop-out line was drawn far closer than the real one**, so prices could pass it
  without triggering liquidation. It now matches the actual stop-out point.
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
