using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 神秘商人的右键菜单（0.3.2）。
    ///
    /// RimWorld 的 FloatMenuMakerMap 会用反射扫描
    /// `FloatMenuOptionProvider` 的所有非抽象子类并自动注册，
    /// 所以这里只要继承它、放进程序集，右键菜单就会多出一项，不需要 Harmony。
    /// </summary>
    public class FloatMenuOptionProvider_Merchant : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;
        protected override bool RequiresManipulation => false;

        protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
        {
            if (clickedPawn == null || clickedPawn.Dead || clickedPawn.Destroyed) return null;
            if (clickedPawn.Map == null) return null;

            FxGameComponent fx = Find.World != null
                ? Find.World.GetComponent<FxGameComponent>() : null;
            if (fx == null || !fx.IsMysteryMerchant(clickedPawn)) return null;

            return new FloatMenuOption("RimForex_MerchantTalk".Translate(), delegate
            {
                fx.OpenMerchantWindow(clickedPawn);
            });
        }
    }
}
