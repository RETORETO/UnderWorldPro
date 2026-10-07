using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Interfaces;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【K2·群体动作】按用户 2026-09 口径实现并锁定：
    ///  · `TargetSpec.MassMode = AllInArea` → 作用于"**某地区内符合条件的所有卡**"，不取单个目标；
    ///  · 作用地区＝本次结算的目标地区（打出时选的地区 / 触发事件所在地），为空＝来源所在地区；
    ///  · 集合条件＝`Kind` + `IncludeCovert` + `Filter`（检索筛选语法）+ `Restrictions`；排除正在死亡的卡与来源自身（除非 `CanTargetSelf`）；
    ///  · **一次结算内对集合每张卡各执行一次动作序列（完整执行）**；**不做"批量期间挂起触发"**——
    ///    期间处于堆叠结算的原子段（其它玩家无法行动），各目标死亡产生的死亡触发按既有触发管线收集（多条由玩家排序）。
    /// 覆盖卡：**JC052 终焉的诗寇蒂**（进场触发：消灭所有本地区的其他角色和暗藏者）、
    ///        **JC050 丧钟回响**（行动：选一个地区，消灭该地区所有角色与暗藏者）。
    /// </summary>
    public class P3MassTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 71 };
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

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static PlayerCardData Probe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 3,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int controllerId, string areaUid)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, controllerId);
            game.ZoneManager.MoveToArea(card, areaUid, controllerId);
            return card;
        }

        // ==================== ① 集合枚举 ====================

        public void TestK2_集合按地区与阵营收敛()
        {
            var game = NewGame(2);
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];

            var src = OnField(game, Probe("TS_k2_src"), 1, a1);
            var mineA = OnField(game, Probe("TS_k2_mineA"), 1, a1);
            var enemyA = OnField(game, Probe("TS_k2_enemyA"), 2, a1);
            var enemyOther = OnField(game, Probe("TS_k2_enemyOther"), 2, a2);

            // 本地区 · 任意阵营 · 排除自身
            var allLocal = new EffectData
            {
                EffectId = "TS_k2_e_all",
                Target = new TargetSpec { Kind = TargetKind.Character, MassMode = MassMode.AllInArea, NeedsSelection = false },
            };
            var set = MassTargetPool.Collect(allLocal, src, "", game.ZoneManager, game.CardManager, game.PlayerManager);
            Assert.AreEqual(2, set.Count, "本地区集合＝该地区内的角色（不含来源自身、不含其他地区）");
            Assert.IsTrue(set.Contains(mineA) && set.Contains(enemyA), "含本方与敌方角色");
            Assert.IsFalse(set.Contains(src), "默认排除来源自身");
            Assert.IsFalse(set.Contains(enemyOther), "不含其他地区的角色");

            // 敌方限定
            var enemyOnly = new EffectData
            {
                EffectId = "TS_k2_e_enemy",
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character, MassMode = MassMode.AllInArea, NeedsSelection = false,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                },
            };
            var enemySet = MassTargetPool.Collect(enemyOnly, src, "", game.ZoneManager, game.CardManager, game.PlayerManager);
            Assert.AreEqual(1, enemySet.Count, "『所有敌方角色』→ 只含敌方角色");
            Assert.IsTrue(enemySet.Contains(enemyA), "命中本地区敌方角色");

            // 显式指定地区（打出时选择的地区）
            var chosenAreaSet = MassTargetPool.Collect(allLocal, src, a2, game.ZoneManager, game.CardManager, game.PlayerManager);
            Assert.AreEqual(1, chosenAreaSet.Count, "指定地区 → 取该地区内的角色");
            Assert.IsTrue(chosenAreaSet.Contains(enemyOther), "命中指定地区内的角色");

            // 含暗藏者（IncludeCovert）
            var covert = OnField(game, Probe("TS_k2_covert"), 2, a1);
            covert.SetCovert(true);
            var withCovert = new EffectData
            {
                EffectId = "TS_k2_e_covert",
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character, IncludeCovert = true, MassMode = MassMode.AllInArea, NeedsSelection = false,
                },
            };
            var covertSet = MassTargetPool.Collect(withCovert, src, "", game.ZoneManager, game.CardManager, game.PlayerManager);
            Assert.AreEqual(3, covertSet.Count, "IncludeCovert=true → 暗藏者也计入集合");
            Assert.IsTrue(covertSet.Contains(covert), "暗藏者被包含");
        }

        public void TestK2_正在死亡的卡不入集合()
        {
            var game = NewGame();
            var areas = Areas(game);
            var src = OnField(game, Probe("TS_k2_src2"), 1, areas[0]);
            var dying = OnField(game, Probe("TS_k2_dying"), 2, areas[0]);
            dying.IsDying = true;

            var e = new EffectData
            {
                EffectId = "TS_k2_e_dying",
                Target = new TargetSpec { Kind = TargetKind.Character, MassMode = MassMode.AllInArea, NeedsSelection = false },
            };
            var set = MassTargetPool.Collect(e, src, "", game.ZoneManager, game.CardManager, game.PlayerManager);
            Assert.AreEqual(0, set.Count, "正在死亡（IsDying）的卡不应再被群体效果重复处置");
            dying.IsDying = false;
        }

        // ==================== ② JC052 端到端：本地区其他角色与暗藏者全灭 ====================

        public void TestK2_JC052_进场消灭本地区其他角色与暗藏者()
        {
            var game = NewGame(2);
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            game.SetInputGateway(new EnterAllGateway());   // 触发阶段走网关（死亡触发收集不阻塞）

            var mine = OnField(game, Probe("TS_k2_52_mine"), 1, a1);
            var enemy = OnField(game, Probe("TS_k2_52_enemy"), 2, a1);
            var covert = OnField(game, Probe("TS_k2_52_covert"), 2, a1);
            covert.SetCovert(true);
            var faraway = OnField(game, Probe("TS_k2_52_far"), 2, a2);

            var shikoudi = OnField(game, Load("JC052_zhongyandeshikoudi"), 1, a1);
            var e = FindEffect(shikoudi.PlayerTemplate, "JC052_1_DestroyAllOthersInArea");
            Assert.IsNotNull(e, "JC052 应有群体消灭效果");
            Assert.AreEqual(MassMode.AllInArea, e.Target.MassMode, "应声明群体模式");
            // 【2026-09 修复·用户口径】卡文没有"你可以" ⇒ **非强制**（原先误写成强制触发）
            Assert.IsFalse(e.Forced, "JC052 进场触发**不是**强制触发（用户实测报障）");
            Assert.IsTrue(e.Target.VersionSkipsSelf, "『其他』＝与收集触发时**同一张卡**（版本号敏感的排除自身）");

            EffectEngine.Instance.ResolveEffect(e, shikoudi, null, a1);

            Assert.AreEqual(CardZone.Graveyard, mine.CurrentZone, "本地区本方角色被消灭");
            Assert.AreEqual(CardZone.Graveyard, enemy.CurrentZone, "本地区敌方角色被消灭");
            Assert.AreEqual(CardZone.Graveyard, covert.CurrentZone, "本地区暗藏者被消灭");
            Assert.AreEqual(CardZone.AreaZone, faraway.CurrentZone, "其他地区的角色不受影响");
            Assert.AreEqual(CardZone.AreaZone, shikoudi.CurrentZone, "卡文『其他』→ 来源自身不被消灭");
        }

        /// <summary>
        /// 【2026-09 修复·用户口径】**版本号不同的自己照常被消灭**：
        /// 用户举例："JC052 进场 → 进场触发 A 收集 → 翻面（版本号刷新）→ 重新现身 → 进场触发 B；
        /// 两个进场触发都结算，**进场 A 应当消灭 JC052**（A 是版本号更老的 JC052 发动的）"。
        /// 即"其他角色"里的"其他"＝**与收集该触发时同一张卡**；版本已变则现存这张"自己"可被消灭。
        /// </summary>
        public void TestK2_JC052_版本号不同的自己会被老触发消灭()
        {
            var game = NewGame(1);
            string area = Areas(game)[0];
            game.SetInputGateway(new EnterAllGateway());

            var foe = OnField(game, Probe("TS_k2_52v_foe"), 2, area);
            var shikoudi = OnField(game, Load("JC052_zhongyandeshikoudi"), 1, area);   // 进场触发入堆叠（记录版本号）
            int collectedVersion = shikoudi.VersionId;

            shikoudi.SetCovert(true);   // 翻面 → 版本号刷新（模拟"进场A 收集后翻面/重新现身"）
            Assert.IsFalse(collectedVersion == shikoudi.VersionId, "前置：翻面刷新版本号");

            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.Graveyard, shikoudi.CurrentZone,
                "版本号已变 ⇒ 老触发把现存的这张『自己』一并消灭（用户口径）");
            Assert.AreEqual(CardZone.Graveyard, foe.CurrentZone, "本地区其他角色照常被消灭");
        }

        /// <summary>【2026-09】JC047：**只有一方**在场上有角色时也照常结算（不该因另一方为空而整段不生效）。</summary>
        public void TestK2_JC047_单方有角色也照常结算()
        {
            var game = NewGame(2);
            var areas = Areas(game);
            game.SetInputGateway(new EnterAllGateway());

            // 所选地区只有**敌方**角色（施术者一方无角色）
            var enemyOnly = OnField(game, Probe("TS_k2_47_enemyonly"), 2, areas[0]);
            var card = game.CardManager.CreateCardInstance(Load("JC047_liehuozhongwei"), 1);
            var e = FindEffect(card.PlayerTemplate, "JC047_1_DamageAllInChosenArea");
            EffectEngine.Instance.ResolveEffect(e, card, null, areas[0]);
            Assert.AreEqual(1, enemyOnly.Damage, "所选地区只有敌方角色 → 照样各受 1 点伤害");

            // 反向：所选地区只有**本方**角色
            var game2 = NewGame(2);
            var areas2 = Areas(game2);
            game2.SetInputGateway(new EnterAllGateway());
            var mineOnly = OnField(game2, Probe("TS_k2_47_mineonly"), 1, areas2[0]);
            var card2 = game2.CardManager.CreateCardInstance(Load("JC047_liehuozhongwei"), 1);
            var e2 = FindEffect(card2.PlayerTemplate, "JC047_1_DamageAllInChosenArea");
            EffectEngine.Instance.ResolveEffect(e2, card2, null, areas2[0]);
            Assert.AreEqual(1, mineOnly.Damage, "所选地区只有本方角色 → 各受 1 点伤害");
        }

        /// <summary>群体动作**完整执行**：集合内全部死亡，且死亡触发出现在堆叠（触发窗口在效果之后）。</summary>
        public void TestK2_群体动作完整执行且触发随后入堆叠()
        {
            var game = NewGame();
            var areas = Areas(game);
            game.SetInputGateway(new EnterAllGateway());

            var src = OnField(game, Probe("TS_k2_atom_src"), 1, areas[0]);

            // 旁观者：每当有角色死亡 → 触发（用于观察群体期间的死亡触发收集）
            var watcherTmpl = new PlayerCardData
            {
                UID = "TS_k2_atom_watcher", CardName = "探针_死亡旁观", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 3,
            };
            watcherTmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_k2_atom_watcher_e",
                Timing = EffectTiming.OnCardDied,
                Condition = new EffectCondition { Predicate = "DiedAnyCharacter" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "1" } } },
            });
            watcherTmpl._effectsInjected = true;
            OnField(game, watcherTmpl, 1, areas[0]);

            var a = OnField(game, Probe("TS_k2_atom_a"), 2, areas[0]);
            var b = OnField(game, Probe("TS_k2_atom_b"), 2, areas[0]);
            var c = OnField(game, Probe("TS_k2_atom_c"), 2, areas[0]);

            var massEffect = new EffectData
            {
                EffectId = "TS_k2_atom_mass",
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character, MassMode = MassMode.AllInArea, NeedsSelection = false,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Destroy } },
            };

            EffectEngine.Instance.ResolveEffect(massEffect, src, null, areas[0]);

            Assert.AreEqual(CardZone.Graveyard, a.CurrentZone, "集合内第 1 张被消灭");
            Assert.AreEqual(CardZone.Graveyard, b.CurrentZone, "集合内第 2 张被消灭");
            Assert.AreEqual(CardZone.Graveyard, c.CurrentZone, "集合内第 3 张被消灭（一次结算内全部完成）");
            Assert.AreEqual(3, game.StackManager.Entries.Count,
                "三张卡各自死亡 → 三条死亡触发被收集入堆叠（触发在群体效果结算之后处理，顺序由玩家排序）");
        }

        // ==================== ③ JC050：打出 → 选地区 → 该地区全清 ====================

        public void TestK2_JC050_结构()
        {
            var tmpl = Load("JC050_sangzhonghuixiang");
            Assert.IsNotNull(tmpl, "JC050 模板应可加载");
            Assert.IsNotNull(tmpl.TargetRules, "JC050 打出目标应为地区");
            Assert.AreEqual(TargetKind.Area, tmpl.TargetRules[0].Kind, "打出时选择目标地区");
            Assert.IsFalse(tmpl.LimitToActionPhaseOnly, "『行动』→ 限行动阶段打出 ⇒ **非快速**：不得在响应窗口打出（A档＝自己行动步骤且堆叠为空）");

            var e = FindEffect(tmpl, "JC050_1_DestroyAllInChosenArea");
            Assert.IsNotNull(e, "JC050 应有群体消灭效果");
            Assert.AreEqual(EffectTiming.OnSorceryCast, e.Timing, "事务卡打出结算");
            Assert.AreEqual(MassMode.AllInArea, e.Target.MassMode, "群体模式");
            Assert.IsTrue(e.Target.IncludeCovert, "包含暗藏者");
            Assert.AreEqual(ActionVerb.Destroy, e.Actions[0].Verb, "动作为消灭");
        }

        public void TestK2_JC050_消灭所选地区全部角色()
        {
            var game = NewGame(2);
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            game.SetInputGateway(new EnterAllGateway());

            var inA1 = OnField(game, Probe("TS_k2_50_a1"), 2, a1);
            var covertA1 = OnField(game, Probe("TS_k2_50_cv"), 1, a1);
            covertA1.SetCovert(true);
            var inA2 = OnField(game, Probe("TS_k2_50_a2"), 2, a2);

            var bell = game.CardManager.CreateCardInstance(Load("JC050_sangzhonghuixiang"), 1);
            game.ZoneManager.MoveToGraveyard(bell, 1);   // 事务卡结算时在墓地（模拟"打出即结算→入墓"）

            var e = FindEffect(bell.PlayerTemplate, "JC050_1_DestroyAllInChosenArea");
            // 结算时携带"打出时选定的目标地区"（= a1）
            EffectEngine.Instance.ResolveEffect(e, bell, null, a1);

            Assert.AreEqual(CardZone.Graveyard, inA1.CurrentZone, "所选地区内角色被消灭");
            Assert.AreEqual(CardZone.Graveyard, covertA1.CurrentZone, "所选地区内暗藏者被消灭");
            Assert.AreEqual(CardZone.AreaZone, inA2.CurrentZone, "未选择地区不受影响");
        }

        // ==================== ④ 其余群体卡：JC011 / JC056 / JC132 / JC047 ====================

        /// <summary>JC011 现身触发：横置本地区所有**敌方**角色（本方不受影响、他地区不受影响）。</summary>
        public void TestK2_JC011_横置本地区所有敌方角色()
        {
            var game = NewGame(2);
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            game.SetInputGateway(new EnterAllGateway());

            var demo = OnField(game, Load("JC011_demositinidamofashi"), 1, a1);
            var mine = OnField(game, Probe("TS_k2_11_mine"), 1, a1);
            var enemy1 = OnField(game, Probe("TS_k2_11_e1"), 2, a1);
            var enemy2 = OnField(game, Probe("TS_k2_11_e2"), 2, a1);
            var enemyFar = OnField(game, Probe("TS_k2_11_far"), 2, a2);

            var e = FindEffect(demo.PlayerTemplate, "JC011_1_TapAllEnemyInArea");
            Assert.IsNotNull(e, "JC011 应有『横置所有敌方角色』效果");
            EffectEngine.Instance.ResolveEffect(e, demo, null, a1);

            Assert.IsTrue(enemy1.IsTapped && enemy2.IsTapped, "本地区敌方角色全部被横置");
            Assert.IsFalse(enemyFar.IsTapped, "其他地区的敌方角色不受影响");
            Assert.IsFalse(mine.IsTapped, "本方角色不受影响（卡文限『敌方』）");
        }

        /// <summary>
        /// 【2026-09 用户口径·修复】JC011 现身触发**不是强制触发**：卡文只写"现身触发：横置…"，
        /// 没有"必须"语义 ⇒ `Forced=false`（玩家可在触发排序窗口把它拖入放弃区）。
        /// </summary>
        public void TestK2_JC011非强制触发()
        {
            var tmpl = Load("JC011_demositinidamofashi");
            var e = FindEffect(tmpl, "JC011_1_TapAllEnemyInArea");
            Assert.IsNotNull(e, "JC011 应有『横置所有敌方角色』效果");
            Assert.IsFalse(e.Forced, "JC011 现身触发不是强制触发（Forced=false，可放弃）");
        }

        /// <summary>JC056 进场触发：横置本地区所有**暗藏者**（角色不受影响）。</summary>
        public void TestK2_JC056_横置本地区所有暗藏者()
        {
            var game = NewGame();
            var areas = Areas(game);
            game.SetInputGateway(new EnterAllGateway());

            var officer = OnField(game, Load("JC056_diqujingyuan"), 1, areas[0]);
            var covert1 = OnField(game, Probe("TS_k2_56_c1"), 2, areas[0]);
            covert1.SetCovert(true);
            var covert2 = OnField(game, Probe("TS_k2_56_c2"), 1, areas[0]);
            covert2.SetCovert(true);
            var plain = OnField(game, Probe("TS_k2_56_p"), 2, areas[0]);

            var e = FindEffect(officer.PlayerTemplate, "JC056_1_TapAllCovert");
            Assert.IsNotNull(e, "JC056 应有『横置所有暗藏者』效果");
            Assert.AreEqual(TargetKind.Covert, e.Target.Kind, "集合大类＝暗藏者");
            EffectEngine.Instance.ResolveEffect(e, officer, null, areas[0]);

            Assert.IsTrue(covert1.IsTapped && covert2.IsTapped, "本地区暗藏者全部被横置（不分敌我）");
            Assert.IsFalse(plain.IsTapped, "正面角色不受影响");
        }

        /// <summary>
        /// 【2026-09 用户口径·修复】JC056 进场触发**不是强制触发**：卡文只写"进场触发：横置…"，
        /// 没有"强制"语义 ⇒ `Forced=false`（玩家可在触发排序窗口把它拖入放弃区）。
        /// 用户实测报障："JC056 的进场触发效果不是强制触发"（原先误写成 `Forced=true` ⇒ 排序窗不能放弃）。
        /// </summary>
        public void TestK2_JC056非强制触发()
        {
            var e = FindEffect(Load("JC056_diqujingyuan"), "JC056_1_TapAllCovert");
            Assert.IsNotNull(e, "JC056 应有『横置所有暗藏者』效果");
            Assert.IsFalse(e.Forced, "JC056 进场触发不是强制触发（Forced=false，可放弃）");
        }

        /// <summary>JC132 行动：选地区 → 横置该地区所有角色。</summary>
        public void TestK2_JC132_横置所选地区所有角色()
        {
            var game = NewGame(2);
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            game.SetInputGateway(new EnterAllGateway());

            var c1 = OnField(game, Probe("TS_k2_132_a1"), 2, a1);
            var c2 = OnField(game, Probe("TS_k2_132_b1"), 1, a1);
            var other = OnField(game, Probe("TS_k2_132_a2"), 2, a2);

            var card = game.CardManager.CreateCardInstance(Load("JC132_gouzhujingtaizhangbi"), 1);
            var e = FindEffect(card.PlayerTemplate, "JC132_1_TapAllInChosenArea");
            Assert.IsNotNull(e, "JC132 应有群体横置效果");
            EffectEngine.Instance.ResolveEffect(e, card, null, a1);

            Assert.IsTrue(c1.IsTapped && c2.IsTapped, "所选地区内角色全部被横置（不分敌我）");
            Assert.IsFalse(other.IsTapped, "未选择地区不受影响");
        }

        /// <summary>JC047 行动：选地区 → 对该地区所有角色各造成 1 点伤害。</summary>
        public void TestK2_JC047_所选地区角色各受1点伤害()
        {
            var game = NewGame(2);
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            game.SetInputGateway(new EnterAllGateway());

            var c1 = OnField(game, Probe("TS_k2_47_a1"), 2, a1);
            var c2 = OnField(game, Probe("TS_k2_47_b1"), 1, a1);
            var other = OnField(game, Probe("TS_k2_47_a2"), 2, a2);

            var card = game.CardManager.CreateCardInstance(Load("JC047_liehuozhongwei"), 1);
            var e = FindEffect(card.PlayerTemplate, "JC047_1_DamageAllInChosenArea");
            Assert.IsNotNull(e, "JC047 应有群体伤害效果");
            EffectEngine.Instance.ResolveEffect(e, card, null, a1);

            Assert.AreEqual(1, c1.Damage, "所选地区内角色各受 1 点伤害（敌方）");
            Assert.AreEqual(1, c2.Damage, "所选地区内角色各受 1 点伤害（本方）");
            Assert.AreEqual(0, other.Damage, "未选择地区的角色不受伤害");
        }

        /// <summary>连续让过直到堆叠清空（群体效果/死亡触发的结算收尾）。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 6 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        /// <summary>触发排序全部入堆叠的网关。</summary>
        private sealed class EnterAllGateway : Script.Net.IInputGateway        {
            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                if (req?.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTriggerOrder)
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                else if (req?.OptionUids != null && req.OptionUids.Count > 0)
                    r.SelectedUid = req.OptionUids[0];
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }
    }
}
