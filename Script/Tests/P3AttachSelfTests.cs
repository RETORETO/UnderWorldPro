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
    /// 【P3·K12】JC121 空鸣御守①：**进场触发：将空鸣御守结附于位于本地区的目标本方角色**
    /// （"附属进场时自行选定结附目标"这条死角）。
    ///  · 本批实现 `ActionVerb.Attach` 执行器（原先只有 TODO）：把**来源卡自己**结附到宿主上
    ///    （`IsAttached`/`AttachedTo`/宿主 `AttachedCards`/`MoveToAttachedZone(卡, 宿主所在地区, 控制者)`）；
    ///  · 目标＝角色 + `Friendly`（本方）+ `LocalZone`（位于本地区）；
    ///  · **用户口径 A18**：**没有合法宿主时留在场上（无宿主状态）**——触发取目标无候选 → 不入堆叠，本卡保持未结附。
    /// </summary>
    public class P3AttachSelfTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 121 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 3,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
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

        // ==================== ① 结构 ====================

        public void TestAttachSelf_JC121结构()
        {
            var tmpl = Load("JC121_kongmingyushou");
            var e = FindEffect(tmpl, "JC121_C_EnterAttachToLocalAlly");
            Assert.IsNotNull(e, "JC121 应有『进场触发：结附于本地区本方角色』效果");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "时机＝进场触发");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.IsTrue(e.Target.Restrictions.Contains(TargetRestriction.Friendly), "限本方");
            Assert.IsTrue(e.Target.Restrictions.Contains(TargetRestriction.LocalZone), "限本地区");
            Assert.AreEqual(ActionVerb.Attach, e.Actions[0].Verb, "动作＝结附");
            Assert.AreEqual("目标", e.Actions[0].Args[0], "宿主＝目标");
        }

        // ==================== ② 端到端：进场 → 结附到本地区本方角色 ====================

        public void TestAttachSelf_进场结附到本地区本方角色()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            string area = game.AreaManager.GetOrderedAreaUids()[0];

            var host = OnField(game, Probe("TS_as_host"), 1, area);      // 本地区本方角色
            var enemy = OnField(game, Probe("TS_as_enemy"), 2, area);     // 敌方角色（不应被选）

            var guard = game.CardManager.CreateCardInstance(Load("JC121_kongmingyushou"), 1);
            game.ZoneManager.MoveToArea(guard, area, 1);                  // 进场触发

            // 触发入堆叠（取目标＝合法候选的第一个）
            if (!game.StackManager.IsEmpty)
                for (int i = 0; i < 4 && !game.StackManager.IsEmpty; i++)
                    game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.AttachedZone, guard.CurrentZone, "空鸣御守已进入附属区（结附完成）");
            Assert.AreEqual(host, guard.AttachedTo, "结附到的宿主＝本地区本方角色");
            Assert.IsTrue(host.AttachedCards != null && host.AttachedCards.Contains(guard), "宿主收录该附属");
            Assert.IsFalse(ReferenceEquals(enemy, guard.AttachedTo), "不会结附到敌方角色");
        }

        // ==================== ③ 本地区没有合法宿主 → 留在场上（无宿主） ====================

        public void TestAttachSelf_无合法宿主则留在场上()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            string area = game.AreaManager.GetOrderedAreaUids()[0];

            OnField(game, Probe("TS_as_enemy2"), 2, area);   // 只有敌方角色（不合法）

            var guard = game.CardManager.CreateCardInstance(Load("JC121_kongmingyushou"), 1);
            game.ZoneManager.MoveToArea(guard, area, 1);

            for (int i = 0; i < 4 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.AreaZone, guard.CurrentZone, "无合法宿主 → 留在场上（未结附）");
            Assert.IsNull(guard.AttachedTo, "没有宿主");
        }

        // ==================== ③b 「不能移动」（前缀；A18/A19） ====================

        public void TestAttachSelf_未结附时不能移动()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var guard = game.CardManager.CreateCardInstance(Load("JC121_kongmingyushou"), 1);
            game.ZoneManager.MoveToArea(guard, areas[0], 1);   // 进场（未结附，位于争夺区）

            Assert.IsTrue(EffectEngine.Instance.HasPrintedStaticKind(guard, StaticKind.CannotMoveWhileUnattached),
                "模板上印刷了『不能移动』静止规则（前缀印刷规则，按模板判定）");

            game.CardManager.MoveCardToArea(guard, areas[1]);   // 机动/效果移动的唯一收口
            Assert.AreEqual(areas[0], guard.ZoneDetailId, "未结附于角色 → 移动被拒绝（仍在原地区）");
        }

        public void TestAttachSelf_结附于角色后不再受不能移动限制()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var host = OnField(game, Probe("TS_as_host4"), 1, areas[0]);
            var guard = game.CardManager.CreateCardInstance(Load("JC121_kongmingyushou"), 1);
            game.ZoneManager.MoveToArea(guard, areas[0], 1);

            var e = FindEffect(guard.PlayerTemplate, "JC121_C_EnterAttachToLocalAlly");
            EffectEngine.Instance.ResolveEffect(e, guard, host, areas[0]);   // 结附
            Assert.AreEqual(host, guard.AttachedTo, "前置：已结附于角色");

            // 结附于角色后：门禁的第二条件（未结附）不再成立 → 「不能移动」不再拦截
            // （实战中本卡是随宿主一起移动；此处直接调移动收口，用来证明门禁确实只在"未结附"时生效）
            game.CardManager.MoveCardToArea(guard, areas[1]);
            Assert.AreEqual(areas[1], guard.ZoneDetailId, "结附于角色后不再受『不能移动』限制（移动照常生效）");
        }

        // ==================== ④ Attach 执行器：结附后随宿主移动 ====================

        public void TestAttachSelf_结附后随宿主移动()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var host = OnField(game, Probe("TS_as_host3"), 1, areas[0]);
            var guard = game.CardManager.CreateCardInstance(Load("JC121_kongmingyushou"), 1);
            game.ZoneManager.MoveToArea(guard, areas[0], 1);

            // 直接调用执行器（不经触发）：宿主换区 → 附属的 ZoneDetailId 跟随宿主所在地区
            var e = FindEffect(guard.PlayerTemplate, "JC121_C_EnterAttachToLocalAlly");
            EffectEngine.Instance.ResolveEffect(e, guard, host, areas[0]);
            Assert.AreEqual(CardZone.AttachedZone, guard.CurrentZone, "已结附");

            game.ZoneManager.MoveToArea(host, areas[1], 1);
            Assert.AreEqual(areas[1], guard.ZoneDetailId, "随宿主移动到新地区（ZoneDetailId 跟随）");
        }
    }
}
