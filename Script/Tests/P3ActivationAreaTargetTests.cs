using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K15 扩展：**激活能力的"目标地区"** → JC117 红眼航班 / JC033 家族传承①】
    /// JC117：快速行动1，横置：将**目标本方角色**移动到**目标地区**。该能力只能在行动阶段发动。
    ///        FAQ：必须进入**另一个**地区（不能原地移动）。
    /// JC033①：行动1，牺牲家族传承：从牌库找一张吸血鬼角色牌，**展示**后以**暗藏者**状态置于**目标地区**，然后洗牌。
    ///
    /// 交付：`EffectData.NeedsAreaTarget`（＋`AreaTargetExcludesCardArea`）→ 激活时额外选定一个地区；
    ///  · 门禁在 **2.7 步**（先于付费/横置）：无合法地区 → 拒绝；未指定 → 确定性兜底第一个合法地区；
    ///  · 入堆叠时锁定 → `StackEntry.TargetAreaId`（含地区箭头）→ 结算时作为 `ResolveEffect` 的 `targetAreaId`；
    ///  · 候选 `ActivateEligibility.GetAreaTargetCandidates`（选定卡目标后按"禁原地"过滤）；
    ///  · `ResolveSearchArea` 改为**显式目标地区优先**（JC033① 的落点就是它）。
  /// </summary>
    public class P3ActivationAreaTargetTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 3)
        {
            var game = new GameInitializer { DebugSeed = 1399 };
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

        private static PlayerCardData Probe(string uid, string subCategory = "人类", int power = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 5, Power = power,
            };
            if (!string.IsNullOrEmpty(subCategory)) tmpl.SubCategories.Add(subCategory);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        /// <summary>把附属卡放进**附属区**（本套件只验证"激活能力的地区目标"本身，故不特意挂到秘社实例上）。</summary>
        private static void AttachToCouncil(GameInitializer game, CardInstance attachment, int playerId)
        {
            attachment.IsAttached = true;
            game.ZoneManager.MoveToAttachedZone(attachment, Areas(game)[0], playerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }

        /// <summary>把一张牌放到指定玩家牌库顶（沿用 JC051Tests 的写法）。</summary>
        private static CardInstance PutOnDeckTop(GameInitializer game, PlayerCardData tmpl, int ownerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, ownerId);
            card.CurrentZone = CardZone.Deck;
            game.DeckManager.GetDeckPlayer1().Insert(0, card);
            return card;
        }

        /// <summary>把 P1 摆到"自己的行动步骤 + 有优先权 + 堆叠为空"（JC117/JC033 的『行动』窗口），并给足费用。</summary>
        private static void PrepareActionWindow(GameInitializer game)
        {
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.FirstPlayerId = 1;              // ③ 仅限自己的行动步骤（先手→ActionFirst）
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            for (int i = 0; i < 3; i++)                       // 提供无色费用（未横置资产数）
            {
                var asset = game.CardManager.CreateCardInstance(Probe($"TS_at_asset_{i}"), 1);
                game.ZoneManager.MoveToAsset(asset, 1);
            }
        }

        private static void ResolveStack(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
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

        public void TestAreaTarget_两张卡结构()
        {
            var jc117 = Load("JC117_hongyanhangban");
            var e117 = FindEffect(jc117, "JC117_1_MoveFriendlyToTargetArea");
            Assert.IsNotNull(e117, "JC117 应有『把本方角色移到目标地区』能力");
            Assert.AreEqual(EffectTiming.AttachmentActivated, e117.Timing, "附属激活能力");
            Assert.IsTrue(e117.NeedsAreaTarget, "需要选定目标地区");
            Assert.IsTrue(e117.AreaTargetExcludesCardArea, "FAQ：不能原地移动");
            Assert.AreEqual("行动", e117.ActivateWindow, "只能在行动阶段发动");
            Assert.AreEqual(1, e117.Cost, "快速行动1");
            Assert.AreEqual("横置本角色", e117.ActivateCondition, "横置此附属");
            Assert.AreEqual(TargetRestriction.Friendly, e117.Target.Restrictions[0], "目标＝本方角色");
            Assert.AreEqual(ActionVerb.Move, e117.Actions[0].Verb, "动作＝移动（落点＝targetAreaId）");

            var jc033 = Load("JC033_jiazuchuancheng");
            var e033 = FindEffect(jc033, "JC033_B_SacrificeSearchVampireToTargetArea");
            Assert.IsNotNull(e033, "JC033 应有『牺牲自身检索吸血鬼角色牌置于目标地区』能力");
            Assert.IsTrue(e033.NeedsAreaTarget, "需要选定目标地区");
            Assert.AreEqual("牺牲本角色", e033.ActivateCondition, "代价＝牺牲家族传承自身");
            Assert.AreEqual(ActionVerb.Search, e033.Actions[0].Verb, "动作＝检索");
            Assert.AreEqual("暗藏进场", e033.Actions[0].Args[1], "以暗藏者状态进场");
            Assert.AreEqual("展示", e033.Actions[0].Args[2], "并展示该牌");
            Assert.AreEqual("子类别:吸血鬼;类别:角色", e033.Actions[0].Args[0], "筛选＝吸血鬼角色牌");
        }

        // ==================== ② 候选地区：排除卡目标当前所在地区（禁原地） ====================

        public void TestAreaTarget_候选排除原地()
        {
            var game = NewGame();
            var areas = Areas(game);
            var plane = game.CardManager.CreateCardInstance(Load("JC117_hongyanhangban"), 1);
            AttachToCouncil(game, plane, 1);
            var ability = FindEffect(plane.PlayerTemplate, "JC117_1_MoveFriendlyToTargetArea");
            var mover = OnField(game, Probe("TS_at_mover"), 1, areas[0]);

            var candidates = ActivateEligibility.GetAreaTargetCandidates(plane, ability, mover);

            Assert.IsFalse(candidates.Contains(areas[0]), "排除卡目标当前所在地区（FAQ：不能原地移动）");
            Assert.IsTrue(candidates.Contains(areas[1]), "其它地区可作目标");
            Assert.IsTrue(candidates.Contains(areas[2]), "其它地区可作目标");
        }

        // ==================== ③ 端到端 JC117：移动到选定地区（且不能原地） ====================

        public void TestAreaTarget_JC117移动到目标地区()
        {
            var game = NewGame();
            var areas = Areas(game);
            var plane = game.CardManager.CreateCardInstance(Load("JC117_hongyanhangban"), 1);
            AttachToCouncil(game, plane, 1);
            var ability = FindEffect(plane.PlayerTemplate, "JC117_1_MoveFriendlyToTargetArea");
            var mover = OnField(game, Probe("TS_at_mover2"), 1, areas[0]);
            PrepareActionWindow(game);

            EffectEngine.Instance.ActivateAbility(plane, ability, mover, -1, null, areas[1]);

            Assert.IsTrue(plane.IsTapped, "代价：横置本附属");
            ResolveStack(game);

            Assert.AreEqual(areas[1], mover.ZoneDetailId, "角色被移动到选定的目标地区");

            // 非法：原地移动 → 拒绝
            mover.SetTapped(false);
            EffectEngine.Instance.ActivateAbility(plane, ability, mover, -1, null, areas[1]);   // 目标地区＝其当前所在地
            Assert.AreEqual(areas[1], mover.ZoneDetailId, "指定『原地』地区被拒绝（能力未生效）");
        }

        // ==================== ④ 端到端 JC033①：牺牲自身 → 检索吸血鬼角色牌 → 暗藏进场到目标地区 ====================

        public void TestAreaTarget_JC033检索暗藏进场到目标地区()
        {
            var game = NewGame();
            var areas = Areas(game);
            var relic = game.CardManager.CreateCardInstance(Load("JC033_jiazuchuancheng"), 1);
            AttachToCouncil(game, relic, 1);
            var ability = FindEffect(relic.PlayerTemplate, "JC033_B_SacrificeSearchVampireToTargetArea");
            // 牌库里放一张吸血鬼角色牌
            var vampire = PutOnDeckTop(game, Probe("TS_at_vampire", "吸血鬼"), 1);
            PrepareActionWindow(game);

            int deckBefore = game.TurnManager.GetDeckOf(1).Count;
            EffectEngine.Instance.ActivateAbility(relic, ability, null, -1, null, areas[2]);

            Assert.AreEqual(CardZone.Graveyard, relic.CurrentZone, "代价：牺牲家族传承（自身进墓地）");
            ResolveStack(game);

            Assert.AreEqual(areas[2], vampire.ZoneDetailId, "检索到的吸血鬼角色牌被置于**选定的目标地区**");
            Assert.IsTrue(vampire.IsCovert, "以**暗藏者**状态进场");
            Assert.AreEqual(deckBefore - 1, game.TurnManager.GetDeckOf(1).Count, "该牌已从牌库取出（随后照常洗牌）");
        }

        // ==================== ⑤ 无合法地区 → 拒绝（先于付费/横置） ====================

        public void TestAreaTarget_无合法地区时拒绝()
        {
            var game = NewGame(areas: 1);   // 只有一个地区 → 禁原地导致"无合法目标地区"
            var areas = Areas(game);
            var plane = game.CardManager.CreateCardInstance(Load("JC117_hongyanhangban"), 1);
            AttachToCouncil(game, plane, 1);
            var ability = FindEffect(plane.PlayerTemplate, "JC117_1_MoveFriendlyToTargetArea");
            var mover = OnField(game, Probe("TS_at_mover3"), 1, areas[0]);
            PrepareActionWindow(game);

            Assert.IsFalse(ActivateEligibility.CanActivate(plane, game.TurnManager, game.PlayerManager, game.CardManager),
                "只有 1 个地区且禁原地 → 该能力不可发动（UI 置灰）");

            EffectEngine.Instance.ActivateAbility(plane, ability, mover, -1, null, areas[0]);
            Assert.IsFalse(plane.IsTapped, "被拒绝的发动没有付出横置代价");
            Assert.AreEqual(areas[0], mover.ZoneDetailId, "角色未被移动");
        }
    }
}
