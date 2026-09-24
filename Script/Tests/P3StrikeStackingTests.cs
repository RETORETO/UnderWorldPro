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
    /// 【K13·袭击数值叠加】按用户 2026-09 口径实现并锁定：
    ///  · "袭击X"来源可以是**卡面自带**（`KeywordEffects.Strike(N)`）或**运行期授予**（如 JC038 光环授予 `Attribute:"袭击"`）；
    ///  · **多个袭击同时存在时数值叠加**（已有袭击1 被授予袭击1 → 变为袭击2），
    ///    且在收集阶段**合并为"一个"触发**（不是两个各 1 点的触发）；
    ///  · 授予只作用于符合条件的角色（JC038：本方 + 组织＝王座会，全场，排除自身）。
    /// 覆盖卡：**JC038 列恩·爱弗罗德**（持续：所有其他本方【王座会】角色获得袭击1）。
    /// </summary>
    public class P3StrikeStackingTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 131 };
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

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        /// <summary>探针角色：可选自带袭击N、可选组织。</summary>
        private static PlayerCardData Probe(string uid, string org = null, int strike = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            if (org != null) tmpl.Organization = org;
            if (strike > 0)
            {
                var s = KeywordEffects.Strike(strike);
                s.EffectId = uid + "_strike";
                tmpl.EffectDataList.Add(s);
            }
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

        // ==================== ① 结构 ====================

        public void TestK13_JC038结构()
        {
            var tmpl = Load("JC038_lieenaifuluode");
            Assert.IsNotNull(tmpl, "JC038 模板应可加载");
            var e = FindEffect(tmpl, "JC038_B_AuraStrike1ToCourt");
            Assert.IsNotNull(e, "JC038 应有『王座会角色获得袭击1』光环");
            Assert.IsTrue(e.IsAura, "是光环");
            Assert.AreEqual("王座会", e.AuraFilterOrganization, "组织筛选＝王座会");
            Assert.AreEqual(1, e.ContinuousGrants.Count, "授予一条");
            Assert.AreEqual("Attribute", e.ContinuousGrants[0].Kind, "以属性形式授予");
            Assert.AreEqual("袭击", e.ContinuousGrants[0].Value, "属性＝袭击");
            Assert.AreEqual(1, e.ContinuousGrants[0].Amount, "点数＝1");

            // 卡面自带的袭击效果带 IsStrike 标记（收集阶段据此合并）
            var s = KeywordEffects.Strike(3);
            Assert.IsTrue(s.IsStrike, "KeywordEffects.Strike 应标记 IsStrike");
            Assert.AreEqual(3, s.StrikeAmount, "StrikeAmount 记录点数");
        }

        // ==================== ② 叠加：自带袭击1 + 授予袭击1 → 合并为一个袭击2 ====================

        public void TestK13_袭击叠加合并为一个触发()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var leader = game.CardManager.CreateCardInstance(Load("JC038_lieenaifuluode"), 1);
            game.ZoneManager.MoveToArea(leader, area, 1);

            // 自带袭击1 的王座会角色（本方）：以**暗藏者身份进场**（生产路径），随后现身
            var mate = game.CardManager.CreateCardInstance(Probe("TS_k13_mate", "王座会", strike: 1), 1);
            mate.SetCovert(true);
            game.ZoneManager.MoveToArea(mate, area, 1);

            EffectEngine.Instance.RegisterPersistent(leader);   // 光环注册（暗藏者不受光环 → 此时未授予）
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            game.SetInputGateway(new EnterAllGateway());
            game.CardManager.RevealCard(mate);                  // 现身：此时才应获得光环授予
            Assert.AreEqual(1, mate.GetAttribute("袭击"), "现身（正面）后获得光环授予『袭击1』");

            // 收集阶段：只应有 **1 条**袭击触发
            var strikeEntries = new System.Collections.Generic.List<StackEntry>();
            foreach (var e in game.StackManager.Entries)
                if (e.EffectDataEffect?.IsStrike == true) strikeEntries.Add(e);
            Assert.AreEqual(1, strikeEntries.Count, "多个袭击来源应**合并为一个触发**");

            var dmgArg = strikeEntries[0].EffectDataEffect.Actions[0].Args[0];
            Assert.AreEqual("2", dmgArg, "伤害＝自带1 + 授予1 = **袭击2**");
        }

        /// <summary>只有授予（无自带袭击）→ 仍会有一个袭击1 的触发。</summary>
        public void TestK13_仅授予也产生袭击触发()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var leader = game.CardManager.CreateCardInstance(Load("JC038_lieenaifuluode"), 1);
            game.ZoneManager.MoveToArea(leader, area, 1);
            var mate = game.CardManager.CreateCardInstance(Probe("TS_k13_plain", "王座会"), 1);
            mate.SetCovert(true);
            game.ZoneManager.MoveToArea(mate, area, 1);

            EffectEngine.Instance.RegisterPersistent(leader);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            game.SetInputGateway(new EnterAllGateway());
            game.CardManager.RevealCard(mate);
            Assert.AreEqual(1, mate.GetAttribute("袭击"), "现身（正面）后获得光环授予");

            var strikeEntries = new System.Collections.Generic.List<StackEntry>();
            foreach (var e in game.StackManager.Entries)
                if (e.EffectDataEffect?.IsStrike == true) strikeEntries.Add(e);
            Assert.AreEqual(1, strikeEntries.Count, "授予的袭击也应参与触发收集（K13 缺口已补）");
            Assert.AreEqual("1", strikeEntries[0].EffectDataEffect.Actions[0].Args[0], "伤害＝授予的 1 点");
        }

        /// <summary>非王座会角色：不获得授予 → 不产生袭击触发。</summary>
        public void TestK13_非王座会不获得袭击()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var leader = game.CardManager.CreateCardInstance(Load("JC038_lieenaifuluode"), 1);
            game.ZoneManager.MoveToArea(leader, area, 1);
            var outsider = game.CardManager.CreateCardInstance(Probe("TS_k13_other", "鸣钟教派"), 1);
            outsider.SetCovert(true);
            game.ZoneManager.MoveToArea(outsider, area, 1);

            EffectEngine.Instance.RegisterPersistent(leader);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            game.SetInputGateway(new EnterAllGateway());
            game.CardManager.RevealCard(outsider);
            Assert.AreEqual(0, outsider.GetAttribute("袭击"), "非王座会角色不获得袭击");

            foreach (var e in game.StackManager.Entries)
                Assert.IsFalse(e.EffectDataEffect?.IsStrike == true, "非王座会角色不应有袭击触发");
        }

        /// <summary>对手的王座会角色不获得（"本方"限定）。</summary>
        public void TestK13_对手王座会不获得()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var leader = game.CardManager.CreateCardInstance(Load("JC038_lieenaifuluode"), 1);
            game.ZoneManager.MoveToArea(leader, area, 1);
            var enemyCourt = game.CardManager.CreateCardInstance(Probe("TS_k13_enemy", "王座会", strike: 0), 2);
            game.ZoneManager.MoveToArea(enemyCourt, area, 2);

            EffectEngine.Instance.RegisterPersistent(leader);
            Assert.AreEqual(0, enemyCourt.GetAttribute("袭击"), "对手角色不获得本方光环的袭击");
        }

        // ==================== ③ 光环消失（源卡离场）→ 授予撤销 ====================

        public void TestK13_光环离场则授予撤销()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var leader = game.CardManager.CreateCardInstance(Load("JC038_lieenaifuluode"), 1);
            game.ZoneManager.MoveToArea(leader, area, 1);
            var mate = game.CardManager.CreateCardInstance(Probe("TS_k13_leave", "王座会"), 1);
            game.ZoneManager.MoveToArea(mate, area, 1);

            EffectEngine.Instance.RegisterPersistent(leader);
            Assert.AreEqual(1, mate.GetAttribute("袭击"), "前置：已获得授予");

            EffectEngine.Instance.UnregisterPersistent(leader);
            Assert.AreEqual(0, mate.GetAttribute("袭击"), "光环注销 → 授予的袭击被撤销");
        }

        // ==================== 桩件 ====================

        private sealed class EnterAllGateway : Script.Net.IInputGateway
        {
            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                for (int i = 0; i < (req?.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                if (req?.OptionUids != null && req.OptionUids.Count > 0) r.SelectedUid = req.OptionUids[0];
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        private sealed class EnterAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone) { }
            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);
            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }
    }
}
