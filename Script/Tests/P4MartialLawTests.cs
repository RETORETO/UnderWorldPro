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
    /// 【P3·K21·JC064 戒严】（用户口径 **A19**）：
    ///  · 持续：**非【国家机构】的角色牌不可正面打出在本地区，也不能移动进入本地区**——
    ///    `StaticKind.BlockNonOrgEntryInArea`（`Param="国家机构"`）；
    ///    "移动"**涵盖机动 / 效果移动 / 置换**（收口 `CardManager.MoveCardToArea`）；
    ///    FAQ 放行两条：**秘密派遣** 与 **其他方式放置进场**（如操控死尸）。
    ///  · 准备步骤**强制**触发：`PayOrSacrificeSelf{1}` —— **由玩家选择**支付或牺牲；
    ///    **费用不足 → 直接牺牲**（卡文"否则"）。
    /// </summary>
    public class P4MartialLawTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 640 };
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

        private static PlayerCardData Probe(string uid, string organization, CardCategory category = CardCategory.Character,
            int cost = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 5 : -1,
                Organization = organization,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveMana(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var asset = game.CardManager.CreateCardInstance(
                    Probe($"TS_jy_asset_{playerId}_{i}", "", CardCategory.Attachment), playerId);
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

        /// <summary>把戒严结附到某地区（生产路径：结附 + 持续效果登记）。</summary>
        private static CardInstance AttachMartialLaw(GameInitializer game, int playerId, string area)
        {
            var law = game.CardManager.CreateCardInstance(Load("JC064_jieyan"), playerId);
            law.IsAttached = true;
            game.ZoneManager.MoveToAttachedZone(law, area, playerId);
            game.EffectEngine.RegisterPersistent(law);
            return law;
        }

        private static void ReadyToPlay(GameInitializer game, int playerId)
        {
            game.TurnManager.PriorityPlayerId = playerId;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
        }

        private sealed class Stub : Script.Interfaces.IGameUIHooks
        {
            public int Answer = 1;      // 1＝支付
            public int Calls;               // 数值输入框调用次数（JC064 应为 **0**）
            public int PayOrSacrificeCalls; // "支付 / 牺牲"二选一窗调用次数

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked) { }
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen) { }
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen) { }

            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
            {
                Calls++;
                onChosen?.Invoke(Answer);
            }

            /// <summary>【2026-09 用户实测·JC064】"支付 N，否则牺牲本卡"＝**二选一窗**（不是数值输入框）。</summary>
            public void PromptPayOrSacrifice(int chooserPlayerId, string sourceName, int amount,
                System.Action<bool> onChosen)
            {
                PayOrSacrificeCalls++;
                onChosen?.Invoke(Answer > 0);
            }
        }

        // ==================== ① 结构 ====================

        public void TestMartialLaw_结构()
        {
            var tmpl = Load("JC064_jieyan");
            Assert.AreEqual(1, tmpl.TargetRules.Count, "打出目标＝一条规则");
            Assert.AreEqual(TargetKind.Area, tmpl.TargetRules[0].Kind, "结附于目标地区");

            var e1 = FindEffect(tmpl, "JC064_A_BlockNonOrgEntry");
            Assert.IsNotNull(e1, "应有进入限制效果");
            Assert.AreEqual(EffectTiming.Continuous, e1.Timing, "持续效果");
            Assert.IsNotNull(e1.StaticEffects, "声明了静止规则");
            Assert.AreEqual(StaticKind.BlockNonOrgEntryInArea, e1.StaticEffects[0].Kind, "规则＝非指定组织不可进入本地区");
            Assert.AreEqual("国家机构", e1.StaticEffects[0].Param, "允许的组织＝国家机构");

            var e2 = FindEffect(tmpl, "JC064_B_UpkeepPayOrSacrifice");
            Assert.IsNotNull(e2, "应有准备步骤生效效果");
            Assert.AreEqual(EffectTiming.OnTurnStarted, e2.Timing, "准备步骤");
            Assert.IsTrue(e2.Forced, "卡文『强制触发』");
            Assert.AreEqual(ActionVerb.PayOrSacrificeSelf, e2.Actions[0].Verb, "动作＝支付否则牺牲");
            Assert.AreEqual("1", e2.Actions[0].Args[0], "金额＝1");
        }

        // ==================== ② 正面打出：非国家机构角色被拒，国家机构放行 ====================

        public void TestMartialLaw_正面打出被拒()
        {
            var game = NewGame();
            string area = FirstArea(game);
            AttachMartialLaw(game, 2, area);                 // 对手（P2）结附戒严
            GiveMana(game, 1, 4);

            var outsider = game.CardManager.CreateCardInstance(Probe("TS_jy_civ", "平民"), 1);
            game.ZoneManager.MoveToHand(outsider, 1);
            ReadyToPlay(game, 1);
            Assert.IsFalse(game.TurnManager.PlayCard(1, outsider.UID, area), "**非【国家机构】角色不能正面打出在该地区**");
            Assert.AreEqual(CardZone.Hand, outsider.CurrentZone, "卡仍在手牌");

            var insider = game.CardManager.CreateCardInstance(Probe("TS_jy_gov", "国家机构"), 1);
            game.ZoneManager.MoveToHand(insider, 1);
            Assert.IsTrue(game.TurnManager.PlayCard(1, insider.UID, area), "【国家机构】角色照常可以正面打出");
            Assert.IsFalse(game.StackManager.IsEmpty, "已入堆叠");
        }

        // ==================== ③ 移动进入：机动/效果移动被拒（含"置换"同收口） ====================

        public void TestMartialLaw_移动进入被拒()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            string locked = areas[0], other = areas[1];
            AttachMartialLaw(game, 2, locked);

            var civ = game.CardManager.CreateCardInstance(Probe("TS_jy_mover", "平民"), 1);
            game.ZoneManager.MoveToArea(civ, other, 1);
            Assert.AreEqual(other, civ.ZoneDetailId, "前置：先位于另一个地区");

            // 移动唯一收口（机动 / 效果移动 / 置换都经此）
            game.CardManager.MoveCardToArea(civ, locked);
            Assert.AreEqual(other, civ.ZoneDetailId, "**非国家机构角色不能移动进入受戒严结附的地区**");

            // 国家机构角色可正常进入（对照）
            var gov = game.CardManager.CreateCardInstance(Probe("TS_jy_govmover", "国家机构"), 1);
            game.ZoneManager.MoveToArea(gov, other, 1);
            game.CardManager.MoveCardToArea(gov, locked);
            Assert.AreEqual(locked, gov.ZoneDetailId, "【国家机构】角色可以移动进入");
        }

        // ==================== ④ FAQ：秘密派遣 / 从墓地"放置进场"不受限 ====================

        public void TestMartialLaw_秘密派遣与放置进场放行()
        {
            var game = NewGame();
            string area = FirstArea(game);
            AttachMartialLaw(game, 2, area);

            // ① 从墓地放进场（"操控死尸"类路径：不经 MoveCardToArea 的"移动"分支）
            var dead = game.CardManager.CreateCardInstance(Probe("TS_jy_dead", "平民"), 1);
            game.ZoneManager.MoveToGraveyard(dead, 1);
            game.ZoneManager.MoveToArea(dead, area, 1);      // 放置进场（非"移动进入"）
            Assert.AreEqual(area, dead.ZoneDetailId, "FAQ：其他方式放置进场（如操控死尸）不受戒严限制");
        }

        // ==================== ⑤ 准备步骤：支付 / 牺牲 两种选择 ====================

        public void TestMartialLaw_支付或牺牲()
        {
            var game = NewGame();
            string area = FirstArea(game);
            GiveMana(game, 1, 3);
            var law = AttachMartialLaw(game, 1, area);
            var upkeep = FindEffect(law.PlayerTemplate, "JC064_B_UpkeepPayOrSacrifice");

            // 选择支付：扣 1 费、卡留在场
            var stub = new Stub { Answer = 1 };
            game.EffectEngine.UIHooks = stub;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            game.EffectEngine.ResolveEffect(upkeep, law, null, "", -1, -1, -1);
            Assert.AreEqual(1, stub.PayOrSacrificeCalls, "弹了一次『支付 / 牺牲』**二选一**窗");
            Assert.AreEqual(0, stub.Calls, "**不出现数值输入框**（用户实测 JC064 口径，参考 JC061）");
            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 费");
            Assert.AreEqual(CardZone.AttachedZone, law.CurrentZone, "戒严留在场上");

            // 选择牺牲
            var game2 = NewGame();
            string area2 = FirstArea(game2);
            GiveMana(game2, 1, 3);
            var law2 = AttachMartialLaw(game2, 1, area2);
            var upkeep2 = FindEffect(law2.PlayerTemplate, "JC064_B_UpkeepPayOrSacrifice");
            game2.EffectEngine.UIHooks = new Stub { Answer = 0 };   // 不支付
            game2.EffectEngine.ResolveEffect(upkeep2, law2, null, "", -1, -1, -1);
            Assert.AreEqual(CardZone.Graveyard, law2.CurrentZone, "选择不支付 → **牺牲戒严**");

            // 费用不足 → 不询问、直接牺牲（卡文"否则"）
            var game3 = NewGame();
            string area3 = FirstArea(game3);
            var law3 = AttachMartialLaw(game3, 1, area3);            // 无资产 → 可用费用 0
            var upkeep3 = FindEffect(law3.PlayerTemplate, "JC064_B_UpkeepPayOrSacrifice");
            var stub3 = new Stub { Answer = 1 };
            game3.EffectEngine.UIHooks = stub3;
            game3.EffectEngine.ResolveEffect(upkeep3, law3, null, "", -1, -1, -1);
            Assert.AreEqual(0, stub3.Calls, "费用不足 → **不询问**");
            Assert.AreEqual(CardZone.Graveyard, law3.CurrentZone, "直接牺牲戒严");
        }

        // ==================== ⑥ 来源离场 → 限制消失 ====================

        public void TestMartialLaw_来源离场后限制消失()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var law = AttachMartialLaw(game, 2, area);

            game.CardManager.DestroyInstance(law);   // 戒严离场
            Assert.AreEqual(CardZone.Graveyard, law.CurrentZone, "前置：已离场");

            var civ = game.CardManager.CreateCardInstance(Probe("TS_jy_after", "平民"), 1);
            game.ZoneManager.MoveToHand(civ, 1);
            GiveMana(game, 1, 4);
            ReadyToPlay(game, 1);
            Assert.IsTrue(game.TurnManager.PlayCard(1, civ.UID, area), "来源离场 → 不再限制进入");
        }
    }
}
