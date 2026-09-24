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
    /// 【K23·次数上限（每回合每能力）】按用户 2026-09 口径实现并锁定：
    ///  · 计数基准 = **每张卡 · 每回合 · 每能力**（按 `EffectId` 分别计数）；
    ///  · **卡版本号变动 ⇒ 该卡的限制重新可用**（`CardInstance.BumpVersion` 清空计数）；
    ///  · 回合重置发生在**重置步骤**（`TurnManager.DoReset` → `CardManager.ResetAllAbilityUsesThisTurn`）；
    ///  · 消费点：激活＝`ActivateEligibility` 门禁 + `EffectEngine.ActivateAbility` 登记；
    ///    触发＝`TurnManager.ResolveEntry` 的 Trigger 分支（结算前检查与登记，避免同批次多条触发超限）。
    /// 覆盖卡：**JC032 血族长老**（行动2：牌库顶 6 张检索吸血鬼角色牌，以暗藏者状态置于本地区，每回合一次）。
    /// </summary>
    public class P3UseLimitTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 61 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
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

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>造一张"每回合限 1 次"的激活探针卡（快速行动、无目标、无费用）。</summary>
        private static PlayerCardData LimitedProbe(string uid, string effectId, int maxPerTurn = 1,
            EffectTiming timing = EffectTiming.CharacterActivated)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = effectId,
                Timing = timing,
                ActivateWindow = timing == EffectTiming.CharacterActivated ? "快速" : "",
                MaxUsesPerTurn = maxPerTurn,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "1" } } },
            });
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static bool StackHas(GameInitializer game, string sourceUid)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.SourceCardUid == sourceUid) return true;
            return false;
        }

        // ==================== ① 计数粒度：每卡 · 每回合 · 每能力 ====================

        public void TestK23_计数按每卡每能力独立()
        {
            var game = NewGame();
            var card = game.CardManager.CreateCardInstance(LimitedProbe("TS_k23_a", "TS_k23_a_e1"), 1);

            Assert.AreEqual(0, card.GetAbilityUsesThisTurn("TS_k23_a_e1"), "初始未使用");
            card.MarkAbilityUsedThisTurn("TS_k23_a_e1");
            Assert.AreEqual(1, card.GetAbilityUsesThisTurn("TS_k23_a_e1"), "同一 EffectId 计数累加");
            Assert.AreEqual(0, card.GetAbilityUsesThisTurn("TS_k23_a_e2"), "**不同能力分别计数**（另一个 EffectId 仍为 0）");
            Assert.IsTrue(card.IsAbilityLimitReached("TS_k23_a_e1", 1), "已达上限（上限 1）");
            Assert.IsFalse(card.IsAbilityLimitReached("TS_k23_a_e2", 1), "另一能力未达上限");

            card.MarkAbilityUsedThisTurn("TS_k23_a_e1");
            Assert.IsTrue(card.IsAbilityLimitReached("TS_k23_a_e1", 2), "上限 2 时第 2 次后达上限");
            Assert.IsFalse(card.IsAbilityLimitReached("TS_k23_a_e1", 0), "上限 0 = 不限次");
        }

        public void TestK23_两张卡各自计数()
        {
            var game = NewGame();
            var a = game.CardManager.CreateCardInstance(LimitedProbe("TS_k23_c1", "TS_k23_e"), 1);
            var b = game.CardManager.CreateCardInstance(LimitedProbe("TS_k23_c2", "TS_k23_e"), 1);

            a.MarkAbilityUsedThisTurn("TS_k23_e");
            Assert.IsTrue(a.IsAbilityLimitReached("TS_k23_e", 1), "A 已达上限");
            Assert.IsFalse(b.IsAbilityLimitReached("TS_k23_e", 1), "**另一张卡的同名能力不受影响**（每张卡分别计数）");
        }

        // ==================== ② 门禁：达上限后不可发动（高亮/候选/权威三层一致） ====================

        public void TestK23_达上限后不可发动与候选剔除()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var tmpl = LimitedProbe("TS_k23_gate", "TS_k23_gate_e");
            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(card, area, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var e = tmpl.EffectDataList[0];
            Assert.IsTrue(ActivateEligibility.CanActivate(card, game.TurnManager, game.PlayerManager, game.CardManager),
                "前置：未使用时可以发动");
            Assert.IsTrue(ActivateEligibility.GetEligibleAbilities(1, game.TurnManager, game.PlayerManager, game.CardManager)
                .Exists(x => x.Source == card), "前置：候选池包含该激活");

            // 第 1 次发动 → 入堆叠并登记计数
            EffectEngine.Instance.ActivateAbility(card, e, null);
            Assert.AreEqual(1, card.GetAbilityUsesThisTurn("TS_k23_gate_e"), "发动被接受 → 计数 = 1");
            Assert.IsTrue(StackHas(game, card.UID), "第 1 次发动应入堆叠");

            // 达上限：高亮/候选/权威三层都应拒绝
            Assert.IsFalse(ActivateEligibility.CanActivate(card, game.TurnManager, game.PlayerManager, game.CardManager),
                "已达上限 → CanActivate=false（不高亮）");
            Assert.IsFalse(ActivateEligibility.GetEligibleAbilities(1, game.TurnManager, game.PlayerManager, game.CardManager)
                .Exists(x => x.Source == card), "已达上限 → 候选池剔除该激活");

            int stackBefore = game.StackManager.Entries.Count;
            EffectEngine.Instance.ActivateAbility(card, e, null);
            Assert.AreEqual(stackBefore, game.StackManager.Entries.Count,
                "已达上限 → 直接调用 ActivateAbility 也被拒绝（不新增堆叠条目）");
            Assert.AreEqual(1, card.GetAbilityUsesThisTurn("TS_k23_gate_e"), "被拒绝的发动不计入次数");
        }

        // ==================== ③ 版本号变动 ⇒ 限制重新可用（用户口径） ====================

        public void TestK23_版本号变动后限制重新可用()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var tmpl = LimitedProbe("TS_k23_ver", "TS_k23_ver_e");
            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(card, area, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            EffectEngine.Instance.ActivateAbility(card, tmpl.EffectDataList[0], null);
            Assert.IsTrue(card.IsAbilityLimitReached("TS_k23_ver_e", 1), "前置：已达上限");

            // 版本号变动（示例：区域移动 → FireMoved → BumpVersion）
            int verBefore = card.VersionId;
            game.ZoneManager.MoveToArea(card, area, 1);   // 同区重放不 BumpVersion（只发 OnMove）——改用跨区移动
            Assert.AreEqual(verBefore, card.VersionId, "前置：争夺区→争夺区/同区不刷新版本号");
            Assert.IsTrue(card.IsAbilityLimitReached("TS_k23_ver_e", 1), "版本号未变 → 限制仍然生效");

            card.BumpVersion();   // 版本号变动（翻面/离场等任何 BumpVersion 场景）
            Assert.IsFalse(card.IsAbilityLimitReached("TS_k23_ver_e", 1),
                "版本号变动 → 『每回合一次』的限制重新可用（用户口径）");
            Assert.IsTrue(ActivateEligibility.CanActivate(card, game.TurnManager, game.PlayerManager, game.CardManager),
                "版本号变动后应重新可发动");
        }

        // ==================== ④ 回合重置（重置步骤） ====================

        public void TestK23_重置步骤清空计数()
        {
            var game = NewGame();
            var card = game.CardManager.CreateCardInstance(LimitedProbe("TS_k23_reset", "TS_k23_reset_e"), 1);
            card.MarkAbilityUsedThisTurn("TS_k23_reset_e");
            var other = game.CardManager.CreateCardInstance(LimitedProbe("TS_k23_reset2", "TS_k23_reset_e"), 1);
            other.MarkAbilityUsedThisTurn("TS_k23_reset_e");
            Assert.IsTrue(card.IsAbilityLimitReached("TS_k23_reset_e", 1), "前置：已达上限");

            game.CardManager.ResetAllAbilityUsesThisTurn();   // 重置步骤调用（`TurnManager.DoReset`）

            Assert.AreEqual(0, card.GetAbilityUsesThisTurn("TS_k23_reset_e"), "回合重置后计数清零（本卡）");
            Assert.AreEqual(0, other.GetAbilityUsesThisTurn("TS_k23_reset_e"), "回合重置后计数清零（所有卡）");
        }

        // ==================== ⑤ 触发类能力的次数上限（结算前检查 + 登记） ====================

        public void TestK23_触发类能力同样受限()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);

            // 旁观者探针：每当有角色死亡 → 计数型触发（每回合 1 次）
            var watcherTmpl = new PlayerCardData
            {
                UID = "TS_k23_watcher", CardName = "探针_死亡计数", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            watcherTmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_k23_watcher_e",
                Timing = EffectTiming.OnCardDied,
                Condition = new EffectCondition { Predicate = "DiedAnyCharacter" },
                MaxUsesPerTurn = 1,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "1" } } },
            });
            watcherTmpl._effectsInjected = true;
            var watcher = game.CardManager.CreateCardInstance(watcherTmpl, 1);
            game.ZoneManager.MoveToArea(watcher, area, 1);

            // 两次死亡（各自触发；第二次应因已达上限而跳过结算）
            var v1 = game.CardManager.CreateCardInstance(Probe("TS_k23_v1"), 2);
            game.ZoneManager.MoveToArea(v1, area, 2);
            var v2 = game.CardManager.CreateCardInstance(Probe("TS_k23_v2"), 2);
            game.ZoneManager.MoveToArea(v2, area, 2);

            game.CardManager.DestroyInstance(v1);
            game.CardManager.DestroyInstance(v2);

            // 两次死亡 → 收集到 2 条同源触发（同时死亡：触发窗口在"导致死亡的效果结算完毕后"打开）
            Assert.AreEqual(2, game.StackManager.Entries.Count, "两次死亡各收集一条触发（同时死亡 → 玩家自行排序，共 2 条）");

            // 双方让过 → 逐条结算
            ResolveStackOnce(game);
            ResolveStackOnce(game);

            int uses = watcher.GetAbilityUsesThisTurn("TS_k23_watcher_e");
            Assert.AreEqual(1, uses, "触发类能力的『每回合一次』：两次死亡最多结算 1 次（结算前检查并登记）");
        }

        /// <summary>双方连续让过一次 → 结算堆叠顶（`TurnManager.Pass` 要求当前优先权持有者才能让过）。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        /// <summary>触发排序窗口网关：全部入堆叠（与 P1/P4 测试同款）。</summary>
        private sealed class EnterAllGateway : Script.Net.IInputGateway
        {
            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                for (int i = 0; i < (req?.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        // ==================== ⑥ JC032 结构 + 端到端 ====================

        public void TestK23_JC032结构()
        {
            var tmpl = Load("JC032_xuezuzhanglao");
            Assert.IsNotNull(tmpl, "JC032 模板应可加载");
            var e = FindEffect(tmpl, "JC032_1_SearchVampireCovert");
            Assert.IsNotNull(e, "JC032 应有『牌库顶 6 张检索吸血鬼』效果");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "为角色激活");
            Assert.AreEqual("行动", e.ActivateWindow, "行动2 → 行动窗口");
            Assert.AreEqual(2, e.Cost, "费用 = 2（行动2）");
            Assert.AreEqual(1, e.MaxUsesPerTurn, "『该能力每回合只能发动一次』");
            Assert.AreEqual(1, e.Actions.Count, "单动作");
            Assert.AreEqual(ActionVerb.Search, e.Actions[0].Verb, "动作为检索");
            Assert.AreEqual("类别:角色;子类别:吸血鬼", e.Actions[0].Args[0], "筛选＝吸血鬼角色");
            Assert.AreEqual("暗藏进场", e.Actions[0].Args[1], "去向＝暗藏进场");
            Assert.AreEqual("牌库顶:6", e.Actions[0].Args[3], "范围＝牌库顶 6 张");
        }

        /// <summary>端到端：JC032 发动 → 牌库顶 6 张里的吸血鬼角色牌以暗藏者状态进入本地区。</summary>
        public void TestK23_JC032_检索吸血鬼暗藏进场()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var elder = game.CardManager.CreateCardInstance(Load("JC032_xuezuzhanglao"), 1);
            game.ZoneManager.MoveToArea(elder, area, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            // 牌库顶放 6 张：第 4 张为吸血鬼角色（探针），其余为普通角色
            var deck = new List<CardInstance>();
            for (int i = 0; i < 6; i++)
            {
                var tmpl = new PlayerCardData
                {
                    UID = $"TS_k23_deck{i}", CardName = $"探针_牌库{i}", Cost = 1,
                    CategoryFlag = CardCategory.Character, Defense = 1,
                };
                if (i == 3) tmpl.SubCategories.Add("吸血鬼");
                tmpl._effectsInjected = true;
                var c = game.CardManager.CreateCardInstance(tmpl, 1);
                c.CurrentZone = CardZone.Deck;
                deck.Add(c);
            }
            game.DeckManager.SetDeckPlayer1(deck);

            // 直接结算该效果（激活门禁/入堆叠/结算链路由既有套件覆盖）
            var vampire = deck[3];   // 【注意】必须在检索前取引用：检索会把牌从牌库列表移出（列表长度/下标会变）
            var e = FindEffect(elder.PlayerTemplate, "JC032_1_SearchVampireCovert");
            EffectEngine.Instance.ResolveEffect(e, elder, null);

            Assert.AreEqual(CardZone.AreaZone, vampire.CurrentZone, "检索到的吸血鬼应以暗藏者状态置于本地区");
            Assert.IsTrue(vampire.IsCovert, "以**暗藏者**状态进场（背面）");
            Assert.AreEqual(area, vampire.ZoneDetailId, "落点＝来源所在地区（本地区）");
            Assert.AreEqual(5, game.DeckManager.GetPlayer1DeckCount(),
                "检索取牌后置于场上：牌库少 1 张（其余 5 张洗回）");
        }

        private static PlayerCardData Probe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }
    }
}
