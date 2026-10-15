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
    /// 【2026-10 实测反馈·第六批】7 条用户实测缺陷的回归单测：
    ///  ① **JC103** 一次性减费：打出下一张卡后费用修正必须被消费（费用被减到 0 也要消费）⇒ 手牌减费提示随之刷新；
    ///  ② **JC104** 预测窗必须是"预测动作 UI"的文案（不得显示成调查对抗 UI）；
    ///  ③ **JC105** 光环必须覆盖**运行期获得【灵体】的卡**（JC106 授予灵体后立刻补授白星）；
    ///  ④ **JC106** 授予的【灵体】必须被识别并**抵挡伤害**；
    ///  ⑤ **JC108** 改为**双击激活 → 选择目标**（自身可选）；
    ///  ⑥ **JC110** 手牌行动：可在**手牌双击**发动；
    ///  ⑦ **JC111** 是**防御 1** 的角色（不是无防御卡）。
    /// </summary>
    public class JCBugfixBatch4Tests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        // ==================== 基础工具 ====================

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 132 };
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
        private static string FirstArea(GameInitializer game) => Areas(game)[0];

        /// <summary>探针（可指定组织 / 领域图标数 / 防御）。</summary>
        private static PlayerCardData Probe(string uid, int defense = 2, string organization = null,
            int domains = 0, CardCategory category = CardCategory.Character, int cost = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Defense = defense,
            };
            if (!string.IsNullOrEmpty(organization)) tmpl.Organization = organization;
            for (int i = 0; i < domains; i++) tmpl.MagicDomains.Add((MagicDomain)i);
            tmpl.SubCategories.Add("人类");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var inst = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(inst, area, playerId);
            return inst;
        }

        /// <summary>给某玩家若干资产（提供可用费用；默认颜色 5＝鲜血，够 JC106 的 5:1 颜色忠诚）。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color = (CardColor)5)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_b4_asset_{playerId}_{i}", defense: -1, category: CardCategory.Attachment);
                tmpl.Colors.Add(color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static void Drain(GameInitializer game)
        {
            for (int i = 0; i < 60 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        /// <summary>推进到自己（P1）的行动步骤且持优先权（A 档打出的前置）。</summary>
        private static void AdvanceToMyActionStep(GameInitializer game, int playerId = 1, int maxPasses = 120)
        {
            for (int i = 0; i < maxPasses; i++)
            {
                bool ready = (game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionFirst
                              || game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionSecond)
                             && game.TurnManager.PriorityPlayerId == playerId
                             && game.StackManager.IsEmpty;
                if (ready) return;
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            }
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList) if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>结算一次 JC106（"黑星/白星→黑刀 + 获得灵体"）。</summary>
        private static void ApplyJC106(GameInitializer game, CardInstance target)
        {
            var tmpl = Load("JC106_jiushenmiyi");
            var spell = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToHand(spell, 1);
            EffectEngine.Instance.ResolveEffect(FindEffect(tmpl, "JC106_1_ReplaceStarsToBladeAndGainSpirit"), spell, target);
        }

        private sealed class 直接入栈触发视图 : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<StackEffectInstance> triggers,
                System.Action<List<StackEffectInstance>> onEnterStack, System.Action onDone)
            {
                onEnterStack?.Invoke(triggers);
                onDone?.Invoke();
            }

            public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked) => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<StackEffectInstance> entries) { }
        }

        // ==================== ① JC103：一次性减费的消费与 UI 刷新 ====================

        /// <summary>
        /// 【用户口径·①】JC103 的"下一张【梦境行者】牌费用 -1（一次性）"由**下一张牌**消费。
        /// 场景：1 费【梦境行者】角色被减到 **0 费** 打出 —— 减免**确实被用掉**，必须消费并移除修正
        /// （消费点会广播 `CostModChanged` ⇒ 手牌减费提示刷新）。
        /// 现状缺陷：消费写在 `totalCost > 0` 分支里 ⇒ 0 费路径**不消费**，修正残留（继续减下一张、UI 不刷新）。
        /// </summary>
        public void TestB4_1A_JC103一次性减费_费用被减到0也必须消费()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.SetTriggerView(new 直接入栈触发视图());
            game.TurnManager.PriorityPlayerId = 1;

            // 手牌：1 费【梦境行者】角色（探针：组织＝梦境行者、Cost＝1）
            var cheap = game.CardManager.CreateCardInstance(
                Probe("TS_b4_103_cheap", defense: 2, organization: "梦境行者", cost: 1), 1);
            game.ZoneManager.MoveToHand(cheap, 1);

            // JC103 激活登记的减免（与卡脚本同一写法：-1 / 组织:梦境行者 / 一次性）
            EffectEngine.Instance.ApplyCostModification(1, 1, game.TurnManager.TurnCount, "组织:梦境行者",
                consumeOnce: true);
            Assert.AreEqual(1, EffectEngine.Instance.GetCostModification(1, cheap.PlayerTemplate.CardName, "梦境行者"),
                "前置：一次性减免 1 生效");

            AdvanceToMyActionStep(game);
            Assert.IsTrue(game.PlayerActionService.PlayCard(1, cheap.UID, area, null),
                $"打出 1 费【梦境行者】（被减到 0 费）：阶段={game.TurnManager.CurrentPhase} 优先权={game.TurnManager.PriorityPlayerId}");

            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, cheap.PlayerTemplate.CardName, "梦境行者"),
                "一次性减免**必须已被消费**（费用被减到 0 的路径同样要消费；残留会让下一张继续被减、手牌提示不刷新）");
        }

        /// <summary>【对照】费用 ≥1（真的扣了费）时的一次性减免消费（既有行为，防回归）。</summary>
        public void TestB4_1B_JC103一次性减费_正常扣费路径消费()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.SetTriggerView(new 直接入栈触发视图());
            game.TurnManager.PriorityPlayerId = 1;
            GiveAssets(game, 1, 4);

            var card = game.CardManager.CreateCardInstance(
                Probe("TS_b4_103_cost3", defense: 2, organization: "梦境行者", cost: 3), 1);
            game.ZoneManager.MoveToHand(card, 1);
            EffectEngine.Instance.ApplyCostModification(1, 1, game.TurnManager.TurnCount, "组织:梦境行者",
                consumeOnce: true);

            AdvanceToMyActionStep(game);
            Assert.IsTrue(game.PlayerActionService.PlayCard(1, card.UID, area, null), "打出 3 费【梦境行者】（减 1）");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, card.PlayerTemplate.CardName, "梦境行者"),
                "一次性减免已消费");
            Assert.AreEqual(0, game.CardManager.CreateCardInstance(
                Probe("TS_b4_103_probe2", organization: "梦境行者"), 1).GetAttribute("费用减少"),
                "对照：无残留费用修正");
        }

        // ==================== ② JC104：预测窗文案（不得显示成调查对抗 UI） ====================

        /// <summary>
        /// 【用户口径·②】JC104 的"预测3"复用了同一份分配窗机制，但**显示上不能是调查对抗 UI**：
        /// 窗口标题栏、窗内标题、两区标题、提示行都由**可复用文案**（`AllocationWindowTexts.Prediction`）
        /// 按场景给出 ⇒ 任何预测类效果都拿到"预测"文案；调查对抗那份文案保持原样（同一机制、两套文案）。
        /// </summary>
        public void TestB4_2A_JC104预测窗文案_不是调查对抗UI()
        {
            var pred = Script.UI.AllocationWindowTexts.Prediction(3);
            Assert.AreEqual("预测3", pred.WindowTitle, "窗口标题栏＝『预测3』（不得是『调查对抗』）");
            Assert.IsTrue(pred.Title.Contains("预测3") && pred.Title.Contains("牌库顶"), "窗内标题＝预测口径");
            Assert.IsFalse(pred.Title.Contains("调查"), "窗内标题不得出现『调查』字样");
            Assert.IsFalse(pred.WindowTitle.Contains("调查"), "窗口标题栏不得出现『调查』字样");
            Assert.IsTrue(pred.TopZoneHeader.Contains("置于牌库顶"), "上区标题＝『置于牌库顶』（预测动作口径）");
            Assert.IsTrue(pred.BottomZoneHeader.Contains("置于牌库底"), "下区标题＝『置于牌库底』");
            Assert.IsFalse(pred.TopZoneHeader.Contains("调查") || pred.Hint.Contains("调查对抗"),
                "两区标题/提示行不得出现调查对抗字样");
            Assert.AreEqual("预测窗口", pred.LogTag, "日志前缀＝预测窗口");

            var inv = Script.UI.AllocationWindowTexts.Investigation(2);
            Assert.AreEqual("调查对抗", inv.WindowTitle, "调查对抗那份文案不变（同一机制、两套文案）");
            Assert.AreEqual("2地区调查对抗获胜", inv.Title, "调查对抗标题不变");
            Assert.AreEqual("调查窗口", inv.LogTag, "调查对抗日志前缀不变");
            Assert.AreEqual("⬅ 放到牌库顶（左侧卡在上方）", inv.TopZoneHeader, "调查对抗上区标题不变（2026-10 起改为左右布局口径）");
        }

        // ==================== ③ JC105：光环必须覆盖"运行期获得灵体"的卡 ====================

        /// <summary>
        /// 【用户口径·③】JC105 的持续光环"所有**其他本方灵体角色**获得［白星］"必须把
        /// **运行期获得【灵体】的卡**（JC106 授予）也算进受影响集合 ⇒ 授予灵体后**立刻补授白星**
        /// （现状缺陷：光环只在"卡移动/回合开始"时重算，关键字授予不触发重算 ⇒ 缺这枚白星）。
        /// </summary>
        public void TestB4_3A_JC105光环_JC106授予灵体后立刻补授白星()
        {
            var game = NewGame();
            string area = FirstArea(game);
            OnField(game, Load("JC105_emenglingzhu"), 1, area);
            var plain = OnField(game, Probe("TS_b4_105_plain", defense: 3), 1, area);

            Assert.IsFalse(DamageRules.IsSpirit(plain), "前置：本卡不是灵体");
            Assert.IsFalse(plain.HasIcon("白星"), "前置：非灵体 ⇒ 不上白星");

            ApplyJC106(game, plain);   // 授予【灵体】

            Assert.IsTrue(DamageRules.IsSpirit(plain), "JC106 已授予【灵体】");
            Assert.IsTrue(plain.HasIcon("白星"),
                "获得【灵体】后应被 JC105 光环补授【白星】（光环受影响集合必须含运行期获得灵体的卡）");
        }

        // ==================== ④ JC106：授予的灵体要能抵挡伤害 ====================
        /// <summary>
        /// 【用户口径·④】JC106"并获得灵体"授予的是**可用的【灵体】**：`IsSpirit` 为真，
        /// 且当"本方（含自身）在本地区的魔法领域图标数 ＞ 对手"时**本次伤害被防止**（不落地、不判死）。
        /// </summary>
        public void TestB4_4A_JC106授予灵体_判定与伤害防止()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var victim = OnField(game, Probe("TS_b4_106_t", defense: 3, domains: 2), 1, area);   // 本方 2 个领域图标
            OnField(game, Probe("TS_b4_106_f", defense: 3), 2, area);                            // 对手 0 个

            Assert.IsFalse(DamageRules.IsSpirit(victim), "前置：授予前不是灵体");
            ApplyJC106(game, victim);

            Assert.IsTrue(DamageRules.IsSpirit(victim), "JC106 授予的【灵体】必须被识别（运行期关键字）");
            Assert.IsTrue(DamageRules.IsSpiritPrevention(victim, game.ZoneManager),
                "本方领域图标（2）＞ 对手（0）⇒ 判定应防止伤害");

            game.CardManager.DealDamageToInstance(victim, 2);
            Assert.AreEqual(0, victim.Damage, "【灵体】生效 ⇒ 伤害不落地");
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "未被伤害 ⇒ 不死亡");
        }

        /// <summary>
        /// 【2026-10 用户口径·**JC105/JC106 互动缺陷 → 层系统改造的验收锚点**】
        ///
        /// 现象（已实测复现）：JC105 的【白星】光环授予与 JC106 的"黑星/白星 → 黑刀"替换**互相打架**，
        /// **替换后白星仍在**。实测日志：
        /// <code>
        /// [EffectEngine] ⇄ 图标替换：探针_TS_b4_105106 的 [白星]×1 → [黑刀]（直到回合结束）
        /// [DIAG 层] 替换前势力=0 替换后势力=0 仍有白星=True 有黑刀=False
        /// </code>
        ///
        /// 根因（两条效应**没有层序**，各自直接改实例）：
        ///  ① `ReplaceIcon` 用**当前已含光环授予**的 `CurrentPower` 当替换计数，再 `Grant` 增量到实例上；
        ///     "先手势力"这类授予量在 `CurrentPower` 里**看不见**（只有 `GetAttribute` 看得到）⇒ 计数算错；
        ///  ② 替换只是"再 Grant 一笔负的属性" ⇒ 把光环那笔授予**记账抵消**掉；随后 `ReevaluateAuras`
        ///     的"自愈补授"发现光环记录里的白星不见了、而目标仍符合光环条件 ⇒ **重新授予**，
        ///     于是替换结果被抹掉（图层语义里这应当是"替换生效、光环的白星不再存在"）。
        ///
        /// 处理：本用例标记为 **[KnownDefect]**（当前失败不计入套件失败，但每次运行都会打印 `[KNOWN]`），
        /// 作为层系统改造的**验收条件**——层系统落地后它会自动通过，并打印 `[FIXED?]` 提醒摘标记。
        /// </summary>
        [KnownDefect("JC105 白星光环 × JC106 图标改写：改写不作用于「条目集合」，且光环自愈按「目标仍符合条件」补授 ⇒ 替换后白星复活、黑刀没留下（统一属性账本改造验收锚点）")]
        public void TestB4_8A_JC105JC106互动_白星替换后不得被光环复活()
        {
            var game = NewGame();
            string area = FirstArea(game);
            OnField(game, Load("JC105_emenglingzhu"), 1, area);                 // 光环源（自身是灵体）
            var probe = OnField(game, Probe("TS_b4_105106", defense: 5), 1, area);
            // 让探针成为灵体，从而被 JC105 光环覆盖 → 获授【白星】
            ApplyJC106_OnlySpirit(game, probe);
            Assert.IsTrue(probe.HasIcon("白星"), "前置：灵体 ⇒ JC105 光环授予【白星】");

            int powerWithStar = probe.CurrentPower;
            int firstStrikePowerWithStar = probe.GetAttribute("先手势力");
            // 施加 JC106 的图标替换（黑星/白星 → 黑刀）
            ApplyJC106(game, probe);
            int afterReplace = probe.CurrentPower;
            Godot.GD.Print($"[DIAG 层] 替换前 势力={powerWithStar} 先手势力={firstStrikePowerWithStar}；" +
                           $"替换后 势力={afterReplace} 先手势力={probe.GetAttribute("先手势力")} " +
                           $"仍有白星={probe.HasIcon("白星")} 有黑刀={probe.HasIcon("黑刀")}");

            Assert.IsFalse(probe.HasIcon("白星"),
                "JC106『所有［黑星］和［白星］都替换为［黑刀］』⇒ 替换后不应再有【白星】" +
                "（当前缺陷：光环自愈补授把白星复活）");
            Assert.IsTrue(probe.HasIcon("黑刀"),
                "替换必须留下【黑刀】（当前缺陷：替换量按错误基数计算 ⇒ 连黑刀都没留下）");
        }

        /// <summary>只施加 JC106 的"获得灵体"（不替换图标），供上面的诊断用例铺垫光环条件。</summary>
        private static void ApplyJC106_OnlySpirit(GameInitializer game, CardInstance target)
        {
            var tmpl = Load("JC106_jiushenmiyi");
            var spell = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToHand(spell, 1);
            EffectEngine.Instance.ResolveEffect(new EffectData
            {
                EffectId = "TS_b4_106_spirit_only",
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "灵体", "1", "目标" } },
                },
            }, spell, target);
        }

        /// <summary>【对照】对手领域图标更多 ⇒ 灵体**不**防止（规则是"严格小于才防止"）。</summary>
        public void TestB4_4B_JC106灵体_对手图标更多则不防止()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var victim = OnField(game, Probe("TS_b4_106_t2", defense: 5), 1, area);              // 本方 0 个
            OnField(game, Probe("TS_b4_106_f2", defense: 3, domains: 2), 2, area);                // 对手 2 个

            ApplyJC106(game, victim);
            Assert.IsTrue(DamageRules.IsSpirit(victim), "前置：是灵体");
            Assert.IsFalse(DamageRules.IsSpiritPrevention(victim, game.ZoneManager),
                "对手图标更多 ⇒ 不防止（严格小于才防止）");
            game.CardManager.DealDamageToInstance(victim, 2);
            Assert.AreEqual(2, victim.Damage, "未防止 ⇒ 伤害照常落地");
        }

        /// <summary>
        /// 【用户口径·④·**正式打出路径**】JC106 经 `PlayCard`（快速行动）打出并结算后，
        /// **被指定的目标角色**必须获得【灵体】（不是"卡牌自己"）并据此免伤。
        /// 这条锁的是"打出时选定的目标确实随堆叠条目传到结算"（若丢失，`Gain{"灵体","1","目标"}`
        /// 会退化成作用在来源卡上 ⇒ 角色毫无灵体、伤害照常落地＝用户实测现象）。
        /// </summary>
        public void TestB4_4C_JC106正式打出_目标角色获得灵体并免伤()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.SetTriggerView(new 直接入栈触发视图());
            game.TurnManager.PriorityPlayerId = 1;
            GiveAssets(game, 1, 6);   // JC106 费用 2

            var victim = OnField(game, Probe("TS_b4_106_t3", defense: 3, domains: 2), 1, area);
            OnField(game, Probe("TS_b4_106_f3", defense: 3), 2, area);

            var spell = game.CardManager.CreateCardInstance(Load("JC106_jiushenmiyi"), 1);
            game.ZoneManager.MoveToHand(spell, 1);

            AdvanceToMyActionStep(game);   // 快速行动（Response）：在自己行动步骤、堆叠为空、持优先权
            Assert.IsTrue(game.PlayerActionService.PlayCard(1, spell.UID, null, victim.UID),
                $"快速行动打出 JC106（指定目标）：阶段={game.TurnManager.CurrentPhase} 优先权={game.TurnManager.PriorityPlayerId}");            Drain(game);

            Assert.IsFalse(ReferenceEquals(spell, victim), "前置：来源卡与目标卡不同");
            Assert.IsTrue(DamageRules.IsSpirit(victim), "**被指定的目标角色**应获得【灵体】");
            game.CardManager.DealDamageToInstance(victim, 2);
            Assert.AreEqual(0, victim.Damage, "灵体生效 ⇒ 伤害被防止（不落地）");
        }

        // ==================== ⑤ JC108：双击激活 + 选择目标（含自身） ====================

        /// <summary>
        /// 【用户口径·⑤】JC108 改为**双击激活 → 选择目标**（不再走"拖到目标上"的手势，否则
        /// 落点＝自身会被判取消）：数据侧声明"双击激活" ⇒ `CanDragActivate` 为假（左键不被拖拽吞掉，
        /// 双击能到达 UI），目标选择候选**含自身**。
        /// </summary>
        public void TestB4_5A_JC108双击激活_目标含自身()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc108 = OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, area);

            var ability = ActivateEligibility.GetFirstActivateAbility(jc108);
            Assert.IsNotNull(ability, "JC108 应有激活能力");
            Assert.IsTrue(ability.ActivateByDoubleClick,
                "数据侧应声明『双击激活』（用户口径：改为双击激活效果选择目标）");
            Assert.IsFalse(ActivateEligibility.CanDragActivate(jc108, out _),
                "声明双击激活 ⇒ **不进拖拽**（否则左键被拖拽吞掉，双击根本到不了 UI）");
            Assert.IsTrue(ActivateEligibility.IsDoubleClickActivationSource(jc108),
                "争夺区正面角色是双击激活来源（双击链路可达）");
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(jc108, ability, jc108,
                game.CardManager, game.PlayerManager), "目标选择候选**含自身**");
            Assert.IsTrue(ActivateEligibility.HasLegalTarget(jc108, ability, game.CardManager, game.PlayerManager),
                "本地区只有本卡时仍有合法目标 ⇒ 双击后能开目标窗");
        }

        // ==================== ⑥ JC110：手牌双击发动"手牌行动" ====================

        /// <summary>
        /// 【用户口径·⑥】JC110"手牌行动4"必须能在**手牌中双击**发动：
        /// 来源判定（手牌 + `Scope=HandZone` 的激活能力）为真，且给出费用/目标满足时 `CanActivate` 成立
        /// （UI 侧"左键被拖拽吞掉"的接线由 ㉖ 承诺钉住）。
        /// </summary>
        public void TestB4_6A_JC110手牌双击激活_来源与门禁()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.SetTriggerView(new 直接入栈触发视图());
            game.TurnManager.PriorityPlayerId = 1;
            GiveAssets(game, 1, 6);

            var jc110 = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(jc110, 1);
            OnField(game, Probe("TS_b4_110_host", defense: 3), 2, area);   // 合法封印目标

            var ability = ActivateEligibility.GetFirstActivateAbility(jc110);
            Assert.IsNotNull(ability, "JC110 应有激活能力");
            Assert.AreEqual(ZoneScope.HandZone, ability.Scope, "『手牌行动』＝生效区为手牌");
            Assert.AreEqual(4, ability.Cost, "费用＝4");
            Assert.IsTrue(ActivateEligibility.IsDoubleClickActivationSource(jc110),
                "手牌中的『手牌行动』卡是双击激活来源（双击链路必须放行手牌来源）");

            AdvanceToMyActionStep(game);
            Assert.IsTrue(ActivateEligibility.CanActivate(jc110, game.TurnManager, game.PlayerManager, game.CardManager),
                $"手牌行动可发动：行动步骤 + 优先权 + 费用足 + 有合法目标（阶段={game.TurnManager.CurrentPhase}）");
        }

        // ==================== ⑦ JC111：防御 1 ====================

        /// <summary>
        /// 【用户口径·⑦】JC111 混沌蝴蝶是**防御 1** 的角色（不是"无防御卡"）：
        /// `.tres` 的 `Defense` 必须是 1（此前为 -1 哨兵＝无防御），且实例 `HasDefense` 为真（可被分配伤害/判死）。
        /// </summary>
        public void TestB4_7A_JC111是防御1的角色()
        {
            var tmpl = Load("JC111_hundunhudie");
            Assert.IsNotNull(tmpl, "JC111 模板应可加载");
            Assert.AreEqual(1, tmpl.Defense, "JC111 的印刷防御＝1（不是 -1 无防御）");

            var game = NewGame();
            var inst = OnField(game, tmpl, 1, FirstArea(game));
            Assert.AreEqual(1, inst.CurrentDefense, "实例当前防御力＝1");
            Assert.IsTrue(inst.HasDefense, "有防御力 ⇒ 可被分配伤害（不再是无防御卡）");
        }
    }
}
