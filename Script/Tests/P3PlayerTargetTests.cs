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
    /// 【K24·玩家目标】按用户 2026-09 口径实现并锁定：
    ///  · 允许指定**任意对手（甚至自己）**：`TargetSpec{Kind=Player}` + `Restrictions`（`Enemy`＝对手 / `Friendly`＝本方 /
    ///    空＝任意）；未登记的写法（`Filter` 文本 / 其他限制条件）→ **fail-closed**（空候选 + 告警）；
    ///  · 需要**独立的选择窗口**：玩家不是场上的卡节点，无法用"点选/拖拽到目标"表达
    ///    （View 侧 `Arena.PromptPlayerTarget`；Model 侧候选＝`PlayerTargetPool.Collect`）；
    ///  · 目标**在入堆叠时锁定**（`StackEntry.TargetPlayerId`），结算时动作里写 `"目标玩家"` 即指向它；
    ///    未锁定时**不落任何玩家**（fail-closed：绝不替玩家乱动别人的牌）；
    ///  · 与 K15 多目标槽位**可组合**（`TargetSlot` 的 `Spec.Kind=Player`），入栈负载按**玩家槽对齐**。
    /// 覆盖卡：**JC111 混沌蝴蝶**（快速行动1：检视目标玩家牌库顶的一张牌；快速行动，横置：将目标玩家的牌库洗牌）。
    /// </summary>
    public class P3PlayerTargetTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_k24_asset_{playerId}_{i}", CardCategory.Attachment);
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

        /// <summary>双方连续让过一次 → 结算堆叠顶。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        private static List<string> DeckUids(GameInitializer game, int playerId)
        {
            var list = new List<string>();
            foreach (var c in game.TurnManager.GetDeckOf(playerId)) list.Add(c.UID);
            return list;
        }

        private static TargetSpec PlayerTarget(params TargetRestriction[] restrictions)
        {
            var spec = new TargetSpec { Kind = TargetKind.Player, NeedsSelection = true };
            foreach (var r in restrictions) if (r != TargetRestriction.None) spec.Restrictions.Add(r);
            return spec;
        }

        // ==================== 桩件：触发路径 ====================

        /// <summary>触发排序窗口桩：把所有触发按默认顺序送入堆叠（复用于触发类效果的测试）。</summary>
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

        /// <summary>玩家目标选择桩：记录调用并按脚本回包（-1＝取消）。</summary>
        private sealed class PlayerPickStub : Script.Interfaces.IGameUIHooks
        {
            public int Answer = -1;
            public int Calls;
            public string LastPrompt;
            public List<int> LastCandidates = new List<int>();

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
                => onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen)
            {
                Calls++;
                LastPrompt = prompt;
                LastCandidates = new List<int>(candidates ?? new List<int>());
                onChosen?.Invoke(Answer);
            }
        }

        /// <summary>把 JC057 以暗藏者身份放到 P1 的一个地区（供"现身触发"用）。</summary>
        private static CardInstance PlaceCovertJC057(GameInitializer game, string area)
        {
            var unit = game.CardManager.CreateCardInstance(Load("JC057_jiankongyuan"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            unit.SetCovert(true);
            return unit;
        }

        // ==================== ① 结构 ====================

        public void TestK24_JC111结构()
        {
            var tmpl = Load("JC111_hundunhudie");
            Assert.IsNotNull(tmpl, "JC111 模板应可加载");

            var a1 = FindEffect(tmpl, "JC111_1_InspectTopOfTargetPlayer");
            Assert.IsNotNull(a1, "JC111 应有『快速行动1：检视目标玩家牌库顶的一张牌』");
            Assert.AreEqual(EffectTiming.CharacterActivated, a1.Timing, "角色激活能力");
            Assert.AreEqual("快速", a1.ActivateWindow, "窗口＝快速（拥有优先权即可发动）");
            Assert.AreEqual(1, a1.Cost, "卡文『快速行动1』→ 费用 1");
            Assert.AreEqual(TargetKind.Player, a1.Target.Kind, "目标大类＝玩家");
            // 【2026-09 用户裁定】卡文只写"目标玩家"（未写"敌方玩家/对手"）⇒ **任意玩家、含自己**
            Assert.IsFalse(a1.Target.Restrictions.Contains(TargetRestriction.Enemy),
                "『目标玩家』＝任意玩家（含自己）⇒ 不得限制为对手");

            Assert.AreEqual(ActionVerb.Inspect, a1.Actions[0].Verb, "动作＝检视");
            Assert.AreEqual("目标玩家牌库顶", a1.Actions[0].Args[0], "检视对象＝目标玩家牌库顶（只读不取出）");

            var a2 = FindEffect(tmpl, "JC111_2_ShuffleTargetPlayerDeck");
            Assert.IsNotNull(a2, "JC111 应有『快速行动，横置：将目标玩家的牌库洗牌』");
            Assert.IsTrue(a2.ActivateCondition.Contains("横置本角色"), "横置自身为代价");
            Assert.AreEqual(TargetKind.Player, a2.Target.Kind, "目标大类＝玩家");
            Assert.AreEqual(ActionVerb.Shuffle, a2.Actions[0].Verb, "动作＝洗牌");
            Assert.AreEqual("目标玩家", a2.Actions[0].Args[0], "洗的是**目标玩家**的牌库");

            Assert.IsNotNull(FindEffect(tmpl, "JC111_A_Spirit"), "应有『灵体』关键词授予");
            Assert.IsNotNull(FindEffect(tmpl, "JC111_B_CannotBeAttached"), "应有『不能被装备结附』");
        }

        // ==================== ② 候选枚举与合法性（单一实现 PlayerTargetPool） ====================

        public void TestK24_候选枚举_任意本方对手()
        {
            var game = NewGame();
            var pm = game.PlayerManager;

            var any = PlayerTargetPool.Collect(PlayerTarget(TargetRestriction.None), 1, pm);
            Assert.AreEqual(2, any.Count, "无限制 → 任意玩家（含自己）");
            Assert.IsTrue(any.Contains(1) && any.Contains(2), "含 1 与 2");

            var enemy = PlayerTargetPool.Collect(PlayerTarget(TargetRestriction.Enemy), 1, pm);
            Assert.AreEqual(1, enemy.Count, "对手限制 → 只有 1 个候选");
            Assert.AreEqual(2, enemy[0], "P1 的对手＝P2");

            // 【2026-09 用户裁定·JC111】卡驱动的"任意玩家（含自己）"：
            // JC111 的两条能力都不限制敌我 ⇒ P1 的候选＝[P2（对手优先列出）, P1（自己排最后）]
            var jc111 = FindEffect(Load("JC111_hundunhudie"), "JC111_1_InspectTopOfTargetPlayer");
            Assert.IsNotNull(jc111, "JC111 应有检视能力");
            var jcCands = PlayerTargetPool.Collect(jc111.Target, 1, pm);
            Assert.AreEqual(2, jcCands.Count, "『目标玩家』＝任意玩家（含自己）⇒ 2 个候选");
            Assert.AreEqual(2, jcCands[0], "对手优先列出");
            Assert.AreEqual(1, jcCands[1], "自己排最后（合法性不变，仅顺序）");

            var friendly = PlayerTargetPool.Collect(PlayerTarget(TargetRestriction.Friendly), 2, pm);
            Assert.AreEqual(1, friendly.Count, "本方限制 → 只有 1 个候选");
            Assert.AreEqual(2, friendly[0], "P2 的本方＝P2 自己");

            // fail-closed：未登记的 Filter 写法 / 对玩家无意义的限制条件
            var badSpec = PlayerTarget();
            badSpec.Filter = "随便写的";
            Assert.AreEqual(0, PlayerTargetPool.Collect(badSpec, 1, pm).Count, "未登记写法 → 空候选（fail-closed）");

            var badRestriction = PlayerTarget(TargetRestriction.Tapped);
            Assert.AreEqual(0, PlayerTargetPool.Collect(badRestriction, 1, pm).Count,
                "对玩家无意义的限制条件 → 空候选（fail-closed）");
        }

        // ==================== ③ 激活路径：检视目标玩家牌库顶 ====================

        public void TestK24_JC111检视目标玩家牌库顶()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 2, (CardColor)5);   // 快速行动1 需要 1 点费用

            var unit = game.CardManager.CreateCardInstance(Load("JC111_hundunhudie"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            game.TurnManager.PriorityPlayerId = 1;

            var deck2Before = DeckUids(game, 2);
            var deck1Before = DeckUids(game, 1);
            Assert.IsTrue(deck2Before.Count > 0, "P2 牌库非空");
            string top2 = deck2Before[0];
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var inspected = new List<CardInstance>();
            var receivers = new List<int>();
            System.Action<CardInstance, int> handler = (c, by) => { inspected.Add(c); receivers.Add(by); };
            UiNotificationBus.Instance.CardInspected += handler;
            try
            {
                var a1 = FindEffect(unit.PlayerTemplate, "JC111_1_InspectTopOfTargetPlayer");
                EffectEngine.Instance.ActivateAbility(unit, a1, null, 2);
                Assert.IsFalse(game.StackManager.IsEmpty, "激活应入堆叠");
                ResolveStackOnce(game);

                Assert.AreEqual(1, inspected.Count, "应产生一次检视通知");
                Assert.AreEqual(top2, inspected[0].UID, "被检视的正是**P2 牌库顶**那张");
                Assert.AreEqual(1, receivers[0], "检视者＝效果来源控制者（私密）");
                Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付了 1 点费用（快速行动1）");

                Assert.AreEqual(deck2Before.Count, DeckUids(game, 2).Count, "**牌库张数不变**（检视不取出）");
                Assert.AreEqual(string.Join(",", deck2Before), string.Join(",", DeckUids(game, 2)), "**牌序不变**");
                Assert.AreEqual(string.Join(",", deck1Before), string.Join(",", DeckUids(game, 1)), "本方牌库不受影响");
                Assert.IsFalse(unit.IsTapped, "能力①不以横置为代价");
            }
            finally
            {
                UiNotificationBus.Instance.CardInspected -= handler;
            }
        }

        // ==================== ④ 激活路径：洗目标玩家的牌库（横置为代价） ====================

        public void TestK24_JC111洗目标玩家牌库()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var unit = game.CardManager.CreateCardInstance(Load("JC111_hundunhudie"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            game.TurnManager.PriorityPlayerId = 1;

            var deck2Before = DeckUids(game, 2);
            var deck1Before = DeckUids(game, 1);

            var a2 = FindEffect(unit.PlayerTemplate, "JC111_2_ShuffleTargetPlayerDeck");
            EffectEngine.Instance.ActivateAbility(unit, a2, null, 2);
            ResolveStackOnce(game);

            Assert.IsTrue(unit.IsTapped, "『横置』作为代价已付出");
            var deck2After = DeckUids(game, 2);
            Assert.AreEqual(deck2Before.Count, deck2After.Count, "洗牌不改变张数");
            Assert.IsFalse(string.Join(",", deck2Before) == string.Join(",", deck2After),
                "**顺序确实被洗乱**（不是空操作；随机源可复现，故结果确定）");
            var setBefore = new List<string>(deck2Before); setBefore.Sort();
            var setAfter = new List<string>(deck2After); setAfter.Sort();
            Assert.AreEqual(string.Join(",", setBefore), string.Join(",", setAfter), "洗牌不改变牌库内容（只是顺序）");
            Assert.AreEqual(string.Join(",", deck1Before), string.Join(",", DeckUids(game, 1)),
                "**只洗目标玩家（P2）的牌库**，本方牌库不动");
        }

        // ==================== ⑤ 目标校验：非法 / 缺失 ====================

        public void TestK24_非法目标被拒()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 2, (CardColor)5);

            var unit = game.CardManager.CreateCardInstance(Load("JC111_hundunhudie"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            game.TurnManager.PriorityPlayerId = 1;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var a1 = FindEffect(unit.PlayerTemplate, "JC111_1_InspectTopOfTargetPlayer");

            // 目标玩家号非法（0）→ 拒绝
            // 【2026-09 用户裁定·JC111】"目标玩家"＝**任意玩家、含自己** ⇒ "自己(1)"是合法目标，
            // 故本用例改用**非法玩家号 0** 验证 fail-closed。
            EffectEngine.Instance.ActivateAbility(unit, a1, null, 0);
            Assert.IsTrue(game.StackManager.IsEmpty, "非法玩家号（0）→ 不入堆叠");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "被拒 → 不扣费");

            // 目标玩家不存在（3）→ 拒绝
            EffectEngine.Instance.ActivateAbility(unit, a1, null, 3);
            Assert.IsTrue(game.StackManager.IsEmpty, "不存在的玩家 → 不入堆叠");

            // 合法（对手 2）→ 入堆叠
            EffectEngine.Instance.ActivateAbility(unit, a1, null, 2);
            Assert.IsFalse(game.StackManager.IsEmpty, "合法目标玩家 → 入堆叠");
        }

        public void TestK24_缺少目标玩家被拒()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 2, (CardColor)5);

            var unit = game.CardManager.CreateCardInstance(Load("JC111_hundunhudie"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            game.TurnManager.PriorityPlayerId = 1;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var a1 = FindEffect(unit.PlayerTemplate, "JC111_1_InspectTopOfTargetPlayer");
            EffectEngine.Instance.ActivateAbility(unit, a1, null);   // 未指定目标玩家

            Assert.IsTrue(game.StackManager.IsEmpty, "玩家目标未指定 → 拒绝发动（绝不静默代选）");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "被拒 → 不扣费");
        }

        // ==================== ⑥ 可用性判定（View 高亮/置灰用） ====================

        public void TestK24_可用性判定()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var unit = game.CardManager.CreateCardInstance(Load("JC111_hundunhudie"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);

            var a1 = FindEffect(unit.PlayerTemplate, "JC111_1_InspectTopOfTargetPlayer");
            Assert.IsTrue(ActivateEligibility.HasLegalTarget(unit, a1, game.CardManager, game.PlayerManager),
                "有合法玩家目标 → View 可高亮/可选");

            // 探针：目标写法未登记 → 无候选 → 不可用（fail-closed，不会显示成可发动）
            var probeTmpl = Probe("TS_k24_bad_player_target");
            probeTmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_k24_bad",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Target = new TargetSpec { Kind = TargetKind.Player, NeedsSelection = true, Filter = "不存在的人" },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Shuffle, Args = new Array<string> { "目标玩家" } } },
            });
            var probe = game.CardManager.CreateCardInstance(probeTmpl, 1);
            game.ZoneManager.MoveToArea(probe, area, 1);
            var bad = FindEffect(probe.PlayerTemplate, "TS_k24_bad");
            Assert.IsFalse(ActivateEligibility.HasLegalTarget(probe, bad, game.CardManager, game.PlayerManager),
                "未登记的目标写法 → 无合法目标（fail-closed）");
        }

        // ==================== ⑦ 未锁定目标玩家 → 不动任何人的牌 ====================

        public void TestK24_未锁定目标玩家时不动任何人的牌()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var unit = game.CardManager.CreateCardInstance(Load("JC111_hundunhudie"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);

            var deck1Before = DeckUids(game, 1);
            var deck2Before = DeckUids(game, 2);

            var a2 = FindEffect(unit.PlayerTemplate, "JC111_2_ShuffleTargetPlayerDeck");
            EffectEngine.Instance.ResolveEffect(a2, unit, null, area);   // 未传目标玩家（-1）

            Assert.AreEqual(string.Join(",", deck1Before), string.Join(",", DeckUids(game, 1)), "本方牌库未被动过");
            Assert.AreEqual(string.Join(",", deck2Before), string.Join(",", DeckUids(game, 2)), "对手牌库也未被动过（fail-closed）");
        }

        // ==================== ⑧ 与 K15 槽位组合 ====================

        /// <summary>"选择目标地区 + 目标对手"两个槽（JC065/JC128 类形状）：候选/合法性/对齐/反推。</summary>
        private static EffectData AreaAndPlayerSlots()
        {
            var data = new EffectData
            {
                EffectId = "TS_k24_slots",
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                TargetSlots = new Array<TargetSlot>
                {
                    new TargetSlot { Label = "目标地区", Optional = true,
                        Spec = new TargetSpec { Kind = TargetKind.Area, NeedsSelection = true } },
                    new TargetSlot { Label = "目标对手", Optional = true, Spec = PlayerTarget(TargetRestriction.Enemy) },
                },
                MinChosenSlots = 1,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Shuffle, Args = new Array<string> { "目标玩家" } },
                },
            };
            return data;
        }

        public void TestK24_槽位_玩家槽候选与校验()
        {
            var game = NewGame();
            var areas = Areas(game);
            var data = AreaAndPlayerSlots();
            var src = game.CardManager.CreateCardInstance(Probe("TS_k24_src"), 1);
            game.ZoneManager.MoveToArea(src, areas[0], 1);
            var pm = game.PlayerManager;

            var chosen = new List<TargetChoice> { TargetChoice.Area(areas[0]), TargetChoice.Empty };

            // 地区槽
            var areaCand = MultiTargetSlots.CollectSlot(data, 0, 1, chosen, game.ZoneManager, game.CardManager, pm, src, game.AreaManager);
            Assert.IsTrue(areaCand.IsArea, "第 1 槽＝地区槽");
            Assert.IsTrue(areaCand.AreaUids.Count >= 1, "地区槽有候选");
            Assert.IsFalse(areaCand.IsPlayer, "地区槽不是玩家槽");

            // 玩家槽
            var pCand = MultiTargetSlots.CollectSlot(data, 1, 1, chosen, game.ZoneManager, game.CardManager, pm, src, game.AreaManager);
            Assert.IsTrue(pCand.IsPlayer, "第 2 槽＝玩家槽");
            Assert.AreEqual(1, pCand.PlayerIds.Count, "对手限制 → 1 个候选");
            Assert.AreEqual(2, pCand.PlayerIds[0], "候选＝P2");
            Assert.AreEqual("目标对手", pCand.Label, "槽位提示＝目标对手（按限制推导）");

            // 合法性：自己非法（Enemy）、对手合法
            Assert.IsFalse(MultiTargetSlots.IsLegal(data, 1, TargetChoice.Player(1), 1, chosen,
                game.ZoneManager, game.CardManager, pm, src, game.AreaManager, out string selfReason),
                "玩家槽选『自己』不合法");
            Assert.IsNotNull(selfReason, "给出不合法原因");
            Assert.IsTrue(MultiTargetSlots.IsLegal(data, 1, TargetChoice.Player(2), 1, chosen,
                game.ZoneManager, game.CardManager, pm, src, game.AreaManager, out _), "选『对手』合法");

            // 同一玩家不能占两个玩家槽由 ChosenContainsPlayer 兜住（此处只有 1 个玩家槽，验证对齐与反推）
            var full = new List<TargetChoice> { TargetChoice.Area(areas[0]), TargetChoice.Player(2) };
            var aligned = MultiTargetSlots.SlotAlignedPlayerIds(data, full);
            Assert.AreEqual(1, aligned.Count, "按玩家槽对齐：1 个玩家槽");
            Assert.AreEqual(2, aligned[0], "对齐值为 2");

            var back = MultiTargetSlots.Reconstruct(data, areas[0], null, aligned);
            Assert.AreEqual(2, back.Count, "反推出 2 个槽");
            Assert.IsTrue(back[0].IsArea && back[0].Uid == areas[0], "地区槽还原");
            Assert.IsTrue(back[1].IsPlayer && back[1].PlayerId == 2, "玩家槽还原");

            Assert.IsNull(MultiTargetSlots.Validate(data, full, 1, game.ZoneManager, game.CardManager, pm, src, game.AreaManager),
                "整组合法");
            Assert.IsNotNull(MultiTargetSlots.Validate(data, new List<TargetChoice> { TargetChoice.Area(areas[0]), TargetChoice.Player(1) },
                1, game.ZoneManager, game.CardManager, pm, src, game.AreaManager), "选自己 → 整组不合法");
        }

        public void TestK24_槽位_结算按玩家槽执行()
        {
            var game = NewGame();
            var areas = Areas(game);
            var data = AreaAndPlayerSlots();
            var src = game.CardManager.CreateCardInstance(Probe("TS_k24_src2"), 1);
            game.ZoneManager.MoveToArea(src, areas[0], 1);

            var deck1Before = DeckUids(game, 1);
            var deck2Before = DeckUids(game, 2);

            var choices = new List<TargetChoice> { TargetChoice.Area(areas[0]), TargetChoice.Player(2) };
            EffectEngine.Instance.ResolveEffect(data, src, null, areas[0], -1, -1, -1, choices);

            var deck2After = DeckUids(game, 2);
            Assert.AreEqual(deck2Before.Count, deck2After.Count, "张数不变");
            Assert.IsFalse(string.Join(",", deck2Before) == string.Join(",", deck2After), "P2 牌库顺序被洗乱");
            var s1 = new List<string>(deck2Before); s1.Sort();
            var s2 = new List<string>(deck2After); s2.Sort();
            Assert.AreEqual(string.Join(",", s1), string.Join(",", s2), "内容不变（被洗的是 P2 牌库）");
            Assert.AreEqual(string.Join(",", deck1Before), string.Join(",", DeckUids(game, 1)),
                "**只洗目标玩家（P2）**：本方牌库不动");
        }

        // ==================== ⑨ 触发路径（JC057 现身触发 → 检视目标玩家手牌） ====================

        public void TestK24_JC057结构()
        {
            var tmpl = Load("JC057_jiankongyuan");
            Assert.IsNotNull(tmpl, "JC057 模板应可加载");
            var e = FindEffect(tmpl, "JC057_1_InspectTargetPlayerHand");
            Assert.IsNotNull(e, "JC057 应有『现身触发：检视目标玩家的手牌』");
            Assert.AreEqual(EffectTiming.UncoverTrigger, e.Timing, "时机＝现身触发（暗藏→公开）");
            Assert.AreEqual(TargetKind.Player, e.Target.Kind, "目标大类＝玩家");
            // 【2026-09 用户裁定】卡文只写"目标玩家"⇒ 任意玩家（含自己），不得限制为对手
            Assert.IsFalse(e.Target.Restrictions.Contains(TargetRestriction.Enemy),
                "『目标玩家』＝任意玩家（含自己）⇒ 不得限制为对手");
            Assert.AreEqual(ActionVerb.Inspect, e.Actions[0].Verb, "动作＝检视");
            Assert.AreEqual("目标玩家手牌", e.Actions[0].Args[0], "检视对象＝目标玩家的手牌");
        }

        /// <summary>端到端：暗藏者现身 → 触发经独立窗口选中对手 → 入堆叠 → 结算时**私密**检视对手全部手牌。</summary>
        public void TestK24_JC057现身触发检视对手手牌()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var unit = PlaceCovertJC057(game, area);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var stub = new PlayerPickStub { Answer = 2 };
            EffectEngine.Instance.UIHooks = stub;

            var hand2 = game.TurnManager.GetHandOf(2);
            Assert.IsTrue(hand2.Count > 0, "前置：对手手牌非空");
            var hand2Uids = new List<string>();
            foreach (var c in hand2) hand2Uids.Add(c.UID);

            var inspected = new List<CardInstance>();
            var receivers = new List<int>();
            System.Action<CardInstance, int> handler = (c, by) => { inspected.Add(c); receivers.Add(by); };
            UiNotificationBus.Instance.CardInspected += handler;
            try
            {
                game.CardManager.RevealCard(unit);   // 现身 → EnterTrigger + UncoverTrigger

                Assert.AreEqual(1, stub.Calls, "触发入堆叠前应弹出一次『目标玩家』独立窗口");
                Assert.AreEqual(2, stub.LastCandidates.Count, "任意玩家（含自己）⇒ 2 个候选");
                Assert.AreEqual(2, stub.LastCandidates[0], "对手优先列出（自己排最后）⇒ 首个候选＝P2");
                Assert.IsFalse(game.StackManager.IsEmpty, "选好目标玩家后触发应入堆叠");

                ResolveStackOnce(game);

                Assert.AreEqual(hand2Uids.Count, inspected.Count, "检视了目标玩家的**全部**手牌");
                foreach (var uid in hand2Uids)
                    Assert.IsTrue(inspected.Exists(x => x.UID == uid), $"含手牌 {uid}");
                foreach (var by in receivers) Assert.AreEqual(1, by, "检视者＝来源控制者（私密：对手看不到）");
                Assert.AreEqual(hand2Uids.Count, game.TurnManager.GetHandOf(2).Count, "检视不移动、不改状态");
                Assert.IsFalse(unit.IsCovert, "现身已生效（正面角色）");
            }
            finally
            {
                UiNotificationBus.Instance.CardInspected -= handler;
            }
        }

        /// <summary>取消选择目标玩家 → 该触发不入堆叠（绝不静默代选）。</summary>
        public void TestK24_触发路径取消则不入堆叠()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            PlaceCovertJC057(game, area);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            EffectEngine.Instance.UIHooks = new PlayerPickStub { Answer = -1 };

            var inspected = new List<CardInstance>();
            System.Action<CardInstance, int> handler = (c, by) => inspected.Add(c);
            UiNotificationBus.Instance.CardInspected += handler;
            try
            {
                game.CardManager.RevealCard(game.ZoneManager.GetZoneCards(CardZone.AreaZone, 0, area)[0]);
                Assert.IsTrue(game.StackManager.IsEmpty, "取消 → 触发不入堆叠（无检视）");
                Assert.AreEqual(0, inspected.Count, "取消 → 不检视任何牌");
            }
            finally
            {
                UiNotificationBus.Instance.CardInspected -= handler;
            }
        }

        /// <summary>无 UI（headless/测试）→ 确定性兜底取第一个候选；目标玩家仍写入堆叠负载。</summary>
        public void TestK24_触发路径无UI确定性兜底()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var unit = PlaceCovertJC057(game, area);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            EffectEngine.Instance.UIHooks = null;   // 无 UI

            game.CardManager.RevealCard(unit);

            Assert.AreEqual(1, game.StackManager.Entries.Count, "触发已入堆叠");
            var entry = game.StackManager.Entries[game.StackManager.Entries.Count - 1];
            Assert.AreEqual(2, entry.TargetPlayerId, "兜底取第一个候选（P1 的对手＝P2）并锁定在堆叠负载上");
            Assert.IsTrue(entry.Targets.Exists(r => r.Kind == StackTargetRef.RefKind.Player && r.PlayerId == 2),
                "箭头引用指向目标玩家");
        }

        // ==================== ⑩ 联机：SelectPlayer 协议与请求只发给做选择的人 ====================

        private sealed class PlayerTargetGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public int Answer = 2;

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                if (req.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTriggerOrder)
                {
                    var r = new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                    return System.Threading.Tasks.Task.FromResult(r);
                }
                return System.Threading.Tasks.Task.FromResult(new Script.Net.ChoiceResult
                {
                    RequestId = req.RequestId,
                    Cancelled = false,
                    SelectedPlayerId = Answer,
                });
            }
        }

        public void TestK24_联机_玩家目标请求只发给控制者()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var unit = PlaceCovertJC057(game, area);
            var gw = new PlayerTargetGateway { Answer = 2 };
            game.SetInputGateway(gw);

            game.CardManager.RevealCard(unit);

            var req = gw.Requests.Find(r => r.RequestKind == Script.Net.ChoiceRequest.Kind.SelectPlayer);
            Assert.IsNotNull(req, "应下发一次 SelectPlayer 请求");
            Assert.AreEqual(1, req.PlayerId, "**只发给做选择的人**（触发来源控制者 P1）");
            Assert.AreEqual(2, req.OptionPlayerIds.Count, "候选玩家号（任意玩家含自己 → 2 个）");
            Assert.AreEqual(2, req.OptionPlayerIds[0], "对手优先列出 ⇒ 首个候选＝P2");
            Assert.IsTrue(req.ForceChoice, "选目标不可取消（与卡目标同口径）");

            StackEntry entry = null;
            foreach (var e in game.StackManager.Entries) if (e.TargetPlayerId == 2) { entry = e; break; }
            Assert.IsNotNull(entry, "回包玩家号写回堆叠负载");
        }

        /// <summary>协议往返：新 Kind 与 `OptionPlayerIds` / `SelectedPlayerId` 字段。</summary>
        public void TestK24_联机_协议字段往返()
        {
            var req = new Script.Net.ChoiceRequest
            {
                RequestId = "req_p", PlayerId = 1,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectPlayer,
                Prompt = "请为 监控员 选择目标玩家",
                ForceChoice = true,
            };
            req.OptionPlayerIds.Add(2);

            var back = Script.Net.ChoiceSerializer.DeserializeRequest(Script.Net.ChoiceSerializer.Serialize(req));
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectPlayer, back.RequestKind, "Kind 往返");
            Assert.AreEqual(1, back.OptionPlayerIds.Count, "候选玩家号往返");
            Assert.AreEqual(2, back.OptionPlayerIds[0], "候选值往返");
            Assert.AreEqual(45, Script.Net.NetworkInputGateway.TimeoutSecondsFor(
                Script.Net.ChoiceRequest.Kind.SelectPlayer), "超时＝45s（选项少，短于选卡）");

            var res = new Script.Net.ChoiceResult { RequestId = "req_p", SelectedPlayerId = 2 };
            var rback = Script.Net.ChoiceSerializer.DeserializeResult(Script.Net.ChoiceSerializer.SerializeResult(res));
            Assert.AreEqual(2, rback.SelectedPlayerId, "回包玩家号往返");
        }
    }
}
