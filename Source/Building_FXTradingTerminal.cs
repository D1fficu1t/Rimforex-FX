using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimForex
{
    /// <summary>
    /// 可建造的 FX 交易终端。玩家通过它的 gizmo 打开交易界面。
    /// Alpha 阶段用 gizmo 而不是自定义 ITab，避免 tab 注册失败导致检视面板出错。
    /// </summary>
    public class Building_FXTradingTerminal : Building
    {
        private FxAccount Account
        {
            get
            {
                var fx = Find.World.GetComponent<FxGameComponent>();
                if (fx == null) return null;
                fx.EnsureAllFactionCurrencies();
                // 用 ColonyAccount：终端建在营地 / 临时地图上时回退到殖民地账户，
                // 既不会为那张图建幽灵账户，也不会让窗口拿到 null。
                return fx.ColonyAccount(Map);
            }
        }

        /// <summary>终端是否可用：还活着、在地上、且通着电。</summary>
        ///
        /// 这个判据原来只在 gizmo 上用过（不满足就把"打开终端"按钮禁用），
        /// 于是**已经打开的窗口**在断电或拆除后仍然能继续下单
        /// —— 250W 的建造门槛等于可以绕过。窗口每帧都会重新查一次（见 Window_FxTerminal）。
        public bool Usable => Spawned && !Destroyed && Powered;

        private bool Powered
        {
            get
            {
                // Building.PowerComp 是 CompPower 基类，PowerOn 只在 CompPowerTrader 上，需自行获取。
                var p = GetComp<CompPowerTrader>();
                return p == null || p.PowerOn;
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            if (Faction != null && Faction.IsPlayer)
            {
                var open = new Command_Action
                {
                    defaultLabel = "RimForex_OpenTerminal".Translate(),
                    defaultDesc = "RimForex_OpenTerminalDesc".Translate(),
                    icon = TexCommand.OpenLinkedQuestTex,
                    action = OpenTerminal
                };
                if (!Powered)
                {
                    open.Disable("RimForex_NoPower".Translate());
                }
                yield return open;
            }
        }

        private void OpenTerminal()
        {
            var account = Account;
            if (account == null) return;
            Find.WindowStack.Add(new Window_FxTerminal(this, account));
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            var fx = Find.World.GetComponent<FxGameComponent>();
            if (fx == null) return s;
            var acc = fx.ColonyAccount(Map);
            if (acc == null) return s;
            string line = "RimForex_InspectAccount".Translate(acc.balance.ToString("F0"), acc.MarginUsed.ToString("F0"), acc.FloatingPnl.ToString("F0"));
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }
    }
}
