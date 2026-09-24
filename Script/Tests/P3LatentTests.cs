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
    /// 【P3·新触发时机「被潜伏/翻面时」（`OnLatent`）+ 群体伤害 → JC054 蒙古死亡蠕虫①】
    /// 卡文：潜伏强制触发：蒙古死亡蠕虫对所有位于本地区的其他角色造成1点伤害。
    ///      结束步骤强制触发：潜伏。（将该角色翻面为暗藏者）
    ///
    /// 交付：
    ///  · 新时机 `GameEvent.OnLatent` → `EffectTiming.OnLatent`：卡**翻面为暗藏者**
    ///    （`CardInstance.SetCovert(true)` → `CardChangeKind.CovertChanged`）时由装配层广播；
    ///    反方向（暗藏→正面）仍走既有 `UncoverTrigger`，两者互不重叠；
    ///    语义＝**自身**时机（只有被潜伏的那张卡自己的 `OnLatent` 效果被收集）。
    ///  · 群体伤害：`MassMode.AllInArea` + `DealDamage{1}` → 对本地区其他角色逐个结算
    ///    （`MassTargetPool` 自动排除来源自身与濒死卡）；无印刷防御的卡不受伤害（引擎既有语义）。
    /// </summary>
    public class P3LatentTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
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

        private static PlayerCardData Probe(string uid, int defense = 2)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = defense,
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

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        private static StackEntry FindEntryPrefix(GameInitializer game, string prefix)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.EffectDataEffect?.EffectId != null && e.EffectDataEffect.EffectId.StartsWith(prefix)) return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestLatent_JC054结构()
        {
            var tmpl = Load("JC054_menggusiwangruchong");
            Assert.IsNotNull(tmpl, "JC054 模板应可加载");

            var e = FindEffect(tmpl, "JC054_1_DamageAllOthersInAreaOnLatent");
            Assert.IsNotNull(e, "JC054 应有『潜伏强制触发：对本地区其他角色各 1 伤害』");
            Assert.AreEqual(EffectTiming.OnLatent, e.Timing, "时机＝被潜伏/翻面时（新时机）");
            Assert.IsTrue(e.Forced, "『强制触发』→ Forced=true");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "集合成员＝角色");
            Assert.AreEqual(MassMode.AllInArea, e.Target.MassMode, "群体：地区内全部符合条件者");
            Assert.IsFalse(e.Target.NeedsSelection, "群体动作不需要选单个目标");
            Assert.AreEqual(ActionVerb.DealDamage, e.Actions[0].Verb, "动作＝造成伤害");
            Assert.AreEqual("1", e.Actions[0].Args[0], "1 点伤害");
        }

        // ==================== ② 事件：只为"翻为暗藏者"广播（现身不广播） ====================

        public void TestLatent_翻为暗藏者才广播()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            // 用带 `OnLatent` 效果的卡（JC054）间接断言事件被广播
            var worm = game.CardManager.CreateCardInstance(Load("JC054_menggusiwangruchong"), 1);
            game.ZoneManager.MoveToArea(worm, area, 1);
            ResolveStackOnce(game);                        // 清掉进场触发

            // ① 翻为暗藏者 → OnLatent 广播 → 触发入堆叠
            worm.SetCovert(true);
            Assert.IsFalse(game.StackManager.IsEmpty, "翻为暗藏者 → OnLatent 触发入堆叠");
            ResolveStackOnce(game);

            // ② 现身（暗藏→正面）→ 不应再广播 OnLatent（该方向走 UncoverTrigger）
            game.CardManager.RevealCard(worm);
            ResolveStackOnce(game);
            Assert.IsNull(FindEntryPrefix(game, "JC054_1"), "现身方向不再触发 OnLatent（只有『被潜伏』才触发）");
        }

        // ==================== ③ 群体伤害：本地区其他角色各 1 点（排除自身与别地区） ====================

        public void TestLatent_群体伤害只打本地区其他角色()
        {
            var game = NewGame();
            var areas = Areas(game);
            string area = areas[0];

            var worm = game.CardManager.CreateCardInstance(Load("JC054_menggusiwangruchong"), 1);
            game.ZoneManager.MoveToArea(worm, area, 1);
            var ally = game.CardManager.CreateCardInstance(Probe("TS_lt_ally"), 1);
            game.ZoneManager.MoveToArea(ally, area, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_lt_foe"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);
            var far = game.CardManager.CreateCardInstance(Probe("TS_lt_far"), 2);
            game.ZoneManager.MoveToArea(far, areas[1], 2);
            ResolveStackOnce(game);

            Assert.AreEqual(0, worm.Damage, "前置：自身无伤害");

            // 潜伏自身 → 触发群体伤害
            worm.SetCovert(true);
            Assert.IsNotNull(FindEntryPrefix(game, "JC054_1"), "被潜伏 → 群体伤害触发已入堆叠");
            ResolveStackOnce(game);

            Assert.AreEqual(1, ally.Damage, "本地区**其他**角色（本方）受伤 1");
            Assert.AreEqual(1, foe.Damage, "本地区**其他**角色（敌方）受伤 1");
            Assert.AreEqual(0, far.Damage, "**别地区**角色不受影响");
            Assert.AreEqual(0, worm.Damage, "**自身**不受影响（『其他』）");
            Assert.IsTrue(worm.IsCovert, "潜伏本身已生效");
        }

        // ==================== ④ 链式：结束步骤潜伏 → 潜伏触发（JC054 的两条效果天然成链） ====================

        public void TestLatent_结束步骤潜伏触发伤害链()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var worm = game.CardManager.CreateCardInstance(Load("JC054_menggusiwangruchong"), 1);
            game.ZoneManager.MoveToArea(worm, area, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_lt_foe2"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);
            ResolveStackOnce(game);

            // 直接结算"结束步骤潜伏"那条效果（模拟结束步骤触发）
            var endStep = FindEffect(worm.PlayerTemplate, "JC054_2_EndStepLatent");
            EffectEngine.Instance.ResolveEffect(endStep, worm, null, area);
            Assert.IsTrue(worm.IsCovert, "结束步骤 → 已潜伏");

            ResolveStackOnce(game);   // OnLatent 触发 → 群体伤害
            Assert.AreEqual(1, foe.Damage, "潜伏引发的 1 点伤害已落到本地区其他角色");
        }

        // ==================== ⑤ 无印刷防御的卡不受伤害（引擎既有语义） ====================

        public void TestLatent_无防御角色不受伤害()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var worm = game.CardManager.CreateCardInstance(Load("JC054_menggusiwangruchong"), 1);
            game.ZoneManager.MoveToArea(worm, area, 1);
            var noDef = game.CardManager.CreateCardInstance(Probe("TS_lt_nodef", -1), 2);
            game.ZoneManager.MoveToArea(noDef, area, 2);
            ResolveStackOnce(game);

            worm.SetCovert(true);
            ResolveStackOnce(game);

            Assert.AreEqual(0, noDef.Damage, "无印刷防御的卡不受伤害（引擎既有语义）");
        }
    }
}
