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
    /// 【P3·K25】「道途」唯一性（JC034 心兽之道「你只能操控至多一个道途」）。
    /// **用户 2026-09 口径**：与【领袖】**同一套处理**——场上存在多个道途时**由该玩家选择一个牺牲**。
    ///  · 规则声明：`StaticKind.UniquePathAtMostOne`（只印在 JC034 上；未印该规则的「道途」卡互不限制）；
    ///  · 消费点：`PathRule.EnforceOnEntry`（挂 `ZoneManager.OnPostMove`，覆盖结附秘社的 `AttachedZone` 落点）；
    ///  · 超限 → **立刻**由该控制者选一个牺牲：**不入堆叠、不可被响应**；牺牲＝死亡（广播死亡触发）；
    ///  · 决策三条路径：单机 `IGameUIHooks.PromptPickCard`（不可取消）／联机 `Kind.SelectTarget`／
    ///    无 UI 确定性兜底（优先牺牲最新进场者）。
    /// </summary>
    public class P3PathUniquenessTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 340 };
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

        private static PlayerCardData Probe(string uid, string subCategory = null,
            CardCategory category = CardCategory.Attachment)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 1,
            };
            if (subCategory != null) tmpl.SubCategories.Add(subCategory);
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

        /// <summary>把一张"道途"卡结附到本方秘社上（与 `EffectEngine.AttachSearchedCard` 的秘社落点同款）。</summary>
        private static CardInstance AttachToCouncil(GameInitializer game, PlayerCardData tmpl, int playerId)
        {
            var council = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
            Assert.IsTrue(council != null && council.Count > 0, "前置：该玩家有秘社实例");
            var host = council[0];
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            card.IsAttached = true;
            card.AttachedTo = host;
            host.AttachedCards.Add(card);
            game.ZoneManager.MoveToAttachedZone(card, "", playerId);
            return card;
        }

        private static List<CardInstance> Paths(GameInitializer game, int playerId)
            => PathRule.PathsOnField(game.CardManager, playerId);

        /// <summary>选卡窗口桩：按给定 UID 回包（构造时传入）。</summary>
        private sealed class PickStub : Script.Interfaces.IGameUIHooks
        {
            public string PickUid;
            public int Calls;
            public int LastCandidateCount;
            public bool LastAllowCancel = true;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
            {
                Calls++;
                LastCandidateCount = candidates?.Count ?? 0;
                LastAllowCancel = allowCancel;
                CardInstance picked = null;
                if (!string.IsNullOrEmpty(PickUid) && candidates != null)
                    foreach (var c in candidates) if (c.UID == PickUid) picked = c;
                picked ??= (candidates != null && candidates.Count > 0 ? candidates[0] : null);
                onPicked?.Invoke(picked);
            }
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(0);
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);
        }

        // ==================== ① 结构：JC034 声明了"至多一个道途"静止标记 ====================

        public void TestPath_JC034结构()
        {
            var tmpl = Load("JC034_xinshouzhidao");
            Assert.IsNotNull(tmpl, "JC034 模板应可加载");
            var e = FindEffect(tmpl, "JC034_A_UniquePathAtMostOne");
            Assert.IsNotNull(e, "JC034 应有『你只能操控至多一个道途』效果");
            Assert.AreEqual(EffectTiming.Static, e.Timing, "以静止规则表达");
            Assert.IsNotNull(e.StaticEffects, "有静止效果声明");
            Assert.AreEqual(StaticKind.UniquePathAtMostOne, e.StaticEffects[0].Kind, "静止种类＝道途唯一性");
            Assert.IsTrue(tmpl.SubCategories.Contains("道途"), "本卡自身是『道途』子类别");
        }

        // ==================== ② 两张道途 → 立刻牺牲掉多余的那张（无 UI 确定性兜底） ====================

        public void TestPath_两张道途_无UI兜底牺牲最新进场()
        {
            var game = NewGame();
            EffectEngine.Instance.UIHooks = null;   // 无 UI → 确定性兜底
            var first = AttachToCouncil(game, Load("JC034_xinshouzhidao"), 1);   // 先：印了规则的 JC034
            Assert.AreEqual(1, Paths(game, 1).Count, "前置：场上 1 张道途");
            Assert.IsTrue(game.CardManager.GetInstance(first.UID) != null, "前置：先进场者仍在场");

            var second = AttachToCouncil(game, Probe("TS_path_second", "道途"), 1);   // 再：另一张道途
            Assert.AreEqual(1, Paths(game, 1).Count, "超限 → 立刻牺牲到只剩 1 张");
            Assert.AreEqual(CardZone.Graveyard, second.CurrentZone, "兜底优先牺牲『最新进场』的那张");
            Assert.AreEqual(CardZone.AttachedZone, first.CurrentZone, "先进场的那张留在场上");
        }

        // ==================== ③ 单机有 UI：按玩家选择牺牲 ====================

        public void TestPath_单机由玩家选择牺牲哪张()
        {
            var game = NewGame();
            var first = AttachToCouncil(game, Load("JC034_xinshouzhidao"), 1);
            var stub = new PickStub { PickUid = first.UID };   // 玩家选择牺牲"先进场"的那张
            EffectEngine.Instance.UIHooks = stub;

            var second = AttachToCouncil(game, Probe("TS_path_second2", "道途"), 1);

            Assert.AreEqual(1, stub.Calls, "询问过一次『选择哪一个牺牲』");
            Assert.AreEqual(2, stub.LastCandidateCount, "候选＝两张道途");
            Assert.IsFalse(stub.LastAllowCancel, "规则性牺牲不可取消");
            Assert.AreEqual(CardZone.Graveyard, first.CurrentZone, "玩家所选的那张被牺牲");
            Assert.AreEqual(CardZone.AttachedZone, second.CurrentZone, "另一张留在场上");
            Assert.AreEqual(1, Paths(game, 1).Count, "收敛到 1 张");
        }

        // ==================== ④ 场上没有任何"印了该规则"的卡 → 不施加限制 ====================

        public void TestPath_场上无卡声明规则时两张道途互不限制()
        {
            var game = NewGame();
            EffectEngine.Instance.UIHooks = null;
            var a = AttachToCouncil(game, Probe("TS_path_a", "道途"), 1);
            var b = AttachToCouncil(game, Probe("TS_path_b", "道途"), 1);

            Assert.AreEqual(2, Paths(game, 1).Count, "两张道途都留在场上（场上无卡声明该规则）");
            Assert.AreEqual(CardZone.AttachedZone, a.CurrentZone, "a 未被牺牲");
            Assert.AreEqual(CardZone.AttachedZone, b.CurrentZone, "b 未被牺牲");
        }

        // ==================== ⑤ 不同玩家各 1 张 → 互不影响（"同一玩家"口径） ====================

        public void TestPath_不同玩家各一张互不影响()
        {
            var game = NewGame();
            EffectEngine.Instance.UIHooks = null;
            AttachToCouncil(game, Load("JC034_xinshouzhidao"), 1);
            var enemy = AttachToCouncil(game, Probe("TS_path_enemy", "道途"), 2);

            Assert.AreEqual(1, Paths(game, 1).Count, "P1 仍有 1 张");
            Assert.AreEqual(1, Paths(game, 2).Count, "P2 仍有 1 张");
            Assert.AreEqual(CardZone.AttachedZone, enemy.CurrentZone, "对手的道途不受 P1 规则影响");
        }
    }
}
