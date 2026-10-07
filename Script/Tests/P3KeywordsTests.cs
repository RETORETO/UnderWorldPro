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
    /// 【P3·词条规则第一批】按用户 2026-09 给出的规则文本实现并锁定：
    ///  · **隐匿**：该角色不能从手牌付费正面打出到争夺区（仅限制这一种方式）；
    ///  · **屏障**：不能被敌方卡牌或能力指定为目标（先被指定、后获屏障 → 结算视为不合法）；
    ///  · **撤回**：地区被赢取时，拥有该能力的卡回 OWNER 手牌而非牌库底；
    ///  · **非资产**：不能被用于【建立资产】基础行动；
    ///  · **XX标志X**：强制进场触发，给触发本效果的卡本身加 X 个 XX 标志；
    ///  · **杀伤 X**：参与战斗对抗并获胜时，该次可分配伤害 +X（多个累加）。
    ///
    /// 本批**未实现**的：领袖 / 灵体 / 声望 / 预测X / 遏制X / 形态转换 / 构筑唯一性 —— 口径待与用户确认。
    /// </summary>
    public class P3KeywordsTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character,
            string subCategory = null, string organization = "")
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Organization = organization,
            };
            if (subCategory != null) tmpl.SubCategories.Add(subCategory);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 71 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            game.TurnManager.SetTriggerView(new 放弃触发视图());
            return game;
        }

        private sealed class 放弃触发视图 : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                System.Collections.Generic.List<StackEffectInstance> triggers,
                System.Action<System.Collections.Generic.List<StackEffectInstance>> onEnterStack,
                System.Action onDone)
                => onDone?.Invoke();

            public void RequestTargetSelection(StackEffectInstance trigger,
                System.Collections.Generic.List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

            public void RemoveOrderEntries(System.Collections.Generic.List<StackEffectInstance> entries) { }
        }

        /// <summary>把收集到的触发**全部入堆叠**的视图（用于"触发真的发生了吗/结算后状态"类断言）。</summary>
        private sealed class 推入触发视图 : Script.Interfaces.ITriggerView
        {
            public readonly System.Collections.Generic.List<StackEffectInstance> Collected
                = new System.Collections.Generic.List<StackEffectInstance>();

            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                System.Collections.Generic.List<StackEffectInstance> triggers,
                System.Action<System.Collections.Generic.List<StackEffectInstance>> onEnterStack,
                System.Action onDone)
            {
                if (triggers != null) Collected.AddRange(triggers);
                onEnterStack?.Invoke(triggers);
                onDone?.Invoke();
            }

            public void RequestTargetSelection(StackEffectInstance trigger,
                System.Collections.Generic.List<string> validTargetUids, System.Action<string> onPicked)
                => onPicked?.Invoke(null);

            public void RemoveOrderEntries(System.Collections.Generic.List<StackEffectInstance> entries) { }
        }

        /// <summary>推进一次堆叠结算（双方连续让过）——与其它套件同款。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int controllerId, string areaUid)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, controllerId);
            game.ZoneManager.MoveToArea(card, areaUid, controllerId);
            return card;
        }

        private static string FirstArea(GameInitializer game) => game.AreaManager.GetOrderedAreaUids()[0];

        // ==================== 隐匿 ====================

        /// <summary>JC097 阿斯旺：卡面固有【隐匿】→ 不能从手牌付费正面打出到争夺区。</summary>
        public void TestP3_隐匿_不能正面付费打出()
        {
            var game = NewGame();
            var tmpl = Load("JC097_asiwang");
            Assert.IsNotNull(tmpl, "JC097 模板应可加载");
            Assert.IsTrue(tmpl.EffectDataList.Count > 0, "JC097 应已接入效果（本批只接「隐匿」）");

            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToHand(card, 1);
            // 让玩家1 成为当前行动权持有者且处于自己的行动步骤（否则测试会因阶段门禁失败而"假通过"）
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            Assert.IsTrue(game.TurnManager.CanEnterBasicActionMask(1),
                "前置：基础行动门禁应放行（否则本用例可能是因其它原因被拒的假通过）");

            bool played = game.TurnManager.PlayCard(1, card.UID, FirstArea(game));

            Assert.IsFalse(played, "具有【隐匿】的角色不能从手牌付费正面打出到争夺区");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "被拒绝后应留在手牌");
        }

        /// <summary>对照：没有【隐匿】的同形卡可以正常打出（确认不是"整体打出门禁坏了"）。</summary>
        public void TestP3_隐匿_对照卡可正常打出()
        {
            var game = NewGame();
            var card = game.CardManager.CreateCardInstance(Probe("TS_p3_normal"), 1);
            game.ZoneManager.MoveToHand(card, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            Assert.IsTrue(game.TurnManager.CanEnterBasicActionMask(1),
                "前置：基础行动门禁应放行");

            bool played = game.TurnManager.PlayCard(1, card.UID, FirstArea(game));

            Assert.IsTrue(played, "普通角色应能正常打出（对照，证明隐匿门禁是唯一拒绝原因）");
        }

        // ==================== 屏障 ====================

        /// <summary>JC017 除咒师：`Gain{"屏障"}` 后，敌方来源不能把它选为目标；己方来源不受限。</summary>
        public void TestP3_屏障_敌方不可指定_己方可以()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var guarded = OnField(game, Probe("TS_p3_guarded"), 1, area);
            Assert.IsFalse(guarded.HasGuard, "初始无屏障");

            var gainBarrier = new EffectData
            {
                EffectId = "TS_p3_gain_barrier",
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "屏障", "1" } },
                },
                Duration = "直到回合结束",
            };
            EffectEngine.Instance.ResolveEffect(gainBarrier, null, guarded);
            Assert.IsTrue(guarded.HasGuard, "Gain『屏障』应置位 HasGuard");

            var enemySource = OnField(game, Probe("TS_p3_enemy"), 2, area);
            var ownSource = OnField(game, Probe("TS_p3_own"), 1, area);

            Assert.IsTrue(TargetValidity.IsBarrierBlocking(guarded, 2), "敌方来源应被屏障拦下");
            Assert.IsFalse(TargetValidity.IsBarrierBlocking(guarded, 1), "己方来源不受屏障限制");

            // 取目标阶段与结算复检共用 CheckRestrictions 内的判定
            Assert.IsFalse(TargetValidity.CheckRestrictions(null, "", guarded, 2),
                "CheckRestrictions 应把『敌方对屏障目标』判为不合法");
            Assert.IsTrue(TargetValidity.CheckRestrictions(null, "", guarded, 1),
                "己方对屏障目标仍应合法");
            Assert.IsTrue(ReferenceEquals(enemySource, enemySource) && ReferenceEquals(ownSource, ownSource));
        }

        /// <summary>时效：`Gain{"屏障"}` + "直到回合结束" → 回合结束时失去屏障。</summary>
        public void TestP3_屏障_回合结束失效()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var card = OnField(game, Probe("TS_p3_timed_guard"), 1, area);

            var gainBarrier2 = new EffectData
            {
                EffectId = "TS_p3_gain_barrier_timed",
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "屏障", "1" } },
                },
                Duration = "直到回合结束",
            };
            EffectEngine.Instance.ResolveEffect(gainBarrier2, null, card);
            Assert.IsTrue(card.HasGuard, "前置：已获得屏障");

            EffectEngine.Instance.ExpireTurnEndEffects(EffectEngine.Instance.CurrentTurn);
            Assert.IsFalse(card.HasGuard, "回合结束后屏障应失效");
        }

        // ==================== 撤回 ====================

        /// <summary>JC016 执行部精锐（角色）与 JC116（附属）：地区被赢取时回 OWNER 手牌而非牌库底。</summary>
        public void TestP3_撤回_角色回手牌而非牌库底()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var withdrawer = OnField(game, Load("JC016_zhixingbujingrui"), 1, area);
            var normal = OnField(game, Probe("TS_p3_no_withdraw"), 1, area);

            // 注册持续效果（撤回关键字来自持续授予；印在卡面上的则走固有判定）
            EffectEngine.Instance.RegisterPersistent(withdrawer);
            EffectEngine.Instance.RegisterPersistent(normal);

            game.AreaManager.ResolveAreaWon(area, 2);

            Assert.AreEqual(CardZone.Hand, withdrawer.CurrentZone, "具有【撤回】的卡应回 OWNER 手牌");
            Assert.AreEqual(CardZone.Deck, normal.CurrentZone, "无【撤回】的卡按常规回牌库底");
        }

        /// <summary>JC116 柯尔特巨蟒左轮作为"结附于角色的附属"时，也应回手而不是随宿主回库底。</summary>
        public void TestP3_撤回_附属回手牌()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var host = OnField(game, Probe("TS_p3_host"), 1, area);
            var revolver = game.CardManager.CreateCardInstance(Load("JC116_keertejumangzuolun"), 1);
            revolver.IsAttached = true;
            revolver.AttachedTo = host;
            host.AttachedCards.Add(revolver);
            game.ZoneManager.MoveToAttachedZone(revolver, area, 1);
            EffectEngine.Instance.RegisterPersistent(revolver);

            game.AreaManager.ResolveAreaWon(area, 2);

            Assert.AreEqual(CardZone.Hand, revolver.CurrentZone, "带【撤回】的附属应回 OWNER 手牌");
            Assert.AreEqual(CardZone.Deck, host.CurrentZone, "其宿主按常规回牌库底");
        }

        // ==================== 非资产 ====================

        public void TestP3_非资产_不能建立资产()
        {
            var game = NewGame();
            var tmpl = Probe("TS_p3_nonasset", CardCategory.Attachment);
            var nonAsset = KeywordEffects.NonAsset();
            nonAsset.EffectId = "TS_p3_nonasset_kw";
            tmpl.EffectDataList.Add(nonAsset);
            tmpl._effectsInjected = true;

            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToHand(card, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            bool ok = game.TurnManager.EstablishAsset(1, card.UID);

            Assert.IsFalse(ok, "具有【非资产】的卡不能用于建立资产");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "被拒绝后应留在手牌");
        }

        public void TestP3_非资产_对照卡可以建立资产()
        {
            var game = NewGame();
            var card = game.CardManager.CreateCardInstance(Probe("TS_p3_asset_ok", CardCategory.Attachment), 1);
            game.ZoneManager.MoveToHand(card, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            Assert.IsTrue(game.TurnManager.EstablishAsset(1, card.UID), "普通卡应能建立资产（对照）");
            Assert.AreEqual(CardZone.AssetZone, card.CurrentZone, "应进入资产区");
        }

        // ==================== XX标志X ====================

        /// <summary>JC044 灾难使者「时间标志1」= 强制进场触发：进场后给自己放 1 个时间标志。</summary>
        public void TestP3_标志_进场强制给自己放标志()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var card = OnField(game, Load("JC044_zainanshizhe"), 1, area);
            Assert.IsTrue(card.Counters.Count == 0, "进场前无标志");

            // 进场触发（本用例直接结算该效果；触发收集链路由既有套件覆盖）
            var e = Find(card.PlayerTemplate, "JC044_A_TimeMarker1");
            Assert.IsNotNull(e, "JC044 应有时间标志词条");
            Assert.IsTrue(e.Forced, "『XX标志X』为强制进场触发");
            EffectEngine.Instance.ResolveEffect(e, card, null);

            Assert.AreEqual(1, card.Counters["时间标志"], "应放置 1 个时间标志");
        }

        /// <summary>卡上标志可累加（多次触发 → 多个标志），并广播 OnMarkerPlaced 供"放置触发"消费。</summary>
        public void TestP3_标志_累加与放置广播()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var card = OnField(game, Load("JC044_zainanshizhe"), 1, area);
            var e = Find(card.PlayerTemplate, "JC044_A_TimeMarker1");


            EffectEngine.Instance.ResolveEffect(e, card, null);
            EffectEngine.Instance.ResolveEffect(e, card, null);

            Assert.AreEqual(2, card.Counters["时间标志"], "两次触发应累加到 2 个标志");
        }

        /// <summary>JC069 警犬「在目标角色或暗藏者上放置一个锁定标志」：对象=目标。</summary>
        public void TestP3_标志_放在目标身上()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var dog = OnField(game, Load("JC069_jingquan"), 1, area);
            var victim = OnField(game, Probe("TS_p3_lock_target"), 2, area);

            var e = Find(dog.PlayerTemplate, "JC069_1_UncoverPlaceLockMarker");
            Assert.IsNotNull(e, "JC069 应有现身放锁定标志");
            Assert.IsTrue(e.Target.IncludeCovert, "卡文『角色或暗藏者』→ 应允许暗藏者");
            EffectEngine.Instance.ResolveEffect(e, dog, victim);

            Assert.AreEqual(1, victim.Counters["锁定标志"], "锁定标志应放在目标身上");
            Assert.AreEqual(0, dog.Counters.Count, "不应放在警犬自己身上");
        }

        /// <summary>
        /// 【2026-09 用户口径·修复】JC069 警犬**现身触发**"在目标角色或暗藏者上放置一个锁定标志"：
        /// 卡文**没有写"本地区"** ⇒ 用户实测报障并裁定"我希望**放开到任意地区**"。
        /// 本引擎触发取目标**默认限同 AREASLOT**（`TurnManager.GetTargetUidsForSpec`）⇒ 目标声明必须
        /// 显式 `AllAreas=true`；本用例用**捕获候选**的视图钉住"其他地区的角色/暗藏者也在候选里"。
        /// </summary>
        public void TestP3_JC069现身触发可指定任意地区的角色或暗藏者()
        {
            var game = new GameInitializer { DebugSeed = 71 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(2);
            var areas = game.AreaManager.GetOrderedAreaUids();
            string a0 = areas[0], a1 = areas[1];

            var nearChar = OnField(game, Probe("TS_p3_kw_near"), 2, a0);
            var farChar = OnField(game, Probe("TS_p3_kw_far"), 2, a1);
            var farCovert = game.CardManager.CreateCardInstance(Probe("TS_p3_kw_farcov"), 2);
            game.ZoneManager.MoveToArea(farCovert, a1, 2);
            farCovert.SetCovert(true);

            var view = new 记录候选视图();
            game.TurnManager.SetTriggerView(view);

            var dog = game.CardManager.CreateCardInstance(Load("JC069_jingquan"), 1);
            game.ZoneManager.MoveToArea(dog, a0, 1);
            dog.SetCovert(true);
            game.CardManager.RevealCard(dog);   // 现身 → 现身触发

            Assert.IsNotNull(view.候选, "现身触发应要求选择目标");
            Assert.IsTrue(view.候选.Contains(nearChar.UID), "同地区角色可选");
            Assert.IsTrue(view.候选.Contains(farChar.UID), "**其他地区**的角色也可选（用户裁定：放开到任意地区）");
            Assert.IsTrue(view.候选.Contains(farCovert.UID), "**其他地区**的暗藏者也可选（卡文『角色或暗藏者』）");
        }

        /// <summary>捕获候选的触发视图（不选目标，只为观察候选集合）。</summary>
        private sealed class 记录候选视图 : Script.Interfaces.ITriggerView
        {
            public System.Collections.Generic.List<string> 候选;
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                System.Collections.Generic.List<StackEffectInstance> triggers,
                System.Action<System.Collections.Generic.List<StackEffectInstance>> onEnterStack,
                System.Action onDone)
            {
                onEnterStack?.Invoke(triggers);
                onDone?.Invoke();
            }
            public void RequestTargetSelection(StackEffectInstance trigger,
                System.Collections.Generic.List<string> validTargetUids, System.Action<string> onPicked)
            {
                候选 = new System.Collections.Generic.List<string>(validTargetUids ?? new System.Collections.Generic.List<string>());
                onPicked?.Invoke(null);
            }
            public void RemoveOrderEntries(System.Collections.Generic.List<StackEffectInstance> entries) { }
        }

        // ==================== 杀伤 X ====================

        public void TestP3_杀伤_授予属性并可累加()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var a = OnField(game, Load("JC016_zhixingbujingrui"), 1, area);   // 杀伤1
            var b = OnField(game, Load("JC046_wumiantuluzhe"), 1, area);      // 杀伤2

            EffectEngine.Instance.RegisterPersistent(a);
            EffectEngine.Instance.RegisterPersistent(b);

            Assert.AreEqual(1, a.GetAttribute("杀伤"), "JC016 应获得 杀伤1");
            Assert.AreEqual(2, b.GetAttribute("杀伤"), "JC046 应获得 杀伤2");
            Assert.AreEqual(3, a.GetAttribute("杀伤") + b.GetAttribute("杀伤"), "多个杀伤角色可累加");
        }

        // ==================== 灵体（伤害防止） ====================

        /// <summary>灵体：被分配伤害时比较该地区双方魔法领域图标数，对手更少 → 防止伤害（不写伤害、不判死）。</summary>
        public void TestP3_灵体_我方图标多则防止伤害()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var spirit = MakeWithDomains(game, "TS_p3_spirit", 1, area, MagicDomain.Mind, spirit: true, defense: 3);
            MakeWithDomains(game, "TS_p3_ally", 1, area, MagicDomain.Star);
            MakeWithDomains(game, "TS_p3_enemy", 2, area, MagicDomain.Death);

            Assert.IsTrue(DamageRules.IsSpirit(spirit), "灵体角色应被识别");
            game.CardManager.DealDamageToInstance(spirit, 5);

            Assert.AreEqual(0, spirit.Damage, "对手图标更少 → 本次伤害应被防止（不写伤害）");
            Assert.IsFalse(spirit.IsDead(), "伤害被防止 → 不应死亡");
        }

        /// <summary>反向对照：对手图标不少于我方便不防止。</summary>
        public void TestP3_灵体_对手图标不少于我则不防止()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var spirit = MakeWithDomains(game, "TS_p3_spirit2", 1, area, MagicDomain.Mind, spirit: true, defense: 3);
            MakeWithDomains(game, "TS_p3_enemy2", 2, area, MagicDomain.Death);
            MakeWithDomains(game, "TS_p3_enemy3", 2, area, MagicDomain.Blood);

            game.CardManager.DealDamageToInstance(spirit, 2);

            Assert.AreEqual(2, spirit.Damage, "对手图标不少于我方 → 伤害不被防止");
        }

        /// <summary>对照：1:1 相等也不防止（规则为"严格小于才防止"）。</summary>
        public void TestP3_灵体_双方图标相等不防止()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var spirit = MakeWithDomains(game, "TS_p3_spirit3", 1, area, MagicDomain.Mind, spirit: true, defense: 3);
            MakeWithDomains(game, "TS_p3_enemy4", 2, area, MagicDomain.Death);

            game.CardManager.DealDamageToInstance(spirit, 1);

            Assert.AreEqual(1, spirit.Damage, "双方图标相等 → 不防止");
        }

        // ==================== JC063 门禁 + 模式可用性（2026-09 用户口径） ====================

        /// <summary>
        /// 【2026-09 修复·用户实测"JC063 门禁错误"】快速行动 ⇒ **C 档（可响应打出）、不限行动阶段**
        /// （与 JC062/JC065 同口径）；且各模式**可用性按该模式自己的目标声明区分**
        /// （用户口径："JC063 两个效果如果场上完全没有合法目标，置灰（注意两个效果要求的合法目标不一致）"）。
        /// </summary>
        public void TestP3_JC063门禁与模式可用性()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var tmpl = Load("JC063_jingchezhuibu");
            Assert.IsNotNull(tmpl, "JC063 模板应可加载");
            Assert.IsFalse(tmpl.LimitToActionPhaseOnly, "**快速行动 ⇒ 不限行动阶段**（此前误写 LimitToActionPhaseOnly=true）");
            var effect = Script.Manager.MultiTargetSlots.FindModeEffect(tmpl, EffectTiming.OnSorceryCast);
            Assert.IsNotNull(effect, "JC063 应有『选择一项』模式效果");

            var faceUp = OnField(game, Probe("TS_jc063_face"), 1, area);
            var covert = OnField(game, Probe("TS_jc063_cov"), 2, area);
            covert.SetCovert(true);
            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToHand(card, 1);

            var avail = game.TurnManager.GetModeAvailability(effect, card);
            Assert.AreEqual(2, avail.Count, "两个模式各一项");
            Assert.IsTrue(avail[0], "场上有正面角色 ⇒ 模式1（将目标角色潜伏）可用");
            Assert.IsTrue(avail[1], "场上有暗藏者 ⇒ 模式2（将目标暗藏者移回手牌）可用");

            // 只有正面角色、没有暗藏者 ⇒ 模式2 置灰（两个模式要求的合法目标不一致）
            covert.SetCovert(false);
            var avail2 = game.TurnManager.GetModeAvailability(effect, card);
            Assert.IsTrue(avail2[0], "模式1 仍可用");
            Assert.IsFalse(avail2[1], "**没有暗藏者 ⇒ 模式2 置灰**");

            // 两个角色都翻面为暗藏者 ⇒ 模式1（需要正面角色）置灰，模式2（需要暗藏者）可用
            faceUp.SetCovert(true);
            covert.SetCovert(true);
            var avail3 = game.TurnManager.GetModeAvailability(effect, card);
            Assert.IsFalse(avail3[0], "没有正面角色 ⇒ 模式1 置灰");
            Assert.IsTrue(avail3[1], "有暗藏者 ⇒ 模式2 可用");
            // 场上清空（都回手牌）⇒ 两个模式都置灰
            game.ZoneManager.MoveToHand(covert, 2);
            game.ZoneManager.MoveToHand(faceUp, 1);
            var avail4 = game.TurnManager.GetModeAvailability(effect, card);
            Assert.IsFalse(avail4[0] && avail4[1], "场上完全没有合法目标 ⇒ 两个模式都置灰");

            // 门禁：非行动阶段（后手行动）也能打出（快速行动 = 可响应）
            // 费用/忠诚：JC063 需 2 费、颜色忠诚 4:2 → 先造两张"颜色4"的资产（本套件无 GiveAssets 辅助）
            for (int i = 0; i < 2; i++)
            {
                var assetTmpl = new PlayerCardData
                {
                    UID = $"TS_jc063_asset{i}", CardName = $"探针资产{i}", Cost = 0,
                    CategoryFlag = CardCategory.Attachment,
                };
                assetTmpl.Colors.Add((CardColor)4);
                assetTmpl._effectsInjected = true;
                var asset = game.CardManager.CreateCardInstance(assetTmpl, 1);
                game.ZoneManager.MoveToAsset(asset, 1);
            }
            var faceUp2 = OnField(game, Probe("TS_jc063_face2"), 1, area);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionSecond;
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, faceUp2.UID, null, 0),
                "**非行动阶段也能打出**（快速行动 ⇒ 不限行动阶段）");
        }


        /// <summary>
        /// 【2026-09 用户口径·声望改触发】声望**不再**在势力对抗出口立即结算（用户实测："声望是触发效果，
        /// 现在实现成直接依附于势力对抗结算后立刻结算了"）：
        /// ① 单机路径：一场 0:0 平局的势力对抗打完，**此时不放标志**（只等"该地区所有对抗结束"的触发）；
        /// ② 到达触发点后由触发结算放置。
        /// </summary>
        public void TestP3_声望_零比零平局也结算_单机()
        {
            var game = NewGame();
            var area = FirstArea(game);
            MakeWithDomains(game, "TS_p3_renown_tie1", 1, area, MagicDomain.Mind, renown: true);   // 势力值 0 ⇒ 0:0 平局
            var view = new 推入触发视图();
            game.TurnManager.SetTriggerView(view);

            var before = game.AreaManager.GetMarks(area);
            bool done = false;
            game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Power, area, 1, () => done = true);
            Assert.IsTrue(done, "势力对抗流程走完（0:0 平局不可破平）");
            Assert.AreEqual(before.p1, game.AreaManager.GetMarks(area).p1,
                "**声望不在对抗出口立即结算**（否则就是用户报障的『直接依附于势力对抗结算』）");

            // 触发点：该地区三种对抗全部结束（TurnManager 在阶段推进时调用）
            game.TurnManager.EmitPowerCombatEnded(area);
            Assert.AreEqual(1, view.Collected.Count, "到达触发点后产生声望触发（与胜负无关，0:0 平局同样触发）");
            ResolveStackOnce(game);
            Assert.AreEqual(before.p1 + 1, game.AreaManager.GetMarks(area).p1, "触发结算 → 声望较多方 +1 势力标志");
        }

        /// <summary>同上，锁**联机（权威）路径**：声望同样只在"该地区所有对抗结束"时触发（不在对抗出口立即结算）。</summary>
        public void TestP3_声望_零比零平局也结算_联机()
        {
            var game = NewGame();
            var area = FirstArea(game);
            MakeWithDomains(game, "TS_p3_renown_tie2", 1, area, MagicDomain.Star, renown: true);
            var view = new 推入触发视图();
            game.TurnManager.SetTriggerView(view);

            var before = game.AreaManager.GetMarks(area);
            game.CombatManager.ResolveAreaCombatAsync(CombatManager.CombatType.Power, area, 1)
                .GetAwaiter().GetResult();
            Assert.AreEqual(before.p1, game.AreaManager.GetMarks(area).p1,
                "联机路径同样**不在对抗出口立即结算**声望");

            game.TurnManager.EmitPowerCombatEnded(area);
            Assert.AreEqual(1, view.Collected.Count, "到达触发点后产生声望触发");
            ResolveStackOnce(game);
            Assert.AreEqual(before.p1 + 1, game.AreaManager.GetMarks(area).p1, "触发结算 → +1 势力标志");
        }

        public void TestP3_声望_对抗阶段结束触发_较多方放标记()
        {
            var game = NewGame();
            var area = FirstArea(game);
            MakeWithDomains(game, "TS_p3_renown1", 1, area, MagicDomain.Mind, renown: true);
            MakeWithDomains(game, "TS_p3_plain", 2, area, MagicDomain.Death);
            var view = new 推入触发视图();
            game.TurnManager.SetTriggerView(view);

            var before = game.AreaManager.GetMarks(area);
            // 触发点＝"该地区所有对抗结束"（TurnManager 在阶段推进时调用；这里直接走同一入口）
            game.TurnManager.EmitPowerCombatEnded(area);

            Assert.AreEqual(1, view.Collected.Count, "声望是**触发效果**：被收集为 1 个触发（不是立即结算）");
            Assert.AreEqual(before.p1, game.AreaManager.GetMarks(area).p1, "只是收集，**尚未**放置标记（要等触发结算）");

            ResolveStackOnce(game);
            var after = game.AreaManager.GetMarks(area);
            Assert.AreEqual(before.p1 + 1, after.p1, "触发结算后：声望较多方（玩家1）放置 1 个势力标志");
            Assert.AreEqual(before.p2, after.p2, "玩家2 的标记不变");
        }

        public void TestP3_声望_相等不触发()
        {
            var game = NewGame();
            var area = FirstArea(game);
            MakeWithDomains(game, "TS_p3_renown2", 1, area, MagicDomain.Mind, renown: true);
            MakeWithDomains(game, "TS_p3_renown3", 2, area, MagicDomain.Death, renown: true);
            var view = new 推入触发视图();
            game.TurnManager.SetTriggerView(view);

            game.TurnManager.EmitPowerCombatEnded(area);
            Assert.AreEqual(0, view.Collected.Count, "双方声望角色相等 → 条件不成立 ⇒ **不产生触发**（也就不会放置）");
        }

        public void TestP3_声望_多个声望只放一个()
        {
            var game = NewGame();
            var area = FirstArea(game);
            MakeWithDomains(game, "TS_p3_renown4", 1, area, MagicDomain.Mind, renown: true);
            MakeWithDomains(game, "TS_p3_renown5", 1, area, MagicDomain.Star, renown: true);
            MakeWithDomains(game, "TS_p3_renown6", 1, area, MagicDomain.Blood, renown: true);
            var view = new 推入触发视图();
            game.TurnManager.SetTriggerView(view);

            var before = game.AreaManager.GetMarks(area);
            game.TurnManager.EmitPowerCombatEnded(area);
            ResolveStackOnce(game);
            var after = game.AreaManager.GetMarks(area);

            Assert.AreEqual(before.p1 + 1, after.p1, "3 个声望角色也只放 1 个势力标志（同维度去重）");
        }

        /// <summary>
        /// 【2026-09 用户口径·第二轮】声望改为**每个势力对抗后都触发**：
        /// · 同一次势力对抗内**多个声望角色都触发**，但也**只加 1 个势力标志**（`PlaceToken` 的"声望"维度去重，参考威名）；
        /// · 同一地区再打一次势力对抗（JC067「额外进行一次势力对抗」）⇒ **再触发并再加 1 个**。
        /// </summary>
        public void TestP3_声望_每次势力对抗触发_同一次只加一个()
        {
            var game = NewGame();
            var area = FirstArea(game);
            // 3 个声望角色（同一方）→ 同一次势力对抗内会收集出 3 个触发
            MakeWithDomains(game, "TS_p3_renown7", 1, area, MagicDomain.Mind, renown: true);
            MakeWithDomains(game, "TS_p3_renown8", 1, area, MagicDomain.Star, renown: true);
            MakeWithDomains(game, "TS_p3_renown9", 1, area, MagicDomain.Blood, renown: true);
            var view = new 推入触发视图();
            game.TurnManager.SetTriggerView(view);

            var before = game.AreaManager.GetMarks(area);
            game.TurnManager.EmitPowerCombatEnded(area);   // 第 1 次势力对抗结束
            Assert.AreEqual(3, view.Collected.Count, "3 个声望角色各产生 1 个触发（每个势力对抗后都触发）");
            ResolveStackOnce(game);
            Assert.AreEqual(before.p1 + 1, game.AreaManager.GetMarks(area).p1,
                "**同一次势力对抗内多个声望角色也只加 1 个势力标志**（同维度去重，参考威名）");

            // JC067：同一地区本回合再打一次势力对抗 ⇒ 再触发一次、再加 1 个
            int afterFirst = game.AreaManager.GetMarks(area).p1;
            game.TurnManager.EmitPowerCombatEnded(area);
            ResolveStackOnce(game);
            Assert.AreEqual(afterFirst + 1, game.AreaManager.GetMarks(area).p1,
                "**第二次势力对抗各触发一次** ⇒ 再加 1 个（用户口径：每个势力对抗后都触发）");
        }

        /// <summary>构造带指定领域（可选灵体/声望词条）的场上角色。</summary>
        private static CardInstance MakeWithDomains(GameInitializer game, string uid, int controllerId, string areaUid,
            MagicDomain domain, bool spirit = false, bool renown = false, int defense = 1)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = defense,
            };
            tmpl.MagicDomains.Add(domain);
            if (spirit)
            {
                tmpl.EffectDataList.Add(new EffectData
                {
                    EffectId = uid + "_spirit", Timing = EffectTiming.Continuous, Scope = ZoneScope.AreaZone,
                    ContinuousGrants = new Array<EffectGrant> { new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 } },
                });
            }
            if (renown)
            {
                tmpl.EffectDataList.Add(new EffectData
                {
                    EffectId = uid + "_renown", Timing = EffectTiming.Continuous, Scope = ZoneScope.AreaZone,
                    ContinuousGrants = new Array<EffectGrant> { new EffectGrant { Kind = "Keyword", Value = "声望", Amount = 1 } },
                });
                // 【2026-09 用户口径·声望改触发】声望的规则本体＝触发效果（`OnPowerCombatEnded`）：
                // 与真实卡面同款，需**显式**声明触发条目（卡面固有关键字不会被模板通道重复合成）。
                var renownTrigger = Script.Data.Effects.KeywordEffects.Renown();
                renownTrigger.EffectId = uid + "_renown_trigger";
                tmpl.EffectDataList.Add(renownTrigger);
            }
            tmpl._effectsInjected = true;

            var card = game.CardManager.CreateCardInstance(tmpl, controllerId);
            game.ZoneManager.MoveToArea(card, areaUid, controllerId);
            return card;
        }

        private static EffectData Find(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }
    }
}
