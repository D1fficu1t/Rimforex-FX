using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// E7 收债窗口（0.3.0）。
    ///
    /// 为什么是窗口而不是右键：原版 `Thing.GetFloatMenuOptions` 是 virtual，
    /// 但改不了原版的 Thing 子类，所以给穿梭机/运输舱加右键菜单这条路走不通。
    /// 作者已选"事件触发时直接弹窗"。
    ///
    /// 三个选项：付清 / 交人 / 拒绝。
    /// </summary>
    public class Window_FxDebt : Window
    {
        private readonly FxGameComponent fx;
        private readonly FxAccount account;

        private bool hostageMode;
        private readonly HashSet<Pawn> selected = new HashSet<Pawn>();
        private Vector2 scroll;

        public Window_FxDebt(FxGameComponent fx, FxAccount account)
        {
            this.fx = fx;
            this.account = account;

            // 作者实测：不给关、又强制暂停，玩家根本没法去筹钱。
            // 现在可以随时关掉去存银，之后从终端上的「债务清算」按钮再打开。
            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = false;
            closeOnClickedOutside = false;
            closeOnAccept = true;
            closeOnCancel = true;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(760f, 560f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            float debt = account != null ? -account.balance : 0f;
            int daysLeft = fx != null
                ? Mathf.Max(0, (fx.debtDeadlineTick - GenTicks.TicksGame) / FxGameComponent.TicksPerDay)
                : 0;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f),
                "RimForex_Ev7WindowTitle".Translate());
            Text.Font = GameFont.Small;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.95f, 0.72f, 0.60f);
            Widgets.Label(new Rect(0f, 34f, inRect.width, 18f),
                "RimForex_Ev7WindowSub".Translate(debt.ToString("F0"), daysLeft.ToString()));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect body = new Rect(0f, 58f, inRect.width, inRect.height - 58f - 34f);
            if (hostageMode) DrawHostages(body);
            else DrawChoices(body);

            // 底部：余额提示
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.72f, 0.74f, 0.78f);
            Widgets.Label(new Rect(0f, inRect.yMax - 26f, inRect.width, 20f),
                "RimForex_Ev7BalanceLine".Translate(
                    (account != null ? account.balance : 0f).ToString("F0")));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawChoices(Rect body)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, body.y, body.width, 90f),
                "RimForex_Ev7WindowBody".Translate());

            float y = body.y + 100f;
            const float W = 220f;
            const float H = 32f;

            // 付清
            if (Widgets.ButtonText(new Rect(0f, y, W, H), "RimForex_Ev7Pay".Translate()))
            {
                if (fx != null && fx.TrySettleDebtByPayment(account)) Close();
                else Messages.Message("RimForex_Ev7NeedSilver".Translate(),
                    MessageTypeDefOf.RejectInput, false);
            }
            y += H + 10f;

            // 交人
            if (Widgets.ButtonText(new Rect(0f, y, W, H), "RimForex_Ev7HostageBtn".Translate()))
            {
                hostageMode = true;
            }
            y += H + 10f;

            // 拒绝
            if (Widgets.ButtonText(new Rect(0f, y, W, H), "RimForex_Ev7Refuse".Translate()))
            {
                if (fx != null) fx.RefuseDebt(account);
                Close();
            }
            y += H + 8f;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.70f, 0.72f, 0.76f);
            Widgets.Label(new Rect(0f, y, body.width, 40f),
                "RimForex_Ev7RefuseTip".Translate());
            GUI.color = Color.white;

            GUI.color = new Color(0.62f, 0.78f, 0.92f);
            Widgets.Label(new Rect(0f, y + 46f, body.width, 40f),
                "RimForex_Ev7CanClose".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawHostages(Rect body)
        {
            float debt = account != null ? -account.balance : 0f;

            List<Pawn> colonists = new List<Pawn>();
            Map map = Find.AnyPlayerHomeMap;
            if (map != null)
            {
                var pawns = map.mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < pawns.Count; i++) colonists.Add(pawns[i]);
            }

            float chosen = 0f;
            foreach (var p in selected) if (p != null) chosen += p.MarketValue;

            Text.Font = GameFont.Tiny;
            GUI.color = chosen >= debt ? new Color(0.6f, 0.9f, 0.65f) : new Color(0.9f, 0.72f, 0.55f);
            Widgets.Label(new Rect(0f, body.y, body.width, 18f),
                "RimForex_Ev7HostageSum".Translate(chosen.ToString("F0"), debt.ToString("F0")));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect listZone = new Rect(body.x, body.y + 24f, body.width, body.height - 76f);
            GUI.BeginGroup(listZone);
            Rect view = new Rect(0f, 0f, listZone.width - 20f, Mathf.Max(listZone.height, colonists.Count * 30f));
            Widgets.BeginScrollView(new Rect(0f, 0f, listZone.width, listZone.height), ref scroll, view);

            float y = 0f;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn p = colonists[i];
                if (p == null) continue;
                bool on = selected.Contains(p);
                Rect row = new Rect(0f, y, view.width, 26f);
                if (Widgets.RadioButton(new Vector2(0f, y + 4f), on))
                {
                    if (on) selected.Remove(p); else selected.Add(p);
                }
                Widgets.Label(new Rect(26f, y + 4f, view.width - 140f, 22f), p.LabelShortCap);
                Widgets.Label(new Rect(view.width - 110f, y + 4f, 100f, 22f),
                    p.MarketValue.ToString("F0"));
                y += 30f;
            }

            Widgets.EndScrollView();
            GUI.EndGroup();

            float by = body.yMax - 30f;
            if (Widgets.ButtonText(new Rect(0f, by, 160f, 28f), "RimForex_Ev7Confirm".Translate()))
            {
                var list = new List<Pawn>();
                foreach (var p in selected) if (p != null) list.Add(p);
                if (fx != null && fx.PayWithHostages(account, list)) Close();
                else Messages.Message("RimForex_Ev7HostageShort".Translate(),
                    MessageTypeDefOf.RejectInput, false);
            }
            if (Widgets.ButtonText(new Rect(170f, by, 120f, 28f), "RimForex_Ev7Back".Translate()))
            {
                hostageMode = false;
                selected.Clear();
            }
        }
    }
}
