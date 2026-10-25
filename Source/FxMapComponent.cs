using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 地图层叠加：收债人头顶画一个「?」，和原版商队领队一样，
    /// 提示玩家"这个人有事找你"。
    ///
    /// 为什么只画问号、不做右键菜单：
    /// 原版给商队加"交易"选项，是 FloatMenuMakerMap 里针对带 TraderKind
    /// 且处于贸易领主的 Pawn 硬编码的。`Thing.GetFloatMenuOptions` 虽然是 virtual，
    /// 但改不了原版的 Pawn 子类，也不存在按实例挂 float menu 的办法 ——
    /// 不改原版（无 Harmony）就插不进去。
    /// 所以交互仍走终端上的「债务清算」gizmo，这里只负责视觉提示。
    ///
    /// MapComponent 的子类会被 Map 自动实例化，不需要任何 XML。
    /// </summary>
    public class FxMapComponent : MapComponent
    {
        public FxMapComponent(Map map) : base(map)
        {
        }

        public override void MapComponentOnGUI()
        {
            if (Find.World == null) return;

            var fx = Find.World.GetComponent<FxGameComponent>();
            if (fx == null || !fx.debtActive || fx.debtCollectorId < 0) return;

            var all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.thingIDNumber != fx.debtCollectorId) continue;

                CellRect view = Find.CameraDriver.CurrentViewRect;
                if (!view.Contains(p.Position)) return;

                Vector2 screen = p.DrawPos.MapToUIPosition();
                Rect r = new Rect(screen.x - 10f, screen.y - 46f, 24f, 22f);

                GUI.color = new Color(0.15f, 0.15f, 0.18f, 0.75f);
                Widgets.DrawBoxSolid(r, GUI.color);
                GUI.color = new Color(1f, 0.92f, 0.55f);
                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(r, "?");
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                return;
            }
        }
    }
}
