using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimForex
{
    /// <summary>
    /// E7 收债窗口（0.3.0，D2 换新界面）。
    /// 入口：选中殖民者右键收债人。四个选项：付清 / 交人 / 拒绝动手 / 让我想想。
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

            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            forcePause = true;
            closeOnClickedOutside = false;
            closeOnAccept = true;
            closeOnCancel = true;
            FxWindowChrome.Prepare(this);
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(780f, 580f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect r = FxWindowChrome.Draw(this, inRect,
                "RimForex_Ev7WindowTitle".Translate());

            float debt = account != null ? -account.balance : 0f;
            int daysLeft = fx != null
                ? Mathf.Max(0, (fx.debtDeadlineTick - GenTicks.TicksGame) / FxGameComponent.TicksPerDay)
                : 0;

            FxTheme.Tiny(new Rect(r.x, r.y, r.width, 18f),
                "RimForex_Ev7WindowSub".Translate(debt.ToString("F0"), daysLeft.ToString()),
                FxTheme.Warning);

            Rect body = new Rect(r.x, r.y + 24f, r.width, r.height - 46f);
            if (hostageMode) DrawHostages(body);
            else DrawChoices(body);

            FxTheme.Tiny(new Rect(r.x, r.yMax - 18f, r.width, 18f),
                "RimForex_Ev7BalanceLine".Translate(
                    (account != null ? account.balance : 0f).ToString("F0")),
                FxTheme.Muted);
        }

        private void DrawChoices(Rect body)
        {
            string text = "RimForex_Ev7WindowBody".Translate();
            GameFont f0 = Text.Font;
            bool w0 = Text.WordWrap;
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float h = Text.CalcHeight(text, body.width);
            Text.Font = f0;
            Text.WordWrap = w0;
            FxTheme.Label(new Rect(body.x, body.y, body.width, h), text,
                GameFont.Small, FxTheme.TextColor, TextAnchor.UpperLeft, true);

            float y = body.y + h + 14f;
            const float W = 220f;
            const float H = 34f;

            if (FxWidgets.Button(new Rect(body.x, y, W, H),
                    "RimForex_Ev7Pay".Translate(), true, true))
            {
                if (fx != null && fx.TrySettleDebtByPayment(account)) Close();
                else Messages.Message("RimForex_Ev7NeedSilver".Translate(),
                    MessageTypeDefOf.RejectInput, false);
            }
            y += H + 10f;
            if (FxWidgets.Button(new Rect(body.x, y, W, H),
                    "RimForex_Ev7HostageBtn".Translate()))
            {
                hostageMode = true;
            }
            y += H + 10f;
            if (FxWidgets.Button(new Rect(body.x, y, W, H),
                    "RimForex_Ev7Refuse".Translate(), true, false, true))
            {
                if (fx != null) fx.RefuseDebt(account);
                Close();
                return;
            }
            y += H + 10f;
            if (FxWidgets.Button(new Rect(body.x, y, W, H),
                    "RimForex_Ev7Think".Translate()))
            {
                Close();
                return;
            }
            y += H + 10f;

            FxTheme.Tiny(new Rect(body.x, y, body.width, 42f),
                "RimForex_Ev7RefuseTip".Translate(), FxTheme.Muted);
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
            FxTheme.Tiny(new Rect(body.x, body.y, body.width, 18f),
                "RimForex_Ev7HostageSum".Translate(chosen.ToString("F0"), debt.ToString("F0")),
                chosen >= debt ? FxTheme.Long : FxTheme.Warning);

            Rect listZone = new Rect(body.x, body.y + 26f, body.width, body.height - 82f);
            int rowH = 30;
            float viewH = Mathf.Max(listZone.height, colonists.Count * rowH);
            if (Mouse.IsOver(listZone) && Event.current.type == EventType.ScrollWheel)
            {
                scroll.y += Event.current.delta.y * 24f;
                Event.current.Use();
            }
            scroll.y = Mathf.Clamp(scroll.y, 0f, Mathf.Max(0f, viewH - listZone.height));

            GUI.BeginGroup(listZone);
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn p = colonists[i];
                if (p == null) continue;
                float yy = i * rowH - scroll.y;
                if (yy + rowH < 0f || yy > listZone.height) continue;

                Rect row = new Rect(0f, yy, listZone.width - 12f, rowH);
                if (i % 2 == 1) FxTheme.Fill(row, new Color32(18, 23, 30, 255));
                bool on = selected.Contains(p);
                if (FxWidgets.Toggle(new Rect(4f, yy + 5f, 18f, 18f), on))
                {
                    if (on) selected.Remove(p); else selected.Add(p);
                }
                FxTheme.Small(new Rect(30f, yy + 5f, row.width - 120f, 20f),
                    p.LabelShortCap, FxTheme.TextColor);
                FxTheme.Small(new Rect(row.width - 86f, yy + 5f, 80f, 20f),
                    p.MarketValue.ToString("F0"), FxTheme.Muted, TextAnchor.MiddleRight);
                FxTheme.HLine(0f, row.width, yy + rowH - 1f, FxTheme.Line);
            }
            GUI.EndGroup();

            float frac = listZone.height / Mathf.Max(listZone.height, viewH);
            float maxScroll = Mathf.Max(0f, viewH - listZone.height);
            FxWidgets.ScrollBar(new Rect(listZone.xMax - 8f, listZone.y, 6f, listZone.height),
                maxScroll > 0.01f ? scroll.y / maxScroll : 0f, frac);

            float by = body.yMax - 34f;
            if (FxWidgets.Button(new Rect(body.x, by, 170f, 34f),
                    "RimForex_Ev7Confirm".Translate(), true, true))
            {
                var list = new List<Pawn>();
                foreach (var p in selected) if (p != null) list.Add(p);
                if (fx != null && fx.PayWithHostages(account, list)) Close();
                else Messages.Message("RimForex_Ev7HostageShort".Translate(),
                    MessageTypeDefOf.RejectInput, false);
            }
            if (FxWidgets.Button(new Rect(body.x + 180f, by, 120f, 34f),
                    "RimForex_Ev7Back".Translate()))
            {
                hostageMode = false;
                selected.Clear();
            }
        }
    }
}
