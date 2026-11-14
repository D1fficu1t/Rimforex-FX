using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimForex
{
    /// <summary>
    /// 右键收债人（0.3.3）。
    ///
    /// 0.3.0 时以为原版不能给 pawn 加右键菜单，把入口放在了终端按钮上。
    /// 后来做神秘商人时发现 FloatMenuOptionProvider 是原版自带的扩展点，
    /// 任何 pawn 都能用，于是这里补上收债人的右键入口。
    ///
    /// 只认 debtCollectorId，随从与其它平台 pawn 不会出现这一项。
    /// </summary>
    public class FloatMenuOptionProvider_DebtCollector : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;
        protected override bool RequiresManipulation => false;

        protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
        {
            if (clickedPawn == null || clickedPawn.Dead || !clickedPawn.Spawned) return null;

            FxGameComponent fx = Find.World != null
                ? Find.World.GetComponent<FxGameComponent>() : null;
            if (fx == null || !fx.IsDebtCollector(clickedPawn)) return null;

            Pawn talker = context.FirstSelectedPawn;
            if (talker == null || !talker.Spawned || talker.Map != clickedPawn.Map) return null;
            if (!talker.CanReach(clickedPawn, PathEndMode.Touch, Danger.Deadly)) return null;

            return new FloatMenuOption("RimForex_DebtCollectorTalk".Translate(), delegate
            {
                fx.BeginDebtTalk(clickedPawn, talker);
            });
        }
    }
}
