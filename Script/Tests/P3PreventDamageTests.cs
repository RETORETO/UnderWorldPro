using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K3 第一批：防止伤害 → JC078 光能盾】
    /// 卡文：快速行动：本回合中，为目标角色防止接下来将受到的所有伤害。
    ///
    /// 交付：`ActionVerb.PreventDamage`（原先只是枚举占位/TODO）首次落地——
    ///  · **伤害护盾**：`EffectEngine.RegisterDamageShield(card)` → `TimedMod{Kind="DamageShield"}`，期限＝本回合；
    ///  · **消费点**：`CardManager.DealDamageToInstance` 的 `DamagePreventer` 钩子（伤害**唯一收口**，
    ///    装配层把本包"灵体"防止与护盾**并联**）→ 被防止＝不写伤害 → 不判死 → **不触发死亡**；
    ///  · **幂等**：同卡同回合只登记一条（卡文是"所有伤害"而非"接下来 X 点"，无层数/点数语义）；
    ///  · **到期**：回合结束由 `ExpireTurnEndEffects` 出栈失效（护盾即登记项本身）。
    /// 口径：对象＝`目标`（缺省）/`自身`；对象＝目标而**当前无目标 → 跳过**（与 `Inspect` 同款，不回退成来源卡）。
    /// </summary>
    public class P3PreventDamageTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 191 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
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

        /// <summary>角色探针（有防御力，可受伤/可死）。</summary>
        private static PlayerCardData CharacterProbe(string uid, int defense = 3)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "角色_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = defense,
            };
            tmpl.SubCategories.Add("人类");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance PlaceCharacter(GameInitializer game, string area, string uid, int defense = 3)
        {
            var card = game.CardManager.CreateCardInstance(CharacterProbe(uid, defense), 1);
            game.ZoneManager.MoveToArea(card, area, 1);
            return card;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private sealed class AbandonTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onDone?.Invoke();

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        // ==================== ① 结构 ====================

        public void TestPreventDamage_JC078结构()
        {
            var tmpl = Load("JC078_guangnengdun");
            Assert.IsNotNull(tmpl, "JC078 模板应可加载");
            var e = FindEffect(tmpl, "JC078_A_PreventAllDamage");
            Assert.IsNotNull(e, "JC078 应有『防止所有伤害』效果");
            Assert.AreEqual(EffectTiming.OnSorceryCast, e.Timing, "事务卡打出即结算");
            Assert.AreEqual(PlayTiming.Response, e.PlayTiming, "快速行动＝可响应打出");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色（不分敌我）");
            Assert.IsTrue(e.Target.NeedsSelection, "需要选取目标");
            Assert.AreEqual(1, e.Actions.Count, "一条动作");
            Assert.AreEqual(ActionVerb.PreventDamage, e.Actions[0].Verb, "动词＝PreventDamage（本轮首次落地）");
        }

        // ==================== ② 护盾登记（只护住目标卡，不波及他人） ====================

        public void TestPreventDamage_护盾只作用于目标卡()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var warded = PlaceCharacter(game, area, "TS_pd_a");
            var other = PlaceCharacter(game, area, "TS_pd_b");

            EffectEngine.Instance.RegisterDamageShield(warded);

            Assert.IsTrue(EffectEngine.Instance.IsDamagePrevented(warded), "目标卡处于护盾下");
            Assert.IsFalse(EffectEngine.Instance.IsDamagePrevented(other), "同地区其它角色不受护盾影响");
        }

        // ==================== ③ 伤害真的被防止（不写伤害、不判死） ====================

        public void TestPreventDamage_伤害被防止且不判死()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var warded = PlaceCharacter(game, area, "TS_pd_c", defense: 1);

            EffectEngine.Instance.RegisterDamageShield(warded);
            game.CardManager.DealDamageToInstance(warded, 5);   // 足以致死的一击

            Assert.AreEqual(0, warded.Damage, "被防止 → 伤害一点都没写进去（不落地的伤害不产生死亡）");
            Assert.IsFalse(warded.IsDead(), "被防止 → 不判死（1 防御受 5 点是致死的，若防止失效必死）");
        }

        // ==================== ④ 对照：没有护盾时伤害正常落地 ====================

        public void TestPreventDamage_对照无护盾时正常受伤()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var plain = PlaceCharacter(game, area, "TS_pd_d", defense: 5);

            game.CardManager.DealDamageToInstance(plain, 2);

            Assert.AreEqual(2, plain.Damage, "无护盾 → 伤害正常落地");
            Assert.IsFalse(plain.IsDead(), "5 防御受 2 伤不死");
        }

        // ==================== ⑤ 期限＝本回合：回合结束护盾失效 ====================

        public void TestPreventDamage_回合结束失效()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var warded = PlaceCharacter(game, area, "TS_pd_e", defense: 5);

            EffectEngine.Instance.RegisterDamageShield(warded);
            game.CardManager.DealDamageToInstance(warded, 1);
            Assert.AreEqual(0, warded.Damage, "本回合内：第 1 次伤害被防止");

            // 回合结束（TurnManager.OnTurnEnded → ExpireTurnEndEffects(TurnCount)）
            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);

            Assert.IsFalse(EffectEngine.Instance.IsDamagePrevented(warded), "回合结束 → 护盾失效");
            game.CardManager.DealDamageToInstance(warded, 1);
            Assert.AreEqual(1, warded.Damage, "护盾失效后 → 伤害正常落地");
        }

        // ==================== ⑥ 幂等：重复结算不叠加（也无从叠加） ====================

        public void TestPreventDamage_重复登记幂等()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var warded = PlaceCharacter(game, area, "TS_pd_f", defense: 5);

            EffectEngine.Instance.RegisterDamageShield(warded);
            EffectEngine.Instance.RegisterDamageShield(warded);   // 第二次（如双份光能盾）不叠加

            game.CardManager.DealDamageToInstance(warded, 2);
            Assert.AreEqual(0, warded.Damage, "护盾仍只『全部防止』，重复登记不改变结果");

            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);
            Assert.IsFalse(EffectEngine.Instance.IsDamagePrevented(warded), "一次到期即完全失效（没有残留第二层）");
            game.CardManager.DealDamageToInstance(warded, 2);
            Assert.AreEqual(2, warded.Damage, "到期后伤害恢复落地（证明确实只剩 0 层护盾）");
        }

        // ==================== ⑦ 端到端：走引擎结算挂上护盾 ====================

        public void TestPreventDamage_端到端结算()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var tmpl = Load("JC078_guangnengdun");
            var spell = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToHand(spell, 1);
            var target = PlaceCharacter(game, area, "TS_pd_g", defense: 2);
            var effect = FindEffect(tmpl, "JC078_A_PreventAllDamage");

            EffectEngine.Instance.ResolveEffect(effect, spell, target);

            Assert.IsTrue(EffectEngine.Instance.IsDamagePrevented(target), "结算后目标获得伤害护盾");
            game.CardManager.DealDamageToInstance(target, 4);
            Assert.AreEqual(0, target.Damage, "护盾生效 → 致死伤害一点都没落地");
            Assert.IsFalse(target.IsDead(), "护盾生效 → 2 防御受 4 点也不死");
        }

        // ==================== ⑧ 对象＝目标但无目标 → 跳过（不回退成来源卡） ====================

        public void TestPreventDamage_无目标时跳过不护来源卡()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var tmpl = Load("JC078_guangnengdun");
            var spell = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToHand(spell, 1);
            var effect = FindEffect(tmpl, "JC078_A_PreventAllDamage");

            EffectEngine.Instance.ResolveEffect(effect, spell, null);   // 无目标（目标已丢失）

            Assert.IsFalse(EffectEngine.Instance.IsDamagePrevented(spell),
                "对象＝目标且当前无目标 → 跳过，绝不回退成『护住来源卡』");
        }
    }
}
