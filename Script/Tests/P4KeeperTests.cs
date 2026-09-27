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
    /// 【JC119 守秘人②】「死亡触发：**展示封印在守秘人上的所有牌**，你可以**打出其中一张**，视作它具有【快速】
    /// 且不需要支付其费用（但**仍需满足忠诚需求**）。将不打出的牌移回其拥有者手上。」
    /// 用户 2026-09 口径（本轮答复）：**走正常打出流程**（死亡触发在原子锁之后结算；此时封印牌已按规则回到手牌）
    /// → **直接从手牌展示那批牌后打出**；选择交互＝**复用既有选卡窗**，**允许取消＝不打**。
    ///
    /// 交付要点：
    ///  · `ZoneManager.DetachSealedCards` 给"因宿主离场而回手"的封印牌打上 `ReturnedFromSealHostUid` 标记
    ///    （卡离开手牌即清除）→ 触发结算时据此识别"刚刚由于封印效果回到手牌的牌"；
    ///  · 动词 `RevealSealedAndQueueFreePlay`：**先公开展示全部** → 弹选卡窗（**可取消**）→ 登记免费打出；
    ///  · **正常打出流程**：`TurnManager` 免费打出队列在**原子锁解除后**执行
    ///    `PlayCard(freePlay:true, asIfQuick:true)` —— **免费**（费用 0、跳过额外费用）＋**视作快速**（跳过阶段门禁）
    ///    ＋**忠诚照常校验**；不打出的牌留在手牌（"移回拥有者手上"天然成立）。
    /// </summary>
    public class P4KeeperTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 1190 };
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

        private static PlayerCardData Probe(string uid, CardCategory category, int cost = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 5 : -1,
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

        /// <summary>选卡窗 + 目标选择窗桩：分别按脚本返回某张候选（null＝取消）。记录各自收到的候选。</summary>
        private sealed class PickStub : Script.Interfaces.IGameUIHooks
        {
            public CardInstance Pick;          // 要选的牌（null＝取消）
            public int Calls;
            public int LastCandidateCount = -1;

            // 【JC119·目标选择窗】记录 PromptPlayTarget 的调用与候选（默认回调 null＝取消）
            public int TargetCalls;
            public bool LastIsArea;
            public string LastPrompt;
            public List<string> LastTargetCandidates;
            public string TargetPick;          // 要选的目标 UID（null＝取消）

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
            {
                Calls++;
                LastCandidateCount = candidates?.Count ?? -1;
                onPicked?.Invoke(Pick);
            }
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
                string confirmLabel, string cancelLabel, System.Action<int> onChosen) { }
            public void PromptPlayTarget(int chooserPlayerId, string prompt, bool isArea, List<string> candidateUids,
                System.Action<string> onPicked)
            {
                TargetCalls++;
                LastIsArea = isArea;
                LastPrompt = prompt;
                LastTargetCandidates = candidateUids == null ? null : new List<string>(candidateUids);
                onPicked?.Invoke(TargetPick);
            }
        }

        /// <summary>造一个"守秘人 + 其上一张背面朝上封印牌"，然后把守秘人消灭（触发死亡时机）。
        /// 返回 (守秘人, 被封印的牌)。</summary>
        private static (CardInstance keeper, CardInstance sealedCard) KeeperWithSealed(GameInitializer game, int playerId)
        {
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var keeper = game.CardManager.CreateCardInstance(Load("JC119_shoumiren"), playerId);
            game.ZoneManager.MoveToArea(keeper, area, playerId);

            var sealedCard = game.CardManager.CreateCardInstance(
                Probe("TS_kp_sealed", CardCategory.Sorcery, cost: 9), playerId);
            game.ZoneManager.MoveToHand(sealedCard, playerId);
            game.TurnManager.SealCard(sealedCard, keeper, faceDown: true);
            Assert.IsTrue(sealedCard.IsSeal, "前置：已封印在守秘人上");
            return (keeper, sealedCard);
        }

        // ==================== ① 结构 ====================

        public void TestKeeper_结构()
        {
            var tmpl = Load("JC119_shoumiren");
            var e = FindEffect(tmpl, "JC119_2_RevealSealedAndFreePlayOne");
            Assert.IsNotNull(e, "JC119 应有『展示封印牌并免费打出一张』死亡触发");
            Assert.AreEqual(EffectTiming.OnCardDied, e.Timing, "时机＝死亡触发");
            Assert.IsFalse(e.Forced, "卡文『你可以』→ 可选");
            Assert.AreEqual(ActionVerb.RevealSealedAndQueueFreePlay, e.Actions[0].Verb, "动作＝展示 + 选一张 + 登记免费打出");
        }

        // ==================== ② 端到端：展示 → 选一张 → 免费打出（忠诚照常、视作快速） ====================

        public void TestKeeper_免费打出封印牌()
        {
            var game = NewGame();
            var (keeper, sealedCard) = KeeperWithSealed(game, 1);
            var effect = FindEffect(keeper.PlayerTemplate, "JC119_2_RevealSealedAndFreePlayOne");

            var stub = new PickStub { Pick = sealedCard };
            game.EffectEngine.UIHooks = stub;

            game.CardManager.DestroyInstance(keeper);      // 守秘人被消灭 → 封印牌回手 + 死亡触发
            Assert.AreEqual(CardZone.Hand, sealedCard.CurrentZone, "前置：封印牌已按封印规则回到手牌");
            Assert.AreEqual(keeper.UID, sealedCard.ReturnedFromSealHostUid, "并带上『刚由该宿主封印回手』标记");

            // 结算死亡触发（触发经堆叠结算；期间只登记免费打出，原子锁解除后走正常打出流程）
            game.EffectEngine.ResolveEffect(effect, keeper, null, "", -1, -1, -1);
            Assert.AreEqual(1, stub.Calls, "弹了一次选卡窗");
            Assert.AreEqual(1, stub.LastCandidateCount, "候选＝那 1 张刚回手的封印牌");

            Assert.IsTrue(game.TurnManager.HasQueuedFreePlay(sealedCard.UID), "已登记免费打出（等待原子锁解除）");
        }

        // ==================== ③ 免费打出：不付费（费用 9 也免费）、入堆叠、离开手牌即清标记 ====================

        public void TestKeeper_免费打出不付费()
        {
            var game = NewGame();
            var (keeper, sealedCard) = KeeperWithSealed(game, 1);
            var effect = FindEffect(keeper.PlayerTemplate, "JC119_2_RevealSealedAndFreePlayOne");
            game.EffectEngine.UIHooks = new PickStub { Pick = sealedCard };
            game.CardManager.DestroyInstance(keeper);

            int manaBefore = game.PlayerManager.GetAvailableMana(1);   // 未给资产 → 0 费也付不起该 9 费牌
            game.EffectEngine.ResolveEffect(effect, keeper, null, "", -1, -1, -1);
            Assert.IsTrue(game.TurnManager.HasQueuedFreePlay(sealedCard.UID), "已登记");

            // 模拟"原子锁解除后"的处理：TurnManager 内部在 ResolveStackTop 末尾调用（这里直接走正常打出流程）
            Assert.IsTrue(game.TurnManager.PlayCard(1, sealedCard.UID, null, null, null, -1, null, null,
                freePlay: true, asIfQuick: true), "**免费 + 视作快速** → 应能打出（无需任何费用）");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "**未支付任何费用**");
            Assert.IsFalse(game.StackManager.IsEmpty, "已入堆叠（走正常打出流程）");
            Assert.IsNull(sealedCard.ReturnedFromSealHostUid, "离开手牌后清除『刚由封印回手』标记");
        }

        // ==================== ④ 取消＝一张都不打（全部留在手牌） ====================

        public void TestKeeper_取消则不打出()
        {
            var game = NewGame();
            var (keeper, sealedCard) = KeeperWithSealed(game, 1);
            var effect = FindEffect(keeper.PlayerTemplate, "JC119_2_RevealSealedAndFreePlayOne");
            game.EffectEngine.UIHooks = new PickStub { Pick = null };   // 取消
            game.CardManager.DestroyInstance(keeper);

            game.EffectEngine.ResolveEffect(effect, keeper, null, "", -1, -1, -1);

            Assert.IsFalse(game.TurnManager.HasQueuedFreePlay(sealedCard.UID), "未登记免费打出");
            Assert.AreEqual(CardZone.Hand, sealedCard.CurrentZone, "不打出的牌留在手牌（＝移回拥有者手上）");
        }

        // ==================== ⑤ 没有"刚由封印回手"的牌 → 无事发生 ====================

        public void TestKeeper_无封印牌则无事发生()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var keeper = game.CardManager.CreateCardInstance(Load("JC119_shoumiren"), 1);
            game.ZoneManager.MoveToArea(keeper, area, 1);
            var effect = FindEffect(keeper.PlayerTemplate, "JC119_2_RevealSealedAndFreePlayOne");
            var stub = new PickStub();
            game.EffectEngine.UIHooks = stub;

            game.EffectEngine.ResolveEffect(effect, keeper, null, "", -1, -1, -1);

            Assert.AreEqual(0, stub.Calls, "没有候选 → 不弹窗");
        }

        // ==================== ⑥ 需选目标的封印牌 → 走**目标选择窗**（卡目标） ====================

        /// <summary>探针附属（需选目标）：`TargetRules` 决定目标大类。</summary>
        private static PlayerCardData AttachProbe(string uid, TargetKind kind)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针附属_" + uid, Cost = 9,
                CategoryFlag = CardCategory.Attachment, Defense = -1,
                TargetRules = new Godot.Collections.Array<TargetRule> { TargetRule.Make(kind) },
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        /// <summary>堆叠里是否存在"来源＝该卡"的条目（守秘人死亡的触发条目会自行入栈，故不能用"堆叠为空"判定）。</summary>
        private static bool StackHasSource(GameInitializer game, string cardUid)
        {
            foreach (var e in game.StackManager.Entries)
                if (e != null && e.SourceCardUid == cardUid) return true;
            return false;
        }

        /// <summary>造"守秘人 + 其上一张**需要选目标**的封印牌"，消灭守秘人并结算死亡触发（登记免费打出）。
        /// 返回 (那张封印牌, 桩)。</summary>
        private static (CardInstance sealedCard, PickStub stub) KeeperWithSealedNeedTarget(
            GameInitializer game, PlayerCardData sealedTmpl)
        {
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var keeper = game.CardManager.CreateCardInstance(Load("JC119_shoumiren"), 1);
            game.ZoneManager.MoveToArea(keeper, area, 1);
            var sealedCard = game.CardManager.CreateCardInstance(sealedTmpl, 1);
            game.ZoneManager.MoveToHand(sealedCard, 1);
            game.TurnManager.SealCard(sealedCard, keeper, faceDown: true);

            var stub = new PickStub { Pick = sealedCard };
            game.EffectEngine.UIHooks = stub;
            game.CardManager.DestroyInstance(keeper);
            game.EffectEngine.ResolveEffect(FindEffect(keeper.PlayerTemplate, "JC119_2_RevealSealedAndFreePlayOne"),
                keeper, null, "", -1, -1, -1);
            Assert.IsTrue(game.TurnManager.HasQueuedFreePlay(sealedCard.UID), "前置：已登记免费打出");
            return (sealedCard, stub);
        }

        public void TestKeeper_需目标牌经目标选择窗打出()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];

            // 前置：选择窗弹出前需要"目标已锁定"？（不需要——窗口弹出后才由玩家选）
            var host = OnField(game, new PlayerCardData
            {
                UID = "TS_kp_host", CardName = "探针宿主", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 5, _effectsInjected = true,
            }, 1, area);

            var (sealedCard, stub) = KeeperWithSealedNeedTarget(game, AttachProbe("TS_kp_att", TargetKind.Character));
            stub.TargetPick = host.UID;                 // 玩家在目标选择窗里点了宿主
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            game.TurnManager.ProcessQueuedFreePlays();   // 原子锁解除后的队列处理

            Assert.AreEqual(1, stub.TargetCalls, "弹了一次目标选择窗");
            Assert.IsFalse(stub.LastIsArea, "目标模式＝卡目标（非地区）");
            Assert.IsNotNull(stub.LastTargetCandidates, "应给出候选");
            Assert.AreEqual(1, stub.LastTargetCandidates.Count, "候选＝场上唯一合法角色");
            Assert.AreEqual(host.UID, stub.LastTargetCandidates[0], "候选即该宿主");
            Assert.IsTrue(stub.LastPrompt != null && stub.LastPrompt.Contains("探针附属_TS_kp_att"),
                "窗口提示带卡名");
            Assert.IsTrue(stub.LastPrompt != null && stub.LastPrompt.Contains("免费打出"),
                "窗口提示点明『由效果免费打出』");

            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "免费打出：未支付任何费用（9 费也免费）");
            Assert.IsTrue(StackHasSource(game, sealedCard.UID), "已按正常打出流程入堆叠");

            // 结算该附属（走正常让过流程）→ 结附到所选宿主
            for (int i = 0; i < 6 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Assert.AreEqual(CardZone.AttachedZone, sealedCard.CurrentZone, "附属已结附（进入附属区）");
            Assert.AreEqual(host, sealedCard.AttachedTo, "结附到**目标选择窗里选中的**宿主");
        }

        // ==================== ⑦ 地区目标：目标窗切到"地区槽高亮"模式 ====================

        public void TestKeeper_需目标牌的地区目标模式()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();

            var (sealedCard, stub) = KeeperWithSealedNeedTarget(game, AttachProbe("TS_kp_att_area", TargetKind.Area));
            stub.TargetPick = areas[0];

            game.TurnManager.ProcessQueuedFreePlays();

            Assert.AreEqual(1, stub.TargetCalls, "弹了一次目标选择窗");
            Assert.IsTrue(stub.LastIsArea, "该卡只允许地区目标 → 走地区槽高亮模式");
            Assert.IsNotNull(stub.LastTargetCandidates, "应给出候选地区");
            Assert.IsTrue(stub.LastTargetCandidates.Contains(areas[0]), "候选含该地区");
            Assert.IsTrue(StackHasSource(game, sealedCard.UID), "已入堆叠");

            for (int i = 0; i < 6 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Assert.AreEqual(CardZone.AttachedZone, sealedCard.CurrentZone, "附属已结附于所选地区");
            Assert.AreEqual(areas[0], sealedCard.ZoneDetailId, "落点＝选中的地区");
        }

        // ==================== ⑧ 取消 / 无候选 → 留在手牌（绝不静默代打） ====================

        public void TestKeeper_需目标牌取消则留手牌()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            OnField(game, new PlayerCardData
            {
                UID = "TS_kp_host2", CardName = "探针宿主2", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 5, _effectsInjected = true,
            }, 1, area);

            var (sealedCard, stub) = KeeperWithSealedNeedTarget(game, AttachProbe("TS_kp_att2", TargetKind.Character));
            stub.TargetPick = null;                     // 玩家在目标窗里取消

            game.TurnManager.ProcessQueuedFreePlays();

            Assert.AreEqual(1, stub.TargetCalls, "弹了窗");
            Assert.AreEqual(CardZone.Hand, sealedCard.CurrentZone, "取消 → 该牌留在手牌");
            Assert.IsFalse(StackHasSource(game, sealedCard.UID), "取消 → 未入堆叠");
            Assert.IsFalse(game.TurnManager.HasQueuedFreePlay(sealedCard.UID), "已出队（不会反复弹窗）");
        }

        public void TestKeeper_需目标牌无合法目标则留手牌()
        {
            var game = NewGame();   // 场上没有任何角色 → 附属（需角色目标）没有合法目标
            var (sealedCard, stub) = KeeperWithSealedNeedTarget(game, AttachProbe("TS_kp_att3", TargetKind.Character));
            stub.TargetPick = "whatever";

            game.TurnManager.ProcessQueuedFreePlays();

            Assert.AreEqual(0, stub.TargetCalls, "无候选 → 不弹窗");
            Assert.AreEqual(CardZone.Hand, sealedCard.CurrentZone, "留在手牌");
            Assert.IsFalse(StackHasSource(game, sealedCard.UID), "未入堆叠");
        }

        // ==================== ⑨ 无 UI（headless/规则内核）→ 确定性兜底取首候选 ====================

        public void TestKeeper_需目标牌无UI确定性兜底()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var host = OnField(game, new PlayerCardData
            {
                UID = "TS_kp_host3", CardName = "探针宿主3", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 5, _effectsInjected = true,
            }, 1, area);

            var sealedCard = game.CardManager.CreateCardInstance(AttachProbe("TS_kp_att4", TargetKind.Character), 1);
            game.ZoneManager.MoveToHand(sealedCard, 1);
            game.EffectEngine.UIHooks = null;            // 无 UI
            game.TurnManager.EnqueueFreePlay(1, sealedCard.UID);

            game.TurnManager.ProcessQueuedFreePlays();

            Assert.IsTrue(StackHasSource(game, sealedCard.UID), "无 UI → 确定性兜底取第一个候选并打出");
            for (int i = 0; i < 6 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Assert.AreEqual(host, sealedCard.AttachedTo, "兜底目标＝首个合法候选（该宿主）");
        }
    }
}
