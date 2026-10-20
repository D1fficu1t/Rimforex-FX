# 更新记录 / Changelog

## 0.2.2

### 中文

**平衡调整**

- **强平线从「剩 50% 保证金」下调到「剩 30%」。** 之前仓位死得太早、没节目效果。
  预警线同步从 60% 调到 45%。100x 下爆仓距离从约 4.6 根报价拉长到 7.2 根（+57%），
  代价是穿仓（负余额）概率上升 —— 这是刻意的。
- **修复高波动品种的 100x 会被静默降档成 50x。**
  最大安全杠杆的公式把「到强平线还剩多少余量」算成了强平线本身，
  在旧值 50% 时两者恰好相等，所以一直没暴露。改强平线必须先修这个公式。

**新增**

- **悬浮盈亏窗**：关掉交易终端去干别的时，屏幕角落会显示当前持仓的浮动盈亏。
  可以用鼠标左键拖动，位置会记住。在设置页可关闭。
  窗口尺寸按实际数字自动调整，不会被切掉。
- **设置页顶部显示版本号**（形如 `0.2.2 (a1b2c3d)`）——
  报错时附上这一行就知道装的是哪一版。

**修复**

- **K 线的爆仓线画错了位置**（约在实际位置的 3/7 处）。
  它用的距离公式把「到强平线还剩多少余量」算成了强平线本身 ——
  在旧值 50% 时两者恰好相等，所以一直没暴露；改成 30% 后
  表现为「价格早就越过爆仓线了，却还没被强平」。实盘判据一直是对的，错的只是显示。
  下单区那句「强平距离约 X%」也是同一个错误，一并修正。
- **殖民地交易盈亏的心情不再影响囚犯、访客与任务寄宿者。**
  之前只看「在哪张地图」，把同地图所有有人形思维的角色都算进去了。
  现在只作用于**自由殖民者**（奴隶不算）。
- **货币列表的「持仓/盈亏」列改为只显示盈亏。**
  持仓单位数能到四位数，会把后面的盈亏挤出格子；单位数移到了悬停提示里。
- **行情积压丢弃时不再完全静默** —— 现在会在日志里留下警告，说明丢了多少步。
- 提取白银不再乘以白银市价（白银本身就是记账单位，那是个恒为 1 的假换算）。
- 随机数种子的 Push/Pop 全部补上 try/finally，避免异常后污染全局随机序列。

### English

**Balance**

- **Stop-out moved from "50% of margin left" down to 30%.** Positions died too early to be
  interesting. The margin-call warning moved from 60% to 45%. At 100x the distance to
  liquidation goes from ~4.6 quotes to 7.2 (+57%); a negative balance becomes more likely,
  which is intended.
- **Fixed high-volatility currencies being silently capped to 50x.**
  The max-safe-leverage formula used the stop-out fraction itself as the "room left before
  liquidation". At the old value of 50% the two happened to be equal, hiding the bug.

**Added**

- **Floating P/L window**: shows the floating P/L of your open positions in a screen corner
  while the terminal is closed. Drag with the left mouse button; the position is remembered.
  Can be turned off in the settings. The box sizes itself to the number, so nothing is clipped.
- **Version string in the settings page** (e.g. `0.2.2 (a1b2c3d)`) — include it in bug reports.

**Fixed**

- **The stop-out line on the chart was drawn in the wrong place** (about 3/7 of the way to the
  real one). Its distance formula used the stop-out fraction itself instead of the room left
  before liquidation — identical at the old 50%, so it stayed hidden until the threshold moved
  to 30%. Symptom: the price sails past the dashed line without being liquidated. The actual
  liquidation check was always correct; only the display was wrong. The "stop-out distance
  ~X%" line in the order panel had the same bug and is fixed too.
- **Colony P/L mood no longer affects prisoners, guests or quest lodgers.** It used to check
  only "which map", catching every humanlike thinker there. Now free colonists only
  (slaves excluded).
- **The "positions / P&L" column now shows P/L only.** Position sizes reach four digits and
  pushed the P/L out of the cell; the unit count moved into the tooltip.
- **Dropped market steps now log a warning** instead of failing silently.
- Silver withdrawal no longer multiplies by silver's market value (silver *is* the unit of account).
- Every random-seed push now has a `try/finally`, so an exception can't corrupt the global RNG.

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
