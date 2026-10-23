using RimWorld;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 平仓心情系统：<b>按单笔平仓盈亏</b>给全体殖民者挂心情。
    ///
    /// ── 为什么不是"每日结算"口径 ──
    /// 早先的做法是每天结算时按<b>当日盈亏</b>选一个档位。问题有两个：
    ///   ① 阈值是绝对值（±400 / ±40），账户一大就天天"平淡"，系统自己失效；
    ///   ② 一天只结算一次，体现不出"这一笔亏得有多惨" —— 而这正是玩家在意的。
    /// 现在改成每平一笔就记一笔，一次亏损立刻有反应。
    ///
    /// ── 叠加规则 ──
    /// 最多同时挂 <see cref="MaxActive"/> 条。同一档再来一笔就叠层（描述逐层升级），
    /// 不同档各自占一条。挂满后新档挤掉<b>最早</b>的那条（先进先出），
    /// 这样最新发生的事永远看得见 —— 否则亏了一万，屏幕上却还挂着三次"亏了五百"，很荒谬。
    ///
    /// ── 持续时间（自己算，不能靠原版）──
    /// 档位越重挂得越久，最长一天。小亏半天就过去了，巨亏和爆仓要一整天。
    ///
    /// ⚠️ 这里有个原版的坑：<see cref="Thought.DurationTicks"/> <b>只对 Thought_Memory 生效</b>。
    /// 本系统用的是情境想法（Thought_Situational），原版根本不读它的时长 ——
    /// 情境想法靠 <c>CurrentStateInternal()</c> 每 120 tick 重算来决定去留。
    /// 最早我覆写了 DurationTicks，结果想法永远不会消失（玩家能一直看到它挂在需求页）。
    ///
    /// 正确做法：在条目上记下发生时刻，由 worker 自己比对时长，
    /// 超时就返回 Inactive，想法随即从需求页消失。
    /// 重复叠层会把时刻刷新，所以"反复小亏"不会因为每笔都轻就一闪而过。
    ///
    /// ── 与 LLM 对话模组的约束 ──
    /// 每条描述都<b>自带完整语境</b>：点明是外汇交易、点明方向、点明量级，然后才是画面感。
    /// 原因是这些词条会被单独喂给 LLM 生成对话，缺了事件本身就会产生幻觉
    /// （比如"有人在打听沙发的价格"会被理解成殖民者想买家具，而不是刚赚了钱）。
    /// </summary>
    public static class FxMoodSystem
    {
        /// <summary>亏损档（金额为负）。索引即 tier 值。</summary>
        public const int TierHugeLoss = 0;
        public const int TierBigLoss = 1;
        public const int TierMediumLoss = 2;
        public const int TierSmallLoss = 3;
        /// <summary>盈利档（金额为正）。</summary>
        public const int TierSmallGain = 4;
        public const int TierMediumGain = 5;
        public const int TierBigGain = 6;
        public const int TierHugeGain = 7;
        /// <summary>爆仓。独立触发，不算作普通亏损。</summary>
        public const int TierMarginCall = 8;
        /// <summary>穿仓。比爆仓更重 —— 亏光保证金之外还倒欠平台钱。</summary>
        public const int TierBlowUp = 9;

        public const int TierCount = 10;

        /// <summary>同一档最多叠几层。</summary>
        public const int MaxStack = 3;

        /// <summary>
        /// 该档实际允许的叠加上限。
        /// 穿仓只允许 1 层 —— 它本来就极罕见（要极端跳空 + 高杠杆），
        /// 叠三层等于 -84，那不是"更难过"，那是直接删档。
        /// </summary>
        public static int MaxStackFor(int tier)
        {
            return tier == TierBlowUp ? 1 : MaxStack;
        }

        /// <summary>同时最多挂几条不同的档。</summary>
        public const int MaxActive = 3;

        /// <summary>每档的心情基准值（单层）。亏盈两侧都可在模组设置里整体缩放。</summary>
        public static readonly float[] TierMood =
        {
            -15f,   // 巨亏
            -10f,   // 大亏
             -7f,   // 中亏
             -5f,   // 小亏
              3f,   // 小赚
              5f,   // 中赚
              8f,   // 大赚
             12f,   // 巨赚
            -20f,   // 爆仓
            -28f,   // 穿仓：倒欠平台钱，比爆仓更重
        };

        /// <summary>
        /// 每档的持续时间（游戏日）。档位越重挂得越久，上限一天。
        /// 盈利侧刻意短一些：好心情来得快去得也快，不占着一整天的位置。
        /// </summary>
        public static readonly float[] TierDurationDays =
        {
            1.00f,   // 巨亏
            0.85f,   // 大亏
            0.65f,   // 中亏
            0.50f,   // 小亏
            0.50f,   // 小赚
            0.65f,   // 中赚
            0.85f,   // 大赚
            1.00f,   // 巨赚
            1.00f,   // 爆仓
            1.00f,   // 穿仓
        };

        /// <summary>
        /// 每档对应的 ThoughtDef。XML 里的 defName 必须与这里逐字一致
        /// （ThoughtWorker 靠 defName 反查自己代表哪一档）。
        /// </summary>
        public static readonly string[] TierDefNames =
        {
            "RimForex_FxLossHuge",
            "RimForex_FxLossBig",
            "RimForex_FxLossMedium",
            "RimForex_FxLossSmall",
            "RimForex_FxGainSmall",
            "RimForex_FxGainMedium",
            "RimForex_FxGainBig",
            "RimForex_FxGainHuge",
            "RimForex_FxMarginCall",
            "RimForex_FxBlowUp",
        };

        /// <summary>
        /// 按单笔平仓盈亏判定档位。返回 -1 表示金额为 0，不记档
        /// （0 既不算赚也不算亏，硬塞进任一边都会让描述与事实不符）。
        ///
        /// 阈值是<b>绝对白银数</b>，不随账户规模缩放 —— 这是刻意的：
        /// 玩家能直接理解"亏了一万"是什么概念，百分比反而抽象。
        /// </summary>
        public static int TierForClose(float pnl)
        {
            if (pnl > 0f)
            {
                if (pnl >= 10000f) return TierHugeGain;
                if (pnl >= 5000f) return TierBigGain;
                if (pnl >= 1000f) return TierMediumGain;
                return TierSmallGain;
            }
            if (pnl < 0f)
            {
                if (pnl <= -10000f) return TierHugeLoss;
                if (pnl <= -5000f) return TierBigLoss;
                if (pnl <= -1000f) return TierMediumLoss;
                return TierSmallLoss;
            }
            return -1;
        }

        /// <summary>
        /// 该档每层的实际心情值，已按模组设置缩放。
        /// 亏损档乘 <c>lossMoodScale</c>，盈利档乘 <c>gainMoodScale</c>，
        /// 爆仓乘 <c>marginCallMoodScale</c>。
        /// </summary>
        public static float MoodFor(int tier)
        {
            if (tier < 0 || tier >= TierCount) return 0f;
            RimForexSettings s = RimForexMod.Settings;
            float v = TierMood[tier];
            if (s == null) return v;
            if (tier == TierMarginCall) return v * s.marginCallMoodScale;
            return v * (v < 0f ? s.lossMoodScale : s.gainMoodScale);
        }

        /// <summary>
        /// 该档在给定时刻是否已经过期。
        ///
        /// <paramref name="count"/> 是层数 —— 层数越高挂得越久（每多一层 +25%），但封顶一天。
        /// </summary>
        public static bool IsExpired(int tier, int count, int sinceTick, int nowTick)
        {
            if (tier < 0 || tier >= TierCount) return true;
            int lv = count;
            if (lv < 1) lv = 1;
            if (lv > MaxStackFor(tier)) lv = MaxStackFor(tier);

            float days = TierDurationDays[tier] * (1f + 0.25f * (lv - 1));
            if (days > 1f) days = 1f;
            return nowTick - sinceTick >= (int)(days * FxGameComponent.TicksPerDay);
        }

        /// <summary>由 defName 反查档位索引；不是本系统的 def 则返回 -1。</summary>
        public static int TierOfDefName(string defName)
        {
            if (defName == null) return -1;
            for (int i = 0; i < TierDefNames.Length; i++)
            {
                if (TierDefNames[i] == defName) return i;
            }
            return -1;
        }

        /// <summary>
        /// 查出某账户上这一档的层数；没挂则返回 0。
        /// 两个子类（时长与心情）都要用，所以抽出来。
        /// </summary>
        internal static int StackOf(FxAccount acc, int tier, int nowTick)
        {
            if (acc == null || acc.moodEntries == null || tier < 0) return 0;
            for (int i = 0; i < acc.moodEntries.Count; i++)
            {
                FxMoodEntry e = acc.moodEntries[i];
                if (e == null || e.tier != tier) continue;
                if (e.ExpiredAt(nowTick)) return 0;   // 过期即视为没挂
                int lv = e.count;
                if (lv < 1) lv = 1;
                if (lv > MaxStackFor(tier)) lv = MaxStackFor(tier);
                return lv;
            }
            return 0;
        }

        /// <summary>清掉所有已过期的条目。在记录新档时顺手调用，避免列表被死条目占满。</summary>
        public static void PruneExpired(FxAccount acc, int nowTick)
        {
            if (acc == null || acc.moodEntries == null) return;
            for (int i = acc.moodEntries.Count - 1; i >= 0; i--)
            {
                FxMoodEntry e = acc.moodEntries[i];
                if (e == null || e.ExpiredAt(nowTick)) acc.moodEntries.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 一条"活跃的心情档"。最多同时存在 <see cref="FxMoodSystem.MaxActive"/> 条。
    /// </summary>
    public class FxMoodEntry : IExposable
    {
        /// <summary>档位索引，见 <see cref="FxMoodSystem"/> 的 Tier* 常量。</summary>
        public int tier;

        /// <summary>叠了几层，1 ~ <see cref="FxMoodSystem.MaxStack"/>。</summary>
        public int count = 1;

        /// <summary>
        /// 最近一次记录（或叠层刷新）的游戏 tick，用来算过期。
        /// 见 <see cref="FxMoodSystem.IsExpired"/> 里关于"为什么不用原版时长"的说明。
        /// </summary>
        public int sinceTick;

        /// <summary>是否已过期。需要当前 tick，所以不放进属性里。</summary>
        public bool ExpiredAt(int nowTick)
        {
            return FxMoodSystem.IsExpired(tier, count, sinceTick, nowTick);
        }

        public FxMoodEntry() { }

        public FxMoodEntry(int tier, int count, int sinceTick)
        {
            this.tier = tier;
            this.count = count;
            this.sinceTick = sinceTick;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref tier, "tier", 0);
            Scribe_Values.Look(ref count, "count", 1);
            Scribe_Values.Look(ref sinceTick, "sinceTick", 0);
        }
    }

    /// <summary>
    /// 把账户上记录的活跃档位挂成殖民者的情境想法。
    ///
    /// 每一档是一个独立的 ThoughtDef，所以不同档能<b>同时</b>显示。
    /// 时长与数值都按"档位 + 层数"现算，不读 XML 里的 baseMoodEffect
    /// （XML 里那两项留作占位，玩家改了模组设置的缩放倍率才生效）。
    /// </summary>
    public class Thought_FxCloseMood : Thought_Situational
    {
        /// <summary>档位索引；不是本系统的 def 则为 -1。</summary>
        private int Tier
        {
            get { return FxMoodSystem.TierOfDefName(def != null ? def.defName : null); }
        }

        private FxAccount Account
        {
            get
            {
                if (pawn == null || pawn.Map == null) return null;
                // 只对殖民地成员生效。理由见 ThoughtWorker 里的说明 ——
                // 账户是按地图索引的，不判身份的话囚犯与访客也会中招。
                if (!pawn.IsFreeColonist) return null;
                var fx = Find.World.GetComponent<FxGameComponent>();
                return fx != null ? fx.ColonyAccount(pawn.Map) : null;
            }
        }

        // ⚠️ 这里【不能】靠覆写 DurationTicks 来定时长。
        // Thought.DurationTicks 只被 Thought_Memory 读取，情境想法完全忽略它 ——
        // 早先这么写过，结果是想法永远不消失。现在过期判断在 ThoughtWorker 里做。

        /// <summary>心情值 = 该档单层值 × 层数（再乘上模组设置里的缩放）。</summary>
        public override float MoodOffset()
        {
            int tier = Tier;
            if (tier < 0) return 0f;
            int lv = FxMoodSystem.StackOf(Account, tier, GenTicks.TicksGame);
            if (lv <= 0) return 0f;
            return FxMoodSystem.MoodFor(tier) * lv;
        }
    }

    /// <summary>
    /// 决定"这一档现在挂不挂、挂第几层"。
    /// stage 索引 0/1/2 恰好对应 1/2/3 层，XML 里的描述也按这个顺序排。
    /// </summary>
    public class ThoughtWorker_FxCloseMood : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            if (p == null || p.Map == null) return ThoughtState.Inactive;

            // ── 必须先按身份过滤（2026-10-20 修）──
            //
            // 账户现在只存在于**玩家殖民地**上（见 FxGameComponent.AccountFor），
            // 但光靠这一点还不够：殖民地图上照样有囚犯、访客、商队成员、任务寄宿者，
            // 只判 p.Map != null 就往下走的话，**地图上任何有人形 ThoughtHandler 的角色**
            // 都会吃到殖民地交易盈亏的心情。作者实测发现：殖民地亏钱，囚犯心情跟着掉。
            //
            // 用 IsFreeColonist 而不是 IsColonist：
            //   IsColonist    = 玩家派系 + 人形 + (!IsSlave || guest.SlaveIsSecure)
            //   IsFreeColonist= IsColonist && HostFaction == null
            // 即：**奴隶不算**（作者决定）。奴隶是财产，不共担殖民地的盈亏。
            //
            // 注意不能用更宽松的写法（例如 p.Faction == Faction.OfPlayer），
            // 那会把"归顺的奴隶"也放进来。
            if (!p.IsFreeColonist) return ThoughtState.Inactive;

            var fx = Find.World.GetComponent<FxGameComponent>();
            if (fx == null) return ThoughtState.Inactive;

            var acc = fx.ColonyAccount(p.Map);
            if (acc == null) return ThoughtState.Inactive;

            int tier = FxMoodSystem.TierOfDefName(def != null ? def.defName : null);
            if (tier < 0) return ThoughtState.Inactive;

            int lv = FxMoodSystem.StackOf(acc, tier, GenTicks.TicksGame);
            if (lv <= 0) return ThoughtState.Inactive;

            return ThoughtState.ActiveAtStage(lv - 1);
        }
    }
}
