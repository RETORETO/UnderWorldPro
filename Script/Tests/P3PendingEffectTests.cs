using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·宿主种类前提（`PrereqKind.HostKind` / `HostIsCouncil`·`HostIsCharacter`）+ 一次性待触发承诺
    ///（`EffectData.Pending` / `PendingEffectSpec`）→ JC082 索亚之书】
    /// 卡文：选择一项： ·借阅：结附于目标角色 持续：受此结附的角色获得［白眼］。
    ///                 ·收藏：结附于本方秘社 快速行动2，横置：本回合中，当你赢得下一次调查对抗时，你可以抓一张牌。
    ///
    /// 口径：「选择的一项」由**结附目标种类**体现（两个选项本身就是"结附于角色"/"结附于秘社"）；
    /// 因此两条效果各加**宿主种类前提**：借阅的授予只对角色宿主生效，收藏的激活能力只对秘社宿主可用。
    ///
    /// 承诺机制：效果声明 `Pending` → 结算时**只登记承诺**（不立即做事）；对应时机首次出现且归属该玩家时
    /// **兑现一次**（走正常触发管线并立即消费）；`Expire="本回合结束"` 到回合结束自动作废。
    /// </summary>
    public class P3PendingEffectTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";
        private const string CouncilDir = "res://Cards/Data/CouncilCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static List<string> Areas(GameInitializer game) => game.AreaManager.GetOrderedAreaUids();

        private static PlayerCardData Probe(string uid, int investigation = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
                Investigation = investigation,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_pe_asset_{playerId}_{i}");
                tmpl.CategoryFlag = CardCategory.Attachment;
                tmpl.Colors.Add(color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>把附属结附到宿主（模拟生产路径的结附落地 + 持续效果登记）。</summary>
        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? Areas(game)[0], attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }

        /// <summary>造一张秘社实例（P1）。</summary>
        private static CardInstance MakeCouncil(GameInitializer game, int playerId)
        {
            var councilTmpl = Godot.ResourceLoader.Load<CouncilCardData>(CouncilDir + "MSJC001_weimushouwang.tres");
            var inst = game.CardManager.CreateCardInstance(councilTmpl, playerId);
            game.ZoneManager.MoveToCouncil(inst, playerId);
            return inst;
        }

        private sealed class EnterAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onEnterStack?.Invoke(triggers);

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        /// <summary>对抗窗口桩：立即"关闭窗口"。</summary>
        private sealed class InstantCombatWindow
        {
            private readonly GameInitializer _game;
            public InstantCombatWindow(GameInitializer game)
            {
                _game = game;
                _game.CombatManager.OnCombatWindowRequired += OnWindow;
            }

            private void OnWindow(CombatManager.CombatType type, int winner, int diff, int areaNo, System.Action onResolved)
                => onResolved?.Invoke();

            public void Detach() => _game.CombatManager.OnCombatWindowRequired -= OnWindow;
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        private static StackEntry FindEntry(GameInitializer game, string effectIdPrefix)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.EffectDataEffect?.EffectId != null && e.EffectDataEffect.EffectId.StartsWith(effectIdPrefix)) return e;
            return null;
        }

        /// <summary>布置一个"P1 赢得调查对抗"的场面（P1 探针调查 2 vs P2 探针 0）。</summary>
        private static void SetupInvestigationWin(GameInitializer game, string area)
        {
            var strong = game.CardManager.CreateCardInstance(Probe("TS_pe_strong", 2), 1);
            game.ZoneManager.MoveToArea(strong, area, 1);
            var weak = game.CardManager.CreateCardInstance(Probe("TS_pe_weak", 0), 2);
            game.ZoneManager.MoveToArea(weak, area, 2);
        }

        // ==================== ① 结构 ====================

        public void TestPending_JC082结构()
        {
            var tmpl = Load("JC082_suoyazhishu");
            Assert.IsNotNull(tmpl, "JC082 模板应可加载");
            Assert.AreEqual(2, tmpl.TargetRules.Count, "打出目标＝『目标角色』或『本方秘社』两条规则（选择的一项）");

            var lend = FindEffect(tmpl, "JC082_A_LendGrantWhiteEye");
            Assert.IsNotNull(lend, "应有『借阅：宿主获得白眼』持续效果");
            Assert.IsTrue(lend.GrantToHost, "授予落点＝结附宿主");
            Assert.AreEqual("白眼", lend.ContinuousGrants[0].Value, "授予白眼");
            Assert.IsNotNull(lend.PrereqSpec, "应有宿主种类前提");
            Assert.AreEqual(PrereqKind.HostKind, lend.PrereqSpec.Kind, "前提＝宿主种类");
            Assert.AreEqual("角色", lend.PrereqSpec.Param, "仅『角色』宿主生效（借阅）");

            var keep = FindEffect(tmpl, "JC082_B_KeepPledgeDrawOnInvestigationWon");
            Assert.IsNotNull(keep, "应有『收藏：承诺抓牌』激活");
            Assert.AreEqual(EffectTiming.AttachmentActivated, keep.Timing, "附属激活");
            Assert.AreEqual(2, keep.Cost, "卡文『快速行动2』→ 费用 2");
            Assert.IsTrue(keep.ActivateCondition.Contains("横置本角色"), "横置本书为代价");
            Assert.AreEqual("HostIsCouncil", keep.Condition.Predicate, "只有结附在秘社上时才可发动");
            Assert.IsNotNull(keep.Pending, "效果＝登记一次性承诺");
            Assert.AreEqual(EffectTiming.OnInvestigationWon, keep.Pending.Timing, "承诺时机＝赢得调查对抗");
            Assert.AreEqual("本回合结束", keep.Pending.Expire, "到期＝本回合结束");
            Assert.IsTrue(keep.Pending.Optional, "卡文『你可以』→ 可选");
            Assert.AreEqual(ActionVerb.Draw, keep.Pending.Actions[0].Verb, "兑现＝抓牌");
        }

        // ==================== ② 宿主种类：谓词与授予 ====================

        public void TestPending_宿主种类谓词()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var chr = game.CardManager.CreateCardInstance(Probe("TS_pe_host"), 1);
            game.ZoneManager.MoveToArea(chr, area, 1);
            var council = MakeCouncil(game, 1);

            var toChr = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, chr, toChr);
            Assert.IsTrue(EffectEngine.Instance.EvaluateCondition("HostIsCharacter", toChr, null, null, -1, null),
                "结附于角色 → HostIsCharacter");
            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("HostIsCouncil", toChr, null, null, -1, null),
                "结附于角色 → 不是 HostIsCouncil");

            var toCouncil = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, council, toCouncil);
            Assert.IsTrue(EffectEngine.Instance.EvaluateCondition("HostIsCouncil", toCouncil, null, null, -1, null),
                "结附于秘社 → HostIsCouncil");
            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("HostIsCharacter", toCouncil, null, null, -1, null),
                "结附于秘社 → 不是 HostIsCharacter");

            var unattached = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            game.ZoneManager.MoveToAttachedZone(unattached, area, 1);
            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("HostIsCouncil", unattached, null, null, -1, null),
                "无宿主 → 两个谓词都不满足");
        }

        /// <summary>借阅：结附于**角色** → 宿主获得白眼；结附于**秘社** → 秘社不获得（借阅形态不成立）。</summary>
        public void TestPending_借阅只对角色宿主授予白眼()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var chr = game.CardManager.CreateCardInstance(Probe("TS_pe_host2"), 1);
            game.ZoneManager.MoveToArea(chr, area, 1);
            var book1 = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, chr, book1);
            Assert.IsTrue(chr.HasIcon("白眼"), "角色宿主获得白眼（借阅）");
            Assert.IsFalse(book1.HasIcon("白眼"), "书自身不获得（授予落点＝宿主）");

            var council = MakeCouncil(game, 1);
            var book2 = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, council, book2);
            Assert.IsFalse(council.HasIcon("白眼"), "秘社宿主**不**获得白眼（前提『宿主是角色』不满足）");
        }

        // ==================== ③ 收藏的激活能力只在秘社宿主上可用 ====================

        public void TestPending_收藏能力仅秘社宿主可发动()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)2);      // JC082 颜色忠诚 2:1 + 费用 2
            game.TurnManager.PriorityPlayerId = 1;

            // 结附于**角色** → 该能力不可发动
            var chr = game.CardManager.CreateCardInstance(Probe("TS_pe_host3"), 1);
            game.ZoneManager.MoveToArea(chr, area, 1);
            var bookOnChr = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, chr, bookOnChr);

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            Assert.IsFalse(ActivateEligibility.CanActivate(bookOnChr, game.TurnManager, game.PlayerManager, game.CardManager),
                "结附于角色 → 收藏能力不可发动（UI 置灰）");
            var keepOnChr = FindEffect(bookOnChr.PlayerTemplate, "JC082_B_KeepPledgeDrawOnInvestigationWon");
            EffectEngine.Instance.ActivateAbility(bookOnChr, keepOnChr, null);
            Assert.IsTrue(game.StackManager.IsEmpty, "结附于角色 → 拒绝发动");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "被拒 → 不扣费");
            Assert.IsFalse(bookOnChr.IsTapped, "被拒 → 不横置");
            Assert.AreEqual(0, EffectEngine.Instance.PendingEffectCount, "被拒 → 未登记承诺");

            // 结附于**秘社** → 可发动
            var council = MakeCouncil(game, 1);
            var bookOnCouncil = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, council, bookOnCouncil);
            Assert.IsTrue(ActivateEligibility.CanActivate(bookOnCouncil, game.TurnManager, game.PlayerManager, game.CardManager),
                "结附于秘社 → 收藏能力可发动");
        }

        // ==================== ④ 承诺：登记 → 兑现一次 → 消费 ====================

        /// <summary>激活 → 只登记承诺（不立即抓牌）；赢得调查对抗 → 兑现为触发 → 抓一张 → 承诺被消费。</summary>
        public void TestPending_承诺兑现一次()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)2);
            var council = MakeCouncil(game, 1);
            var book = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, council, book);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            SetupInvestigationWin(game, area);

            int handBefore = game.TurnManager.GetHandOf(1).Count;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var keep = FindEffect(book.PlayerTemplate, "JC082_B_KeepPledgeDrawOnInvestigationWon");
            EffectEngine.Instance.ActivateAbility(book, keep, null);
            Assert.IsFalse(game.StackManager.IsEmpty, "激活入堆叠");
            ResolveStackOnce(game);

            Assert.AreEqual(1, EffectEngine.Instance.PendingEffectCount, "结算后**登记了一份承诺**");
            Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "登记承诺本身**不抓牌**");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "支付 2 费");
            Assert.IsTrue(book.IsTapped, "横置本书（代价）");

            // 赢得调查对抗 → 兑现
            var win = new InstantCombatWindow(game);
            try
            {
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);
                Assert.IsNotNull(FindEntry(game, "Pending_"), "承诺兑现为触发并入堆叠");
                Assert.AreEqual(0, EffectEngine.Instance.PendingEffectCount, "兑现即**消费**（『下一次』只一次）");
                ResolveStackOnce(game);
                Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "兑现后抓了 1 张");
            }
            finally
            {
                win.Detach();
            }

            // 第二次赢得调查对抗 → 不再兑现
            int handAfter = game.TurnManager.GetHandOf(1).Count;
            var win2 = new InstantCombatWindow(game);
            try
            {
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);
                ResolveStackOnce(game);
                Assert.AreEqual(handAfter, game.TurnManager.GetHandOf(1).Count, "第二次不再抓牌（承诺已消费）");
            }
            finally
            {
                win2.Detach();
            }
        }

        /// <summary>输掉对抗不兑现（承诺仍在表里）。</summary>
        public void TestPending_输掉对抗不兑现()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)2);
            var council = MakeCouncil(game, 1);
            var book = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, council, book);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            // P2 调查 3 → P2 赢
            var weak = game.CardManager.CreateCardInstance(Probe("TS_pe_weak2", 0), 1);
            game.ZoneManager.MoveToArea(weak, area, 1);
            var strong = game.CardManager.CreateCardInstance(Probe("TS_pe_strong2", 3), 2);
            game.ZoneManager.MoveToArea(strong, area, 2);

            var keep = FindEffect(book.PlayerTemplate, "JC082_B_KeepPledgeDrawOnInvestigationWon");
            EffectEngine.Instance.ActivateAbility(book, keep, null);
            ResolveStackOnce(game);
            Assert.AreEqual(1, EffectEngine.Instance.PendingEffectCount, "前置：已登记承诺");

            var win = new InstantCombatWindow(game);
            try
            {
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);
                Assert.IsNull(FindEntry(game, "Pending_"), "本方未获胜 → 不兑现");
                Assert.AreEqual(1, EffectEngine.Instance.PendingEffectCount, "承诺仍留存（未被消费）");
            }
            finally
            {
                win.Detach();
            }
        }

        /// <summary>回合结束到期作废：之后再赢得对抗也不再兑现。</summary>
        public void TestPending_回合结束作废()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)2);
            var council = MakeCouncil(game, 1);
            var book = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, council, book);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            SetupInvestigationWin(game, area);

            var keep = FindEffect(book.PlayerTemplate, "JC082_B_KeepPledgeDrawOnInvestigationWon");
            EffectEngine.Instance.ActivateAbility(book, keep, null);
            ResolveStackOnce(game);
            Assert.AreEqual(1, EffectEngine.Instance.PendingEffectCount, "前置：已登记承诺");

            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);
            Assert.AreEqual(0, EffectEngine.Instance.PendingEffectCount, "回合结束 → 未兑现的承诺作废");

            int handBefore = game.TurnManager.GetHandOf(1).Count;
            var win = new InstantCombatWindow(game);
            try
            {
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);
                ResolveStackOnce(game);
                Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "作废后即使获胜也不抓牌");
            }
            finally
            {
                win.Detach();
            }
        }

        /// <summary>承诺兑现时是**可选**触发（卡文"你可以"）：非强制 → 触发条目 Forced=false。</summary>
        public void TestPending_兑现为可选触发()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)2);
            var council = MakeCouncil(game, 1);
            var book = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            Attach(game, council, book);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            SetupInvestigationWin(game, area);

            var keep = FindEffect(book.PlayerTemplate, "JC082_B_KeepPledgeDrawOnInvestigationWon");
            EffectEngine.Instance.ActivateAbility(book, keep, null);
            ResolveStackOnce(game);

            var win = new InstantCombatWindow(game);
            try
            {
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);
                var entry = FindEntry(game, "Pending_");
                Assert.IsNotNull(entry, "承诺已兑现为触发");
                Assert.IsFalse(entry.EffectDataEffect.Forced, "『你可以』→ 非强制（可在排序窗放弃）");
            }
            finally
            {
                win.Detach();
            }
        }
    }
}
