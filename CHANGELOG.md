# 更新记录 / Changelog

## 0.3.0

> 相对 0.2.4：新增了一整套**借贷、世界事件、收债**体系。

### 中文

**新增**

- 官方借贷服务：终端命令栏多一个入口，四家机构（2,000 / 8,000 / 10,000 / 20,000 上限），
  利息按日累计、单利不滚本金，期限 3 / 6 / 10 日与无期限。
  到期未清或连续 3 日付不出利息即违约，欠款一次性打进余额。
  借款上限随净值缩放，且按「净值 − 负债」计算，借钱不会抬高自己的额度。
- 隔夜利息：持仓每天按名义仓位收 0.2%。终端状态行直接标出当前费率。
- 托管费：账户余额超过 20,000 的部分每天收 3%。
  以前把钱存在账户里躲袭击是完全免费的。
- 费用凭证：隔夜利息、托管费、贷款利息每天都会作为一条记录写进交易历史，
  不会再出现「扣了钱却查不到」。
- 新手教程：终端底部常驻入口。五节内容加一张现画的 K 线示意图。
- 世界事件系统（可在设置里整体开关，冷却与概率都有滑块）：
  - 货币暴涨：单日（盘中实时判定）涨幅越过阈值时，中立派系两天后派商队来扫货；
    只能走轨道贸易的派系派贸易船；帝国派征收队；敌对派系则有三成到六成概率发动袭击。
  - 货币暴跌：中立派系派清库存的商队，谈价更占优势；
    敌对派系的据点会在 10 天内守备空虚，去打能少一半守军。
  - 外汇奇才：单日赚到大钱会让各派系刮目相看（好感 +6），也可能招来不速之客。
  - 战争新闻：两个敌对派系开战，接下来三天双方货币会走出缓坡。
  - 做空报复：把某个派系的货币做空到足够大并维持三天，他们会翻脸，
    每两天来一次袭击，直到你平仓。每十次袭击引发一次金融危机：
    货币永久贬值 10%，殖民地边缘会掉下无派系难民；五次危机之后该派系名存实亡，
    货币下架、持仓强制平仓，据点仍留在地图上。
  - 穿仓收债：余额为负满两天，清算部会派一名收债人和两名武装随从上门。
    你可以付清、交人抵债，或者动手。动手会招来机械族袭击并冻结账户一年，
    冻结满 30 天后仍可借款翻身。
- 平台强制清算：余额已经为负却还挂着仓位时，平台会直接按市价全部强平补债。

**修复**

- 追加保证金后，K 线上的爆仓线不会移动，而且价格跌破线也不爆仓。
  原因是画线用初始保证金、强平判据用含追加的保证金，两边差一个追加额。
- 极端跳空只会爆仓、不会穿仓：跳空的幅度被盘内子步摊平，
  价格一格格走过强平线，在中间就被平掉了。现在跳空是瞬间到位的。

**其他**

- 模组设置重排：世界事件在最上面，调试用的测试按钮折叠到最下面。

### English

**Added**

- Official lending service: four institutions (limits 2,000 / 8,000 / 10,000 / 20,000),
  simple interest accrued daily, terms of 3 / 6 / 10 days or open-ended.
  Default after the term, or after three unpaid days of interest, charges the debt to your balance.
  Limits scale with net worth and are computed as net worth minus existing debt, so borrowing
  never raises your own ceiling.
- Overnight interest: 0.2% of notional per day. The current rate is shown in the terminal
  status line.
- Custody fee: 3% per day on any balance above 20,000 silver.
  Parking silver in the account to dodge raids is no longer free.
- Fee receipts: overnight interest, custody fees and loan interest are now recorded in the
  trade history, so the money is always traceable.
- Beginner's guide: a permanent entry at the bottom of the terminal. Five sections plus a
  hand-drawn candle diagram; the honest advice is hidden behind an easter egg.
- World events (master switch, cooldown and probability sliders in the settings):
  - Currency surge: judged intraday. Neutral factions send a caravan two days later;
    factions that only trade from orbit send a trade ship; the Empire sends a tribute collector;
    hostile factions have a 30-60% chance to raid.
  - Currency crash: neutral factions send a clearance caravan and you get a bargaining edge;
    hostile settlements are left under-defended for 10 days.
  - Currency prodigy: a big one-day win raises goodwill by 6 across the board, and may attract
    uninvited guests.
  - War news: two hostile factions fight, and both currencies drift over the next three days.
  - Short-squeeze retaliation: short a faction's currency hard enough for three days and they
    turn hostile, raiding every two days until you close the position. Every ten raids triggers a
    financial crisis: the currency loses 10% permanently and factionless refugees crash-land at the
    edge of your colony. Five crises and the faction is finished - its currency is delisted and open
    positions are force-closed, though its settlements remain on the map.
  - Debt collection: two days in the red and Collections sends a collector with two armed
    escorts. Pay, hand people over, or fight. Fighting brings a mechanoid raid and a one-year
    account freeze; borrowing reopens 30 days into the freeze.
- Forced liquidation: if the balance is negative while positions are still open, the platform
  closes everything at market to cover the debt.

**Fixed**

- The stop-out line did not move after adding margin, and price could cross it without a
  liquidation. The line was drawn from the initial margin while the rule used margin including
  top-ups; both now use the same basis.
- Extreme gaps only ever caused a margin call, never a blow-up: the gap was smoothed across
  intraday sub-steps and the position was closed halfway down. Gaps now land in one step.

**Other**

- Mod settings reordered: world events on top, debug test buttons collapsed at the bottom.

---

## 0.2.4

### 中文

**新增**

- **杠杆改成滑块**，档位更细：1 / 2 / 3 / 5 / 8 / 10 / 15 / 20 / 30 / 50 / 75 / 100 倍。
  滑块拖到头就是这个货币能用的最高档。
- **浮动盈亏窗可以展开了**：点一下显示每笔持仓的盈亏，行尾能直接**平仓**；
  右上角 `>>` 打开交易终端。
- 终端底部新增「模组设置」按钮，不用退出就能改设置。
- **终端可以「重新安装」**：卸下来搬到别处装好，**不消耗材料**。
  以前想换个位置只能拆掉重建、再出一整套料。

**修复**

- **结算时不再同时蹦出两三封「盈亏 +0」的信。**
  以前看过一眼任务图、或远行队被伏击过，那张临时地图就会留下一个空账户，
  之后每 3 天跟着发一封结算信。
- **空仓又没交易的日子不再收到结算信**（以前每 3 天会补一封摘要）。
  现在只有「当天动过仓」或「收盘还有持仓」才发。
- **终端断电后不能再从浮动盈亏窗平仓。** 以前这是个后门：
  窗口关了、电也断了，却还能从角落的小窗平仓，250W 的门槛形同虚设。
- **点「最小值」后再改杠杆，保证金会跟着重算。**
  以前在 1x 点最小值得到 80，拖到 100x 仍按 80 —— 于是"最小值"开出了 100 倍规模。
  现在会跟着变成新杠杆下的最小值；手动改过金额后就不再跟随。
- 终端改成可搬运之后，主菜单会报红字（可搬运建筑缺少质量值），已修。

**其他**

- 账户区移除「累计手续费」那一项统计（交易历史里的手续费明细保留不动）。

### English

**Added**

- **Leverage is now a slider** with finer steps: 1 / 2 / 3 / 5 / 8 / 10 / 15 / 20 / 30 / 50 / 75 / 100x.
  The slider caps at the highest step the selected currency can safely carry.
- **The floating P/L panel expands**: click it to list each position with its own P/L and a
  **Close** button; the `>>` button opens the terminal.
- A "Mod settings" button in the terminal footer, so you can change settings without leaving it.
- **The terminal can be reinstalled**: uninstall it, carry it, install it elsewhere —
  **no materials spent**. Previously the only way to move it was to deconstruct and rebuild.

**Fixed**

- **Settlement no longer drops two or three "+0" letters at once.**
  Viewing a quest map (or being ambushed on the road) used to leave an empty account behind,
  which then mailed a settlement letter every three days.
- **Flat days with no trades no longer send a settlement letter** (it used to send a digest
  every three days). Now a letter only goes out if you traded that day or still hold a position.
- **An unpowered terminal can no longer be bypassed through the floating panel.** It used to be
  a back door: the window closed, the power was out, yet you could still close positions from
  the corner widget.
- **Changing leverage after pressing "Minimum" now re-applies the minimum.**
  Previously you could press it at 1x (80 silver), drag to 100x, and still open 100x size from
  80 — the button's promise was broken. It now follows the new minimum; typing a number yourself
  stops the tracking.
- Fixed a red error on the main menu after the terminal became reinstallable
  (a haulable building without a mass value).

**Other**

- Removed the "fees paid to date" stat from the account header
  (the per-trade fee detail in the trade history is unchanged).

---

## 0.2.3

### 中文

**新增**

- 设置页测试区新增「测试：常规跳空」：3 游戏时后，所有货币各来一次常规跳空
  （8~9 倍单步波动，方向随机），用来验证播报。
- 报价提示会写明「一步之内还有 N 次微调」，不再让玩家误以为报价变快了。

**修复**

- **盘中价格现在会逐步移动，K 线影线也会反映盘内走势。**
  之前每一步只在步边界记一个点，逐点采样从未执行 —— 影线信息一直是丢的。
- **时 K / 日 K 不再出现"收盘价跑到最高最低价之外"的异常蜡烛。**
- **跳空播报修好了。** 向下跳空以前一律被记成 0（价格暴跌却没有任何提示）；
  低波动货币的常规跳空也因为阈值写死 0.5% 而永远播不出来。现在按波动率判定。
- **追加保证金后，预警线与强平线回到同一标尺。**
  以前可能出现"界面显示健康度 60%、离预警线还很远"却被强平。
- **游戏暂停时连开多笔，预警不再互相覆盖**（同 tick 的仓位以前会顶掉彼此的提示，
  被顶掉的那笔还可能永久漏报）。
- **预警不再刷屏**：必须回到 55% 以上、且距上次预警满 1 游戏小时，才会再次提醒。
  （修好盘内采样后健康度每步都会小幅波动，原来的单阈值会反复触发。）
- **穿仓之后再次爆仓，心情不再叠加成 −48。**
- **「梭哈」现在能开到真正买得起的最大量**（以前会少开一点）。
- **终端断电或被拆除时，已打开的窗口会自动关闭。**
- **交易历史里的点差成本不再在每次读档后变化。**
- 报价倒计时只显示秒数，并且不再被「日 K」按钮遮住。
- 常规与极端跳空的提示文案不再混用。

### English

**Added**

- New "Test: normal gap" button in the settings test section: after 3 game hours every currency
  takes one normal gap (8-9x the single-step volatility, random direction), to verify announcements.
- The quote-pacing hint now says how many intra-quote updates there are, so the faster chart motion
  no longer looks like a faster quote rate.

**Fixed**

- **Intra-quote price movement is now real, and candle wicks reflect the path inside each quote.**
  Previously only the step boundary was sampled and the per-step sampling never ran, so wicks
  were missing all of that information.
- **Hour and day candles no longer produce impossible bars** where the close sits outside the high/low.
- **Gap announcements work again.** Downward gaps used to be recorded as 0 (a price crash with no
  message at all), and normal gaps on low-volatility currencies never cleared the hard-coded 0.5%
  threshold. The threshold is now based on volatility.
- **Warning and stop-out use the same scale after you add margin.** The UI could previously show
  60% health while the position was already past the stop-out line.
- **Warnings for several positions opened on the same tick no longer overwrite each other**
  (one of them could be lost for good).
- **Warnings no longer spam**: you must recover above 55% and wait a game hour before another one.
  (With intra-quote sampling fixed, health moves slightly every step and the old single threshold
  fired repeatedly.)
- A margin call right after a negative-balance event no longer stacks both mood penalties (-48).
- **"All in" now buys the true maximum** (it used to stop a little short).
- **The terminal window closes itself** when the building loses power or is destroyed.
- Spread cost in trade history no longer changes on every save load.
- The quote countdown now shows seconds only, and is no longer hidden behind the Day button.
- Normal and extreme gaps no longer share the same message text.

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
