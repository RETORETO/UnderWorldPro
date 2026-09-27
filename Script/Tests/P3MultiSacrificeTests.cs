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
    /// 【P3·K22·多玩家/对手侧】"由指定玩家**自己选择**牺牲"（用户口径 A11/A12）：
    ///  · `ActionVerb.SacrificeChoice{种类, 谁, 地区}`；
    ///  · **每位玩家**：按**先手优先**顺序逐个自选（后选者可见先选者的选择——UI 高亮由 View 负责），
    ///    全部选定后**一起牺牲**（不分死亡先后）；
    ///  · **无合法候选者跳过、什么都不付出**；
    ///  · 牺牲＝死亡（`ResolveDeath(Sacrifice)`，照常触发死亡时机）；
    ///  · 覆盖卡：JC046（每位玩家各牺牲一个角色）/ JC113（各牺牲一个附属）/ JC134（各牺牲一个资产）/
    ///    JC065（目标对手牺牲该地区一个暗藏者 → 遏制1）。
    /// </summary>
    public class P3MultiSacrificeTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 46 };
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
                CategoryFlag = category, Defense = 4,
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

        /// <summary>选卡窗口桩：记录"谁被问过"，并按给定 UID（或第一个候选）回包。</summary>
        private sealed class PickStub : Script.Interfaces.IGameUIHooks
        {
            public readonly List<int> AskedPlayers = new List<int>();
            public readonly List<int> AskedCounts = new List<int>();
            public string PickUid;
            public bool ConfirmNumber;      // 数值输入桩：true＝确认（支付），false＝取消
            public int NumberCalls;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
            {
                AskedPlayers.Add(chooserPlayerId);
                AskedCounts.Add(candidates?.Count ?? 0);
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
            {
                NumberCalls++;
                onChosen?.Invoke(ConfirmNumber ? max : -1);
            }
        }

        // ==================== ① 结构：四张卡的写法 ====================

        public void TestMultiSacrifice_结构()
        {
            var jc046 = FindEffect(Load("JC046_wumiantuluzhe"), "JC046_B_UncoverEachPlayerSacrificesCharacter");
            Assert.IsNotNull(jc046, "JC046 应有『每位玩家各牺牲一个角色』");
            Assert.AreEqual(EffectTiming.UncoverTrigger, jc046.Timing, "JC046 时机＝现身");
            Assert.IsTrue(jc046.Forced, "强制触发");
            Assert.AreEqual("角色", jc046.Actions[0].Args[0], "种类＝角色");
            Assert.AreEqual("每位玩家", jc046.Actions[0].Args[1], "谁＝每位玩家");

            var jc113 = FindEffect(Load("JC113_wuxingdaodanjing"), "JC113_A_EnterEachPlayerSacrificesAttachment");
            Assert.IsNotNull(jc113, "JC113 应有『每位玩家各牺牲一个附属』");
            Assert.AreEqual(EffectTiming.EnterTrigger, jc113.Timing, "JC113 时机＝进场");
            Assert.AreEqual("附属", jc113.Actions[0].Args[0], "种类＝附属");

            var jc134 = FindEffect(Load("JC134_shenpan"), "JC134_A_EachPlayerSacrificesAsset");
            Assert.IsNotNull(jc134, "JC134 应有『每位玩家牺牲一个资产』");
            Assert.AreEqual(EffectTiming.OnSorceryCast, jc134.Timing, "JC134＝事务卡打出即结算");
            Assert.AreEqual("资产", jc134.Actions[0].Args[0], "种类＝资产");

            var jc065 = FindEffect(Load("JC065_shenwenxianyiren"), "JC065_A_OpponentSacrificesCovertInArea");
            Assert.IsNotNull(jc065, "JC065 应有『对手牺牲该地区暗藏者 → 遏制1』");
            Assert.AreEqual("暗藏者", jc065.Actions[0].Args[0], "种类＝暗藏者");
            Assert.AreEqual("目标玩家", jc065.Actions[0].Args[1], "谁＝目标玩家（对手）");
            Assert.AreEqual("目标地区", jc065.Actions[0].Args[2], "地区＝目标地区");
            Assert.AreEqual(ActionVerb.Curb, jc065.Actions[1].Verb, "随后遏制");
        }

        // ==================== ② 每位玩家各牺牲一个角色：先手先问 → 一起牺牲 ====================

        public void TestMultiSacrifice_每位玩家各牺牲一个角色()
        {
            var game = NewGame();
            game.TurnManager.FirstPlayerId = 1;
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var p1a = OnField(game, Probe("TS_ms_p1a"), 1, area);
            var p1b = OnField(game, Probe("TS_ms_p1b"), 1, area);
            var p2a = OnField(game, Probe("TS_ms_p2a"), 2, area);

            var stub = new PickStub();
            EffectEngine.Instance.UIHooks = stub;

            var e = FindEffect(Load("JC046_wumiantuluzhe"), "JC046_B_UncoverEachPlayerSacrificesCharacter");
            EffectEngine.Instance.ResolveEffect(e, p1a, null, "");

            Assert.AreEqual(2, stub.AskedPlayers.Count, "两个玩家各被问了一次");
            Assert.AreEqual(1, stub.AskedPlayers[0], "先问先手玩家（P1）");
            Assert.AreEqual(2, stub.AskedPlayers[1], "再问后手玩家（P2）");
            Assert.AreEqual(2, stub.AskedCounts[0], "P1 的候选＝其 2 个角色");

            Assert.AreEqual(CardZone.Graveyard, p1a.CurrentZone, "P1 被牺牲者进墓地");
            Assert.AreEqual(CardZone.Graveyard, p2a.CurrentZone, "P2 被牺牲者进墓地");
            Assert.AreEqual(CardZone.AreaZone, p1b.CurrentZone, "P1 未被选中的角色留在场上");
        }

        // ==================== ③ 没有可牺牲者 → 跳过、什么都不付出 ====================

        public void TestMultiSacrifice_无可牺牲者则跳过()
        {
            var game = NewGame();
            game.TurnManager.FirstPlayerId = 1;
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var only = OnField(game, Probe("TS_ms_only"), 1, area);   // 只有 P1 有角色

            var stub = new PickStub();
            EffectEngine.Instance.UIHooks = stub;

            var e = FindEffect(Load("JC046_wumiantuluzhe"), "JC046_B_UncoverEachPlayerSacrificesCharacter");
            EffectEngine.Instance.ResolveEffect(e, only, null, "");

            Assert.AreEqual(1, stub.AskedPlayers.Count, "只有有候选的玩家被问（P2 无角色 → 跳过）");
            Assert.AreEqual(1, stub.AskedPlayers[0], "被问的是 P1");
            Assert.AreEqual(CardZone.Graveyard, only.CurrentZone, "P1 的角色被牺牲");
        }

        // ==================== ④ 每位玩家牺牲一个资产（JC134） ====================

        public void TestMultiSacrifice_每位玩家牺牲一个资产()
        {
            var game = NewGame();
            game.TurnManager.FirstPlayerId = 2;   // 先手是 P2 → 应先问 P2
            var a1 = game.CardManager.CreateCardInstance(Probe("TS_ms_asset1", CardCategory.Attachment), 1);
            var a2 = game.CardManager.CreateCardInstance(Probe("TS_ms_asset2", CardCategory.Attachment), 2);
            game.ZoneManager.MoveToAsset(a1, 1);
            game.ZoneManager.MoveToAsset(a2, 2);

            var stub = new PickStub();
            EffectEngine.Instance.UIHooks = stub;

            var e = FindEffect(Load("JC134_shenpan"), "JC134_A_EachPlayerSacrificesAsset");
            EffectEngine.Instance.ResolveEffect(e, a1, null, "");

            Assert.AreEqual(2, stub.AskedPlayers.Count, "两位玩家都被问");
            Assert.AreEqual(2, stub.AskedPlayers[0], "先问先手玩家（本轮先手＝P2）");
            Assert.AreEqual(1, stub.AskedPlayers[1], "再问后手玩家（P1）");
            Assert.IsFalse(a1.CurrentZone == CardZone.AssetZone, "P1 的资产被牺牲（离开资产区）");
            Assert.IsFalse(a2.CurrentZone == CardZone.AssetZone, "P2 的资产被牺牲（离开资产区）");
        }

        // ==================== ④b JC128 替罪羊：标志够则问、不够必须牺牲、无角色则什么都不付出 ====================

        public void TestMultiSacrifice_JC128标志不足必须牺牲()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var spell = game.CardManager.CreateCardInstance(Load("JC128_tizuiyang"), 1);
            game.ZoneManager.MoveToHand(spell, 1);
            OnField(game, Probe("TS_128_p1"), 1, area);
            var victim = OnField(game, Probe("TS_128_victim"), 2, area);
            game.AreaManager.AddPowerMark(area, 2, 2);   // P2 只有 2 个标志（< 3）

            var stub = new PickStub();
            EffectEngine.Instance.UIHooks = stub;
            var e = FindEffect(spell.PlayerTemplate, "JC128_A_BribeOrSacrifice");
            EffectEngine.Instance.ResolveEffect(e, spell, null, "", -1, -1, -1, null, -1, 2);   // 目标玩家＝P2

            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "标志不足 3 → 必须牺牲一个角色");
        }

        public void TestMultiSacrifice_JC128标志足够时选择移除标志()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var spell = game.CardManager.CreateCardInstance(Load("JC128_tizuiyang"), 1);
            game.ZoneManager.MoveToHand(spell, 1);
            OnField(game, Probe("TS_128_p1b"), 1, area);
            var victim = OnField(game, Probe("TS_128_victim2"), 2, area);
            game.AreaManager.AddPowerMark(area, 2, 4);   // P2 有 4 个标志

            var stub = new PickStub { ConfirmNumber = true };   // 确认＝移除标志
            EffectEngine.Instance.UIHooks = stub;
            var e = FindEffect(spell.PlayerTemplate, "JC128_A_BribeOrSacrifice");
            EffectEngine.Instance.ResolveEffect(e, spell, null, "", -1, -1, -1, null, -1, 2);

            Assert.AreEqual(1, stub.NumberCalls, "标志足够 → 询问过该玩家一次");
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "选择移除标志 → 不牺牲角色");
        }

        public void TestMultiSacrifice_JC128没有角色时牺牲路线什么都不付出()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var spell = game.CardManager.CreateCardInstance(Load("JC128_tizuiyang"), 1);
            game.ZoneManager.MoveToHand(spell, 1);
            OnField(game, Probe("TS_128_p1c"), 1, area);
            game.AreaManager.AddPowerMark(area, 2, 4);   // 标志足够，但 P2 **没有角色**

            var stub = new PickStub { ConfirmNumber = false };   // 取消＝走牺牲路线
            EffectEngine.Instance.UIHooks = stub;
            var e = FindEffect(spell.PlayerTemplate, "JC128_A_BribeOrSacrifice");
            EffectEngine.Instance.ResolveEffect(e, spell, null, "", -1, -1, -1, null, -1, 2);

            Assert.AreEqual(0, stub.AskedPlayers.Count, "没有角色 → 牺牲路线无候选、不弹选卡窗（什么都不付出）");
            Assert.AreEqual(4, game.AreaManager.GetPowerMark(area, 2), "标志也未被动过");
        }

        // ==================== ⑤ 对手牺牲该地区暗藏者（JC065，限地区） ====================

        public void TestMultiSacrifice_对手牺牲该地区暗藏者()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            string target = areas[0];
            var inArea = game.CardManager.CreateCardInstance(Probe("TS_ms_covert_in"), 2);
            game.ZoneManager.MoveToArea(inArea, target, 2);
            inArea.SetCovert(true);
            var otherArea = game.CardManager.CreateCardInstance(Probe("TS_ms_covert_out"), 2);
            game.ZoneManager.MoveToArea(otherArea, areas[1], 2);
            otherArea.SetCovert(true);

            var stub = new PickStub { PickUid = otherArea.UID };   // 即使桩想选别的地区，也必须被拒（候选里没有）
            EffectEngine.Instance.UIHooks = stub;

            // 来源＝**施术者 P1 打出的事务卡**（对手＝P2 才是"目标对手"）
            var spell = game.CardManager.CreateCardInstance(Load("JC065_shenwenxianyiren"), 1);
            game.ZoneManager.MoveToHand(spell, 1);
            var e = FindEffect(spell.PlayerTemplate, "JC065_A_OpponentSacrificesCovertInArea");
            EffectEngine.Instance.ResolveEffect(e, spell, null, target);

            Assert.AreEqual(1, stub.AskedPlayers.Count, "只问目标对手（K24 缺省＝施术者对手）");
            Assert.AreEqual(2, stub.AskedPlayers[0], "被问的是 P2");
            Assert.IsTrue(stub.AskedCounts[0] >= 1, "候选＝该地区的暗藏者");
            Assert.IsFalse(inArea.CurrentZone == CardZone.AreaZone, "该地区内的暗藏者被牺牲");
            Assert.AreEqual(CardZone.AreaZone, otherArea.CurrentZone, "别的地区的暗藏者不受影响（限地区）");
        }

        // ==================== ⑥ 异步窗口（真实 Arena 弹窗语义）回归：未点击前不得牺牲 ====================

        /// <summary>
        /// **异步窗口桩**：模拟真实 `Arena.PromptPickCard`——只登记"窗口已打开"与回调，
        /// **不**立即触发（真实窗口在玩家点击时才回调）。用于锁住 P0 缺陷：
        /// 原实现在发起询问后**同一帧内**就 `chosen ??= candidates[0]` 并 `ResolveDeath`
        /// ⇒ 窗口刚弹出、第一候选就被牺牲，玩家点击无效（同步桩长期掩盖）。
        /// </summary>
        private sealed class AsyncPickStub : Script.Interfaces.IGameUIHooks
        {
            public readonly List<(int PlayerId, List<CardInstance> Candidates, System.Action<CardInstance> Callback)> Pending
                = new List<(int, List<CardInstance>, System.Action<CardInstance>)>();

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
                => Pending.Add((chooserPlayerId, candidates, onPicked));   // 不触发回调＝等待玩家点击
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid)
                => onPickedAreaUid?.Invoke(null);   // 不做分配（按兜底补齐）
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

            /// <summary>"玩家点击"：取当前打开的窗口，选中指定 UID 的候选（null＝第一候选）并回调。</summary>
            public void ClickOldest(string pickUid)
            {
                Assert.IsTrue(Pending.Count > 0, "前置：应有一个等待玩家的窗口");
                var (_, candidates, cb) = Pending[0];
                Pending.RemoveAt(0);
                CardInstance picked = null;
                if (!string.IsNullOrEmpty(pickUid) && candidates != null)
                    foreach (var c in candidates) if (c.UID == pickUid) picked = c;
                picked ??= (candidates != null && candidates.Count > 0 ? candidates[0] : null);
                cb?.Invoke(picked);
            }
        }

        public void TestMultiSacrifice_异步窗口_未点击前不牺牲()
        {
            var game = NewGame();
            game.TurnManager.FirstPlayerId = 1;
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var p1a = OnField(game, Probe("TS_ms_as_p1a"), 1, area);
            var p1b = OnField(game, Probe("TS_ms_as_p1b"), 1, area);
            var p2a = OnField(game, Probe("TS_ms_as_p2a"), 2, area);

            var stub = new AsyncPickStub();
            EffectEngine.Instance.UIHooks = stub;

            var e = FindEffect(Load("JC046_wumiantuluzhe"), "JC046_B_UncoverEachPlayerSacrificesCharacter");
            EffectEngine.Instance.ResolveEffect(e, p1a, null, "");

            Assert.AreEqual(1, stub.Pending.Count, "只开了先手玩家（P1）的窗口（后手窗口要等这一位选完）");
            Assert.AreEqual(1, stub.Pending[0].PlayerId, "先问先手玩家");
            Assert.AreEqual(CardZone.AreaZone, p1a.CurrentZone,
                "**玩家尚未点击 → 绝不允许牺牲任何卡**（回归：旧实现此刻已把第一候选牺牲掉）");
            Assert.AreEqual(CardZone.AreaZone, p1b.CurrentZone, "同上（第二候选同样不该被牺牲）");

            // 玩家点击"第二个候选" → 记录选择；**按既定口径"全部选完再一起牺牲"**，此刻尚未落地
            stub.ClickOldest(p1b.UID);
            Assert.AreEqual(CardZone.AreaZone, p1b.CurrentZone,
                "选完 P1 后仍未落地牺牲（口径：全部选定后**一起**牺牲）");
            Assert.AreEqual(1, stub.Pending.Count, "继续询问下一位玩家（P2）");
            Assert.AreEqual(2, stub.Pending[0].PlayerId, "被问的是后手玩家");

            stub.ClickOldest(p2a.UID);
            Assert.AreEqual(CardZone.Graveyard, p1b.CurrentZone, "全部选完 → 按**玩家点击的候选**牺牲（不是第一候选）");
            Assert.AreEqual(CardZone.Graveyard, p2a.CurrentZone, "P2 的选择同样按点击落地");
            Assert.AreEqual(CardZone.AreaZone, p1a.CurrentZone, "未被点击的候选始终留在场上（旧实现会误牺牲它）");
            Assert.AreEqual(0, stub.Pending.Count, "两位玩家都选完 → 不再有窗口");
        }

        public void TestMultiSacrifice_异步窗口_全部选完才落地且动作链续算()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            string target = areas[0];
            // P2 在该地区有一个暗藏者（待牺牲）+ 1 个势力标志（供随后的「遏制1」消费）
            var covert = game.CardManager.CreateCardInstance(Probe("TS_ms_as_cov"), 2);
            game.ZoneManager.MoveToArea(covert, target, 2);
            covert.SetCovert(true);
            game.AreaManager.AddPowerMark(target, 2, 1);
            int marksBefore = game.AreaManager.GetPowerMark(target, 2);

            var stub = new AsyncPickStub();
            EffectEngine.Instance.UIHooks = stub;

            var spell = game.CardManager.CreateCardInstance(Load("JC065_shenwenxianyiren"), 1);
            game.ZoneManager.MoveToHand(spell, 1);
            var e = FindEffect(spell.PlayerTemplate, "JC065_A_OpponentSacrificesCovertInArea");
            EffectEngine.Instance.ResolveEffect(e, spell, null, target);

            Assert.AreEqual(1, stub.Pending.Count, "打开了目标对手的选择窗");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "未点击前不牺牲");
            Assert.AreEqual(marksBefore, game.AreaManager.GetPowerMark(target, 2),
                "未点击前**后续动作（遏制1）也不得执行**（动作链应处于挂起状态）");

            stub.ClickOldest(covert.UID);

            Assert.IsFalse(covert.CurrentZone == CardZone.AreaZone, "点击后按所选牺牲");
            Assert.AreEqual(marksBefore - 1, game.AreaManager.GetPowerMark(target, 2),
                "牺牲落地后**动作链续算**：随后的『遏制1』已执行");
        }
    }
}
