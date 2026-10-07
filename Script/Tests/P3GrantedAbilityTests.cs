using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K13：**运行期授予的激活能力** → JC019 弥尔顿的镇魔枪】
    /// 卡文：结附于目标角色 持续：受此结附的角色获得［黑刀］，并获得"快速行动，横置：此角色对位于本地区的
    ///      目标非人类角色造成1点伤害，该伤害不能被防止"的能力。
    /// FAQ：使用该能力需要横置**它所结附的角色**，而非武器本身。
    /// 口径（用户 2026-09）：宿主原本就有激活效果时，**在既有的多激活效果选择窗口里额外增列 1 项**。
    ///
    /// 交付：`EffectData.GrantedAbility` + `EffectEngine.ReapplyGrantedAbilities()`（统一重算）
    ///  · 候选/高亮/权威门禁与卡面固有激活能力**同一套**（`ActivateEligibility.GetAllActivateAbilities`
    ///    把运行期授予项并入 → 选择窗口自然"增列 1 项"）；
    ///  · 能力以**宿主**为来源 → `ActivateCondition="横置本角色"` 横置的是宿主（FAQ）；
    ///  · `DealDamage{"1","不可防止"}` → 跳过"防止"（灵体/护盾）直接落地。
  /// </summary>
    public class P3GrantedAbilityTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 1321 };
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

        /// <summary>角色探针；`subCategory` 决定是否"非人类"。</summary>
        private static PlayerCardData Probe(string uid, string subCategory = "人类", int defense = 5)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = defense,
            };
            if (!string.IsNullOrEmpty(subCategory)) tmpl.SubCategories.Add(subCategory);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>宿主探针：自带一条可发动的激活能力（用于验证"选择窗口增列 1 项"）。</summary>
        private static PlayerCardData HostWithAbility(string uid)
        {
            var tmpl = Probe(uid);
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = uid + "_own",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Forced = false,
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Godot.Collections.Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Godot.Collections.Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "宿主自带能力",
            });
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? Areas(game)[0], attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
            EffectEngine.Instance.ReapplyGrantedAbilities();   // 与生产路径一致：登记后重算运行期授予
        }

        private static CardInstance PlaceSpear(GameInitializer game, CardInstance host, string area)
        {
            var spear = game.CardManager.CreateCardInstance(Load("JC019_mierdundezhenmoqiang"), 1);
            game.ZoneManager.MoveToArea(spear, area, 1);
            Attach(game, host, spear);
            return spear;
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

        public void TestGranted_JC019结构()
        {
            var tmpl = Load("JC019_mierdundezhenmoqiang");
            Assert.IsNotNull(tmpl, "JC019 模板应可加载");
            var e = tmpl.EffectDataList[0];
            Assert.IsNotNull(e.GrantedAbility, "持续效果应声明运行期授予的激活能力");
            Assert.IsTrue(e.GrantToHost, "授予落点＝宿主（结附的角色）");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.GrantedAbility.Timing, "授予的是激活能力");
            Assert.AreEqual("横置本角色", e.GrantedAbility.ActivateCondition, "代价＝横置本角色（宿主，见 FAQ）");
            Assert.AreEqual("快速", e.GrantedAbility.ActivateWindow, "快速行动");
            Assert.IsTrue(e.GrantedAbility.Target.IsLocalZone, "目标限本地区");
            Assert.AreEqual(TargetRestriction.NotCurrentSubCategory, e.GrantedAbility.Target.Restrictions[0],
                "目标须『非人类』（读实例当前类别；用户 2026-09 口径）");
            Assert.AreEqual("人类", e.GrantedAbility.Target.Param, "非人类＝排除『人类』");
            Assert.AreEqual("不可防止", e.GrantedAbility.Actions[0].Args[1], "该伤害不能被防止");
            // 黑刀要"两条通道都写"：Icon（存在性/展示）+ Attribute:战斗（数值）
            Assert.AreEqual(2, e.ContinuousGrants.Count, "黑刀＝图标通道 + 属性通道各一条");
            Assert.AreEqual("Icon", e.ContinuousGrants[0].Kind, "第一条＝图标存在性");
            Assert.AreEqual("Attribute", e.ContinuousGrants[1].Kind, "第二条＝属性数值（黑刀=+1 战力）");
        }

        // ==================== ② 宿主获得能力 + 黑刀 ====================

        public void TestGranted_宿主获得能力与黑刀()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, HostWithAbility("TS_ga_host"), 1, areas[0]);
            int strengthBefore = host.CurrentStrength;

            PlaceSpear(game, host, areas[0]);

            Assert.AreEqual(strengthBefore + 1, host.CurrentStrength, "宿主获得［黑刀］（战斗 +1）");
            Assert.IsTrue(host.HasIcon("黑刀"), "宿主同时获得『黑刀』图标本身（展示/存在性通道）");
            var abilities = ActivateEligibility.GetAllActivateAbilities(host);
            Assert.AreEqual(2, abilities.Count, "宿主自带 1 条 + 运行期授予 1 条 = 2 条（选择窗口自然增列 1 项）");
            Assert.IsTrue(abilities.Exists(a => a.EffectId == "JC019_G_HostActivatedSmite"), "授予的那条在候选里");
        }

        // ==================== ③ 发动：横置宿主 + 不可防止的 1 点伤害 ====================

        public void TestGranted_发动横置宿主并造成不可防止伤害()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_ga_host2"), 1, areas[0]);
            var target = OnField(game, Probe("TS_ga_target", "不死生物"), 2, areas[0]);
            PlaceSpear(game, host, areas[0]);
            var ability = EffectEngine.Instance.GetGrantedAbilities(host)[0];
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            // 让目标处于"伤害会被防止"的状态：给它一个伤害护盾（普通伤害会被防止，本能力的伤害不会）
            EffectEngine.Instance.RegisterDamageShield(target);

            EffectEngine.Instance.ActivateAbility(host, ability, target);

            Assert.IsTrue(host.IsTapped, "代价：**横置宿主**（而非武器本身，符合 FAQ）");
            Assert.IsFalse(target.IsDead(), "前置：目标未被直接打死（1 点伤害对 5 防御角色）");
            // 堆叠结算
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Assert.AreEqual(1, target.Damage, "『不可防止』→ 伤害穿透护盾直接落地（护盾没有拦下它）");
        }

        // ==================== ④ 对照：普通伤害会被护盾防止 ====================

        public void TestGranted_对照护盾能拦普通伤害()
        {
            var game = NewGame();
            var areas = Areas(game);
            var victim = OnField(game, Probe("TS_ga_shield"), 2, areas[0]);
            EffectEngine.Instance.RegisterDamageShield(victim);

            game.CardManager.DealDamageToInstance(victim, 2);

            Assert.AreEqual(0, victim.Damage, "对照：普通伤害被护盾防止（证明上一用例确实走的是『不可防止』通道）");
        }

        // ==================== ⑤ 解附/武器离场 → 授予的能力收回 ====================

        public void TestGranted_解附后能力收回()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, HostWithAbility("TS_ga_host3"), 1, areas[0]);
            var spear = PlaceSpear(game, host, areas[0]);
            Assert.AreEqual(2, ActivateEligibility.GetAllActivateAbilities(host).Count, "前置：2 条能力");

            game.ZoneManager.MoveToHand(spear, 1);   // 武器离场（解附）

            var abilities = ActivateEligibility.GetAllActivateAbilities(host);
            Assert.AreEqual(1, abilities.Count, "武器离场 → 运行期授予的能力收回，只剩宿主自带的 1 条");
            Assert.IsFalse(abilities.Exists(a => a.EffectId == "JC019_G_HostActivatedSmite"), "授予的那条已消失");
        }

        // ==================== ⑥ 双击入口（2026-09 修复·用户实测"宿主高亮但点击/拖拽都无反馈"） ====================

        /// <summary>
        /// 【缺陷】单机 Arena 的双击转发器此前 `if (!IsAttached) return;` ⇒ **争夺区正面角色**（如 JC019 的宿主）
        /// 双击根本进不来；而"能力收集"又只读模板 `EffectDataList`（运行期授予项不在里面）⇒ 宿主高亮却怎么点都无反应。
        /// 修复：来源判定改用 `ActivateEligibility.IsDoubleClickActivationSource`（附属 / 争夺区正面角色 / 手牌行动卡），
        /// 能力收集改用 `GetAllActivateAbilities`（模板 ∪ 授予）。
        /// </summary>
        public void TestGranted_宿主角色可作为双击激活来源()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_ga_dbl"), 1, areas[0]);   // 宿主**无**自带激活能力
            Assert.AreEqual(0, ActivateEligibility.GetAllActivateAbilities(host).Count, "前置：宿主模板无激活能力");
            Assert.IsTrue(ActivateEligibility.IsDoubleClickActivationSource(host),
                "争夺区正面角色属于可双击激活的来源（旧实现被 `!IsAttached` 拦掉）");

            PlaceSpear(game, host, areas[0]);

            Assert.IsTrue(ActivateEligibility.IsDoubleClickActivationSource(host), "获得授予能力后仍是合法来源");
            var abilities = ActivateEligibility.GetAllActivateAbilities(host);
            Assert.AreEqual(1, abilities.Count, "宿主原本没有效果 ⇒ 选择内容只有 JC019 赋予的这 1 条");
            Assert.AreEqual("JC019_G_HostActivatedSmite", abilities[0].EffectId, "即 JC019 赋予的那条");
        }

        /// <summary>
        /// 【缺陷·同一入口】JC024 董事局「快速行动2：将 S．P．T 董事局潜伏」是**自我指代、不需要目标**：
        /// 争夺区角色只能靠拖拽发动（`CardDragController` 要求拖到目标上），无目标能力因此**完全发不动**。
        /// 现由双击入口承担：来源判定为真 + 能力收集到 1 条 + `NeedsSelection=false` ⇒ 直接发动、不弹目标窗。
        /// </summary>
        public void TestGranted_无目标自指激活可双击发动()
        {
            var game = NewGame();
            var areas = Areas(game);
            var tmpl = Load("JC024_SPTdongshiju");
            var card = OnField(game, tmpl, 1, areas[0]);
            var ability = FindEffect(tmpl, "JC024_2_LatentThenFreeUncoverNextPrepare");

            Assert.IsNotNull(ability, "JC024 应有『潜伏自身』的激活能力");
            Assert.IsTrue(ActivateEligibility.IsDoubleClickActivationSource(card), "争夺区正面角色可双击");
            Assert.IsFalse(ability.Target.NeedsSelection, "自我指代 ⇒ 不需要取目标（旧交互只能拖拽、无目标可拖 ⇒ 发不动）");
            Assert.AreEqual(TargetKind.None, ability.Target.Kind, "目标声明＝None（自我指代）");
            Assert.IsTrue(ability.Actions[0].Args[0] == "自身", "潜伏对象＝自身");

            // 双击入口：单条能力 + 不需要目标 ⇒ 直接发动（等价于 Arena 的 BeginActivate(inst, only, null, -1)）
            GiveMana(game, 1, ability.Cost);   // 快速行动2：需要 2 点费用
            game.TurnManager.PriorityPlayerId = 1;
            EffectEngine.Instance.ActivateAbility(card, ability, null, -1);
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsTrue(card.IsCovert, "结算后已潜伏（自我指代生效）");
        }

        // ==================== ⑦ "目标含自身"的激活目标（A62 在激活路径的收口） ====================

        /// <summary>
        /// 【缺陷】`IsLegalActivateTarget` 无条件 `source == target → false`、`HasLegalTarget` 也无条件跳过来源卡，
        /// 与候选枚举（按 `Target.CanTargetSelf` 放行）**口径分叉** ⇒ 当"本地区唯一合法对象就是自己"时
        /// （JC003 独自在场、JC019 宿主独自在场）能力被判"无合法目标"，UI 置灰/双击被 `CanActivate` 拦下。
        /// </summary>
        public void TestGranted_目标含自身时可取自己且能发动()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_ga_self", "不死生物"), 1, areas[0]);   // 本地区**只有它自己**；非人类（本能力目标限定）
            PlaceSpear(game, host, areas[0]);
            var ability = EffectEngine.Instance.GetGrantedAbilities(host)[0];
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            Assert.IsTrue(ability.Target.CanTargetSelf, "前置：JC019 授予能力的『目标非人类角色』含自身（用户确认）");
            Assert.IsFalse(host.IsTapped, "前置：宿主未横置");
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(host, ability, host, game.CardManager, game.PlayerManager),
                "取自身为激活目标必须合法");
            Assert.IsTrue(ActivateEligibility.HasLegalTarget(host, ability, game.CardManager, game.PlayerManager),
                "独自在场时也必须判定为『存在合法目标』");
            Assert.IsTrue(ActivateEligibility.CanActivate(host, game.TurnManager, game.PlayerManager, game.CardManager),
                "⇒ 能力可发动（旧实现此处为 false，宿主高亮却点不动）");

            int before = host.Damage;
            EffectEngine.Instance.ActivateAbility(host, ability, host);
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsTrue(host.IsTapped, "代价：横置宿主");
            Assert.AreEqual(before + 1, host.Damage, "独自在场 ⇒ 取自身为目标，对自己造成 1 点伤害");
        }

        // ==================== ⑨ 联机权威路径取数（2026-09 用户实测） ====================

        /// <summary>
        /// 【用户实测】"JC019 结附后，宿主使用效果，弹出提示框操作被服务端拒绝：**发动能力参数无效**"。
        /// 根因：联机权威路径 `GodotIntentRouter.ActivateAbility` 用 `CardManager.GetEffectData(uid, effectId)`
        /// 按 id 找效果，而该方法只查**模板/秘社**的 `EffectDataList` ⇒ 运行期授予的能力（JC019 给宿主的）
        /// 取不到 → 抛"发动能力参数无效"（客户端弹了窗、点了能力却被拒）。
        /// 修复：`GetEffectData` 补查运行期授予项（与候选枚举/门禁同一入口）。
        /// </summary>
        public void TestGranted_权威路径可按id取到授予的能力()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_ga_lookup", "不死生物"), 1, areas[0]);
            PlaceSpear(game, host, areas[0]);

            var granted = EffectEngine.Instance.GetGrantedAbilities(host);
            Assert.IsTrue(granted.Count > 0, "前置：宿主获得运行期授予的能力");
            string id = granted[0].EffectId;
            Assert.AreEqual("JC019_G_HostActivatedSmite", id, "前置：授予的能力 id");

            var byId = game.CardManager.GetEffectData(host.UID, id);
            Assert.IsNotNull(byId,
                "联机权威路径必须能按 id 取到**运行期授予**的能力（否则报『发动能力参数无效』）");
            Assert.AreEqual(id, byId.EffectId, "取到的就是该授予项");
        }

        // ==================== ⑧ 拖拽 / 双击的分工（2026-09 用户实测修复） ====================

        /// <summary>
        /// 【用户实测】"JC019 角色高亮了但是拖拽无反馈"、"JC024 UI 上依然是拖拽 UI，无法正常使用效果"、
        /// "结附给 JC024 后有两个激活效果，但双击没有弹出选择效果界面（双击被拖拽覆盖）"。
        /// 规则（单一实现 `ActivateEligibility.CanDragActivate`）：**拖拽只能表达"恰好 1 条能力 ＋ 需选场上卡目标"**；
        /// 其余（多能力 ⇒ 双击弹效果菜单；无目标/自指 ⇒ 双击直接发动；玩家/地区目标 ⇒ 各自专用窗口）
        /// 一律**不进入拖拽**，从而不吞左键、让双击信号送达卡 UI。
        /// 同时 `GetFirstActivateAbility` 必须**含运行期授予项**（否则 JC019 宿主的拖拽取不到能力＝无反馈）。
        /// </summary>
        public void TestGranted_拖拽只表达单条需卡目标的能力()
        {
            var game = NewGame();
            var areas = Areas(game);

            // ① JC019 宿主（只有授予的"需选角色目标"能力）⇒ 可用拖拽，且取到的就是授予的那条
            var host = OnField(game, Probe("TS_ga_drag", "不死生物"), 1, areas[0]);
            OnField(game, Probe("TS_ga_drag_t", "不死生物"), 2, areas[0]);   // 给个合法目标
            PlaceSpear(game, host, areas[0]);

            Assert.AreEqual(1, ActivateEligibility.GetAllActivateAbilities(host).Count, "宿主只有授予的 1 条能力");
            Assert.AreEqual("JC019_G_HostActivatedSmite",
                ActivateEligibility.GetFirstActivateAbility(host)?.EffectId,
                "首条能力取法必须包含**运行期授予**（旧实现只读模板 ⇒ 拖拽时 ability=null ⇒ 松手判『无合法目标』取消）");
            Assert.IsTrue(ActivateEligibility.CanDragActivate(host, out var dragAbility), "单条 ＋ 需选卡目标 ⇒ 可拖拽");
            Assert.AreEqual("JC019_G_HostActivatedSmite", dragAbility?.EffectId, "拖拽用的就是授予的那条");

            // ② 宿主再自带一条激活能力（＝JC024 那种"自带 + 授予"的两条）⇒ **不可拖拽**（交给双击菜单）
            var host2 = OnField(game, HostWithAbility("TS_ga_drag2"), 1, areas[0]);
            PlaceSpear(game, host2, areas[0]);
            Assert.AreEqual(2, ActivateEligibility.GetAllActivateAbilities(host2).Count, "前置：宿主自带 1 条 + 授予 1 条");
            Assert.IsFalse(ActivateEligibility.CanDragActivate(host2, out _),
                "多条能力 ⇒ 不进拖拽（否则吞掉左键，双击无法弹出效果选择窗）");

            // ③ JC024「将自身潜伏」＝自我指代、不需要目标 ⇒ **不可拖拽**（双击直接发动）
            var jc024 = OnField(game, Load("JC024_SPTdongshiju"), 1, areas[0]);
            Assert.IsFalse(ActivateEligibility.CanDragActivate(jc024, out _),
                "无目标的自指激活 ⇒ 不进拖拽（拖拽无目标可落 ⇒ 松手取消＝用户报的『拖拽 UI 无法正常使用』）");

            // ④ 对照：普通"单条需选卡目标"的角色能力仍走拖拽（既有交互不变）
            var tapLike = OnField(game, Probe("TS_ga_drag3"), 1, areas[0]);
            tapLike.PlayerTemplate.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_ga_drag3_ability",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
            });
            Assert.IsTrue(ActivateEligibility.CanDragActivate(tapLike, out _), "单条 ＋ 需选卡目标 ⇒ 照常可拖拽");
        }

        /// <summary>对照：**未**声明 `CanTargetSelf` 的能力不能取来源卡自身（不因修复而放宽）。</summary>
        public void TestGranted_未声明含自身时仍不能取自己()
        {
            var game = NewGame();
            var areas = Areas(game);
            var probe = OnField(game, Probe("TS_ga_noself"), 1, areas[0]);
            var ability = new EffectData
            {
                EffectId = "TS_ga_noself_ability",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true, IsLocalZone = true },
            };
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(probe, ability, probe, game.CardManager, game.PlayerManager),
                "未声明 CanTargetSelf ⇒ 不能取自身");
            Assert.IsFalse(ActivateEligibility.HasLegalTarget(probe, ability, game.CardManager, game.PlayerManager),
                "本地区只有它自己且不能取自身 ⇒ 无合法目标");
        }

        // ==================== ⑨ 服务端投影必须含"运行期授予"的能力（联机拖拽回归） ====================

        /// <summary>
        /// 【用户实测】"JC019 让宿主获得效果，现在变回**无法拖拽**的形式了"。
        /// 根因（联机/纯客户端）：客户端要判"能否拖拽、用哪条能力"走 `ActivateEligibility.GetAllActivateAbilities`，
        /// 而它读的是**服务端才有的**运行期授予表（`EffectEngine.GetGrantedAbilities`）⇒ 客户端只看到模板项，
        /// JC019 授予宿主的能力**不可见**（本地能力数 0 ⇒ 拖拽起手直接 return false）；
        /// 而服务端的合法性投影 `LegalityProjection` 也只枚举模板效果 ⇒ 投影里同样没有该能力。
        /// 修复：① 投影改用 `GetAllActivateAbilities`（模板 ∪ 授予）；
        ///       ② 新增 `GameViewSnap.ActivationAbilities`（含 `Draggable` 结论），客户端据此起手拖拽。
        /// </summary>
        public void TestGranted_服务端投影含授予的能力与可拖拽结论()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_ga_proj", "不死生物"), 1, areas[0]);
            var victim = OnField(game, Probe("TS_ga_proj_t", "不死生物"), 2, areas[0]);
            PlaceSpear(game, host, areas[0]);

            var view = Script.Manager.LegalityProjection.Build(game.CardManager, game.PlayerManager,
                game.TurnManager, game.ZoneManager, 1);
            Assert.IsNotNull(view, "应生成投影");

            // ① 能力清单含"运行期授予"的那条，且带"可拖拽"结论
            Script.Net.ActivationAbilitySnap entry = null;
            foreach (var a in view.ActivationAbilities)
                if (a != null && a.SourceUid == host.UID && a.EffectId == "JC019_G_HostActivatedSmite") entry = a;
            Assert.IsNotNull(entry, "投影必须下发**运行期授予**的能力（否则客户端看不到它）");
            Assert.IsTrue(entry.Draggable, "宿主只有这 1 条能力 ＋ 需选场上卡目标 ⇒ 服务端应判『可拖拽』");
            Assert.IsTrue(entry.NeedsSelection, "该能力需要选目标");
            Assert.AreEqual((int)TargetKind.Character, entry.TargetKindValue, "目标大类＝角色");

            // ② 该能力的合法目标也要投影（客户端拖拽期间的高亮/落点全靠它）
            Script.Net.AbilityTargetSnap tgt = null;
            foreach (var t in view.AbilityTargets)
                if (t != null && t.SourceUid == host.UID && t.EffectId == "JC019_G_HostActivatedSmite") tgt = t;
            Assert.IsNotNull(tgt, "投影必须含该能力的合法目标条目（旧实现只有模板效果 ⇒ 0 条）");
            Assert.IsTrue(tgt.TargetUids.Contains(victim.UID), "敌方非人类角色应是合法落点");
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>给玩家 n 点可用费用（资产区放 n 张费用探针，与其它套件同款）。</summary>
        private static void GiveMana(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var asset = game.CardManager.CreateCardInstance(
                    Probe($"TS_ga_mana_{playerId}_{i}", "", 1), playerId);
                asset.PlayerTemplate.CategoryFlag = CardCategory.Attachment;
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }
    }
}
