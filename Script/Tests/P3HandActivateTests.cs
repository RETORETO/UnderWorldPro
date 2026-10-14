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
    /// 【P3·K27「手牌行动」：在手牌中发动激活能力 → JC110 闲人的绿地①】
    /// 卡文：灵体 手牌行动4：将闲人的绿地封印在目标角色上。
    ///
    /// 交付：
    ///  · **生效区声明即许可**：激活类效果声明 `Scope = ZoneScope.HandZone` 即可在**手牌**中发动
    ///    （无需新数据标志）；`EffectEngine.ActivateAbility` 新增**来源生效区门禁**（默认角色=争夺区、
    ///    附属=附属区；手牌行动＝手牌）——顺带堵住"来源不在生效区也能激活"的漏洞；
    ///  · `ActivateEligibility`（`CanActivate` / `GetEligibleAbilities` 候选扫描）把手牌来源纳入；
    ///  · View：`CardUI` 双击"手牌中的手牌行动卡"即请求激活（该分支 `AcceptEvent` 先于拖拽，普通手牌不受影响）；
    ///  · 费用/行动步骤/目标规则照常（卡文"手牌**行动**4"）。
    /// </summary>
    public class P3HandActivateTests : TestRunner
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
                var tmpl = Probe($"TS_ha_asset_{playerId}_{i}", CardCategory.Attachment);
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

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        // ==================== ① 结构 ====================

        public void TestHandActivate_JC110结构()
        {
            var tmpl = Load("JC110_xianrendelvdi");
            Assert.IsNotNull(tmpl, "JC110 模板应可加载");
            var e = FindEffect(tmpl, "JC110_1_HandActionSealSelfOnCharacter");
            Assert.IsNotNull(e, "JC110 应有『手牌行动4：把自身封印在目标角色上』");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活");
            Assert.AreEqual(ZoneScope.HandZone, e.Scope, "**生效区＝手牌**（手牌行动的单一表达）");
            Assert.AreEqual("行动", e.ActivateWindow, "窗口＝行动");
            Assert.AreEqual(4, e.Cost, "卡文『行动4』→ 费用 4");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.IsTrue(e.Target.NeedsSelection, "需要选目标");
            Assert.AreEqual(ActionVerb.Seal, e.Actions[0].Verb, "动作＝封印");
            Assert.AreEqual("自身", e.Actions[0].Args[0], "被封印者＝自身");
            Assert.AreEqual("目标", e.Actions[0].Args[1], "宿主＝目标角色");
        }

        // ==================== ② 生效区门禁：手牌可发动、离手不可发动 ====================

        public void TestHandActivate_手牌中可发动离手被拒()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)5);       // JC110 颜色 5 + 费用 4
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_victim"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            var e = FindEffect(unit.PlayerTemplate, "JC110_1_HandActionSealSelfOnCharacter");
            Assert.IsTrue(ActivateEligibility.CanActivate(unit, game.TurnManager, game.PlayerManager, game.CardManager),
                "手牌中的『手牌行动』卡可发动（候选/高亮纳入手牌）");

            // 把卡移到场上（不再是手牌）→ 生效区不符 → 拒绝
            game.ZoneManager.MoveToArea(unit, area, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            EffectEngine.Instance.ActivateAbility(unit, e, victim);
            Assert.IsTrue(game.StackManager.IsEmpty, "离手后（生效区＝手牌）拒绝发动");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "被拒 → 不扣费");
            Assert.IsFalse(victim.IsSealed, "未发生封印");
        }

        // ==================== ③ 端到端：从手牌发动 → 付费 → 自身封印在目标角色上 ====================

        public void TestHandActivate_从手牌发动并封印自身()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)5);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_victim2"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            var e = FindEffect(unit.PlayerTemplate, "JC110_1_HandActionSealSelfOnCharacter");
            EffectEngine.Instance.ActivateAbility(unit, e, victim);

            Assert.IsFalse(game.StackManager.IsEmpty, "手牌行动 → 入堆叠");
            Assert.AreEqual(manaBefore - 4, game.PlayerManager.GetAvailableMana(1), "支付 4 费");
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.SealedZone, unit.CurrentZone, "本卡已进入**封印区**");
            Assert.IsTrue(ReferenceEquals(victim, unit.SealedBy), "封印宿主＝目标角色");
            Assert.IsTrue(victim.SealedCards.Contains(unit), "目标角色记录被封印的本卡");
            Assert.IsTrue(victim.IsSealed, "目标角色处于『被封印』状态");
        }

        // ==================== ④ 费用不足 → 拒绝（门禁顺序：付费前） ====================

        public void TestHandActivate_费用不足被拒()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_victim3"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            var e = FindEffect(unit.PlayerTemplate, "JC110_1_HandActionSealSelfOnCharacter");
            EffectEngine.Instance.ActivateAbility(unit, e, victim);

            Assert.IsTrue(game.StackManager.IsEmpty, "无资产 → 付不起 4 费 → 拒绝");
            Assert.AreEqual(CardZone.Hand, unit.CurrentZone, "卡仍在手牌");
        }

        // ==================== ⑤ 候选扫描：手牌来源进入可发动列表 ====================

        public void TestHandActivate_候选含手牌来源()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)5);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var plain = game.CardManager.CreateCardInstance(Probe("TS_ha_plain"), 1);
            game.ZoneManager.MoveToHand(plain, 1);
            // 场上需有**合法目标**（否则门禁 4「需选目标时场上须有合法目标」会让能力不进候选）
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_victim4"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            var list = ActivateEligibility.GetEligibleAbilities(1, game.TurnManager, game.PlayerManager, game.CardManager);
            bool hasUnit = false, hasPlain = false;
            foreach (var (src, ab) in list)
            {
                if (src.UID == unit.UID && ab.EffectId == "JC110_1_HandActionSealSelfOnCharacter") hasUnit = true;
                if (src.UID == plain.UID) hasPlain = true;
            }
            Assert.IsTrue(hasUnit, "手牌中的『手牌行动』进入可发动候选");
            Assert.IsFalse(hasPlain, "普通手牌卡（无手牌生效区能力）不在候选里");
        }

        // ==================== ⑥ 【2026-10 用户口径】拖到合法目标上＝发动（可拖拽付费打出） ====================

        /// <summary>
        /// 用户原文："JC110 **无法通过拖拽正面付费打出**"。
        /// 落点判定（单一实现 `ActivateEligibility.CanHandActivateOn`）必须成立：手牌中的『手牌行动』卡
        /// 拖到**该能力声明的合法角色目标**上松手 ⇒ 发动（与"双击 → 取目标"同一出口）。
        /// 这是"拖拽链路也能发动"的规则侧前提；View 侧接线见 `CardDragController.TryHandActivateDrop`。
        /// </summary>
        public void TestHandActivate_可拖到合法目标上发动()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)5);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_drop_target"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            var e = ActivateEligibility.GetHandScopedActivateAbility(unit);
            Assert.IsNotNull(e, "手牌中的 JC110 应取到『手牌行动』能力（供落点判定用）");
            Assert.AreEqual("JC110_1_HandActionSealSelfOnCharacter", e.EffectId, "取到的正是手牌行动那条");
            Assert.IsTrue(ActivateEligibility.HasHandScopedActivation(unit),
                "HasHandScopedActivation（View/协调器共用判据）应为真");

            Assert.IsTrue(ActivateEligibility.CanHandActivateOn(unit, victim,
                    game.TurnManager, game.PlayerManager, game.CardManager),
                "拖到**合法角色目标**上应判『可发动』（用户口径：拖拽正面付费打出）");
        }

        /// <summary>落点不是合法目标（敌方角色之外的空白 / 无目标）时**不得**误发动，交回普通流程。</summary>
        public void TestHandActivate_落点非法则不发动()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)5);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_drop_ok"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            Assert.IsFalse(ActivateEligibility.CanHandActivateOn(unit, null,
                game.TurnManager, game.PlayerManager, game.CardManager), "落点为空 ⇒ 不发动");
            Assert.IsFalse(ActivateEligibility.CanHandActivateOn(unit, unit,
                game.TurnManager, game.PlayerManager, game.CardManager),
                "把自身当作宿主＝非法（卡文『封印在**目标角色**上』，不适用自封）");

            // 费用不足时同样不得发动（门禁复用 CanActivate）
            var poor = new GameInitializer { DebugSeed = 7 };
            poor.InitializeAll();
            poor.StartMatch();
            poor.PlayerActionService.SetupInitialAreas(1);
            var poorUnit = poor.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            poor.ZoneManager.MoveToHand(poorUnit, 1);
            var poorVictim = poor.CardManager.CreateCardInstance(Probe("TS_ha_drop_poor"), 2);
            poor.ZoneManager.MoveToArea(poorVictim, Areas(poor)[0], 2);
            poor.TurnManager.PriorityPlayerId = 1;
            poor.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            Assert.IsFalse(ActivateEligibility.CanHandActivateOn(poorUnit, poorVictim,
                    poor.TurnManager, poor.PlayerManager, poor.CardManager),
                "无资产付不起 4 费 ⇒ 不算可发动（回落到普通流程/归位）");
        }

        /// <summary>
        /// 【2026-10 用户口径·JC110 端到端】"拖拽链路"用的调用的形态（`ActivateAbility(卡, 能力, 目标)`）
        /// 必须与双击链路**完全同效**：付费 → 入堆叠 → 结算 ⇒ 本卡进入封印区、挂在目标角色上。
        /// （单机 `CardDragController.TryHandActivateDrop` 走的正是这一句。）
        /// </summary>
        public void TestHandActivate_拖拽发动与双击同效_封印在目标上()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)5);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_ha_drag_host"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);

            var ability = ActivateEligibility.GetHandScopedActivateAbility(unit);
            Assert.IsNotNull(ability, "手牌中的 JC110 应取到『手牌行动』能力");
            Assert.IsTrue(ActivateEligibility.CanHandActivateOn(unit, host,
                    game.TurnManager, game.PlayerManager, game.CardManager),
                "前置：拖到该宿主上应判可发动");

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            // 与 CardDragController.TryHandActivateDrop 的发动调用完全一致
            EffectEngine.Instance.ActivateAbility(unit, ability, host);

            Assert.IsFalse(game.StackManager.IsEmpty, "拖拽发动 ⇒ 入堆叠");
            Assert.AreEqual(manaBefore - 4, game.PlayerManager.GetAvailableMana(1), "支付 4 费");

            ResolveStackOnce(game);
            Assert.AreEqual(CardZone.SealedZone, unit.CurrentZone, "本卡已进入封印区");
            Assert.IsTrue(ReferenceEquals(host, unit.SealedBy), "封印宿主＝拖拽落点角色");
            Assert.IsTrue(host.SealedCards.Contains(unit), "宿主记录被封印的本卡");
        }

        // ==================== ⑦ 【2026-10 用户口径】取目标可取消（源码接线锁） ====================

        // 需要断言的源码片段（含成对双引号，故用 char 拼接避免在 C# 字符串里再转义引号；
        // 用静态构造集中初始化，避免"静态字段初始化顺序"导致 Q 尚未赋值）
        private static readonly string Q;
        private static readonly string CancelableWire;
        private static readonly string CancelExtraLabelWire;
        private static readonly string NoHintWindowWire;
        private static readonly string CancelFieldWire;
        private static readonly string CancelInvokeWire;

        static P3HandActivateTests()
        {
            Q = ((char)34).ToString();
            CancelableWire = "OpenAttachmentActivateChoice(hit, cancelable: true);";
            CancelExtraLabelWire = "extraLabel: allowCancel ? " + Q + "取消（不发动）" + Q + " : null,";
            NoHintWindowWire = "hintWindow: false);";
            CancelFieldWire = "_cancelActivateTargetSelection = InvokeExtraExit;";
            CancelInvokeWire = "void InvokeExtraExit()";
        }

        private static string ReadProjectText(string rel)
        {
            foreach (var basePath in new[] { "res://", "res://../" })
            {
                var f = Godot.FileAccess.Open(basePath + rel, Godot.FileAccess.ModeFlags.Read);
                if (f != null) { var t = f.GetAsText(); f.Close(); return t; }
            }
            return null;
        }

        /// <summary>
        /// 用户原文："同时需要**双击激活手牌行动效果可取消**"。
        /// 行为侧（"取目标时点了取消 ⇒ 不入堆叠、不扣费"）依赖 View 的窗口/右键手势，headless 无法驱动；
        /// 故这里用**源码接线锁**钉住三处消费点，防止将来"改了取目标签名但漏传取消出口"这类静默回退：
        ///  ① 手牌双击链路传入 `cancelable: true`；
        ///  ② 协调器的取消出口字段（窗口按钮与场上右键**共用**同一份投递）；
        ///  ③ 手牌行动的取目标走"不弹窗 + 可取消"的 `RequestActivateTargetSelection` 重载。
        /// </summary>
        public void TestHandActivate_手牌双击激活可取消_源码接线()
        {
            var arena = ReadProjectText("Script/UI/Arena.cs");
            Assert.IsNotNull(arena, "Arena.cs 源码应可读（请在项目目录内运行测试）");
            Assert.IsTrue(arena.Contains(CancelableWire),
                "手牌双击链路必须传 `cancelable: true`（用户口径：双击激活手牌行动效果可取消）");
            Assert.IsTrue(arena.Contains(CancelExtraLabelWire),
                "取目标必须把手牌来源的取消出口交给协调器（allowCancel ⇒ 场上右键=取消）");
            Assert.IsTrue(arena.Contains(NoHintWindowWire),
                "激活取卡目标不再弹提示窗（用户口径：直接选择场上的高亮卡）");

            var coord = ReadProjectText("Script/UI/Interaction/TriggerInteractionCoordinator.cs");
            Assert.IsNotNull(coord, "TriggerInteractionCoordinator.cs 源码应可读");
            Assert.IsTrue(coord.Contains(CancelFieldWire),
                "取消出口必须挂在协调器上（场上右键走同一份投递）");
            Assert.IsTrue(coord.Contains(CancelInvokeWire),
                "取消出口投递必须是单一实现（窗口按钮 / 场上右键共用）");
        }
    }
}
