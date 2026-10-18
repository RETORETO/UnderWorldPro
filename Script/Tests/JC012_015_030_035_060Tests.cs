using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// JC012/JC015/JC030/JC035/JC060 五张卡数据层测试（文字用例→代码，2026-08-16）。
    /// 覆盖模式参照 JC001/002/003：
    ///   · 目标合法性矩阵（IsLegalActivateTarget / TargetValidity.Check）
    ///   · 目标丢失（夺控换边 / 换地区 / 翻面；含正常通过对照）
    ///   · 静止/持续授予数据层不变量（RegisterPersistent / Grant）
    ///   · 费用修正栈（ApplyCostModification / ConsumeCostModification）
    ///   · 触发条件判定（EvaluateCondition）
    ///   · 光环授予/撤销（JC060，需注入 ZoneManagerRef）
    /// </summary>

    // ==================== JC012 帷幕守望徽记（附属） ====================
    public partial class JC012Tests : TestRunner
    {
        /// <summary>构造 JC012 风格附属模板：打出目标=本方秘社；激活①(减费)、激活②(横置目标) 。</summary>
        private PlayerCardData MakeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "JC012_weimushouwanghuiji",
                CardName = "帷幕守望徽记",
                CategoryFlag = CardCategory.Attachment,
            };
            // 打出目标：仅本方秘社
            tmpl.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };
            // 激活①：一次性减费（无目标）
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC012_1_CostReduce",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Target = null,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "费用减少", "1", "帷幕守望", "一次性" } }
                },
            });
            // 激活②：横置目标角色（无视地区）
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC012_2_Tap",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色,牺牲本角色",
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    IncludeCovert = false,
                },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Tap } },
            });
            return tmpl;
        }

        private CardInstance NewInstance(PlayerCardData tmpl, string uid, int ctrl = 1)
        {
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = CardZone.AreaZone;
            inst.ZoneDetailId = "areaA";
            return inst;
        }

        private TargetSpec TapAbilityTarget() => new TargetSpec
        {
            Kind = TargetKind.Character,
            NeedsSelection = true,
            IncludeCovert = false,
        };

        public void TestJC012_T1_打出目标本方秘社()
        {
            var tmpl = MakeTemplate();
            var allowed = tmpl.GetAllowedTargets();
            Assert.IsTrue(allowed.Contains(TargetKind.Council), $"打出目标应含 Council，实际={string.Join(",", allowed)}");

            // 本方秘社合法：来源控制者=1，目标秘社控制者=1 → Friendly 通过
            var cm = new CardManager(); cm._Ready();
            var src = NewInstance(MakeTemplate(), "src", 1);
            var councilSelf = new CardInstance(new CouncilCardData { CardName = "本方秘社" }, "c1");
            councilSelf.ControllerId = 1;
            councilSelf.CurrentZone = CardZone.CouncilZone;
            Assert.IsTrue(cm.IsLegalPlayTarget(src, councilSelf), "本方秘社应为合法打出目标");

            // 敌方秘社非法：目标控制者=2 ≠ 来源1 → Friendly 不通过
            var councilEnemy = new CardInstance(new CouncilCardData { CardName = "敌方秘社" }, "c2");
            councilEnemy.ControllerId = 2;
            councilEnemy.CurrentZone = CardZone.CouncilZone;
            Assert.IsFalse(cm.IsLegalPlayTarget(src, councilEnemy), "敌方秘社不应为合法打出目标");
        }

        public void TestJC012_T2_激活二目标无视地区拒绝暗藏者()
        {
            var source = NewInstance(MakeTemplate(), "src", 1);

            // 目标卡必须用【角色卡】模板构造：若用 JC012 附属模板，MapInstanceToTargetKind 会映射为 Attachment，
            // 与激活②的 Character 目标粗筛不符而误判非法（用例修正）。
            // 不同地区角色目标 → 合法（无视地区）
            var target = NewCharTarget("tgt", 2);
            target.ZoneDetailId = "areaB";   // 不同地区
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(source, MakeTemplate().EffectDataList[1], target, null),
                "激活②对任意地区角色应合法（无视地区）");

            // 暗藏者目标 → 非法（IncludeCovert=false）
            var covert = NewCharTarget("cov", 2);
            covert.SetCovert(true);
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(source, MakeTemplate().EffectDataList[1], covert, null),
                "激活②不应以暗藏者为目标（IncludeCovert=false）");

            // 自身 → 非法
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(source, MakeTemplate().EffectDataList[1], source, null),
                "激活②不能以自身为目标");
        }

        /// <summary>构造角色卡目标（CategoryFlag=Character），避免被 MapInstanceToTargetKind 映射为附属。</summary>
        private CardInstance NewCharTarget(string uid, int ctrl)
        {
            var tmpl = new PlayerCardData { UID = uid, CardName = uid, CategoryFlag = CardCategory.Character };
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = CardZone.AreaZone;
            inst.ZoneDetailId = "areaA";
            return inst;
        }

        public void TestJC012_T3_激活一一次性费用减免()
        {
            // 用独立玩家 id 避免污染其他测试
            int pid = 77;
            EffectEngine.Instance.ApplyCostModification(pid, -1, 5, "帷幕守望", consumeOnce: true);

            Assert.AreEqual(-1, EffectEngine.Instance.GetCostModification(pid, "帷幕守望", ""), "匹配卡名应减免 1");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(pid, "其他牌", ""), "非匹配卡不应减免");

            EffectEngine.Instance.ConsumeCostModification(pid, "帷幕守望");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(pid, "帷幕守望", ""), "一次性消费后应归零");
        }

        public void TestJC012_T4_激活一无目标()
        {
            var source = NewInstance(MakeTemplate(), "src", 1);
            // 激活① Target=null：IsLegalActivateTarget 应因无 NeedsSelection 目标而返回 false（不要求选目标）
            var ability = MakeTemplate().EffectDataList[0];
            var target = NewInstance(MakeTemplate(), "tgt", 2);
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(source, ability, target, null),
                "激活①无目标描述，不应通过目标合法性判定（无目标可选）");
        }
    }

    // ==================== JC015 驱魔人（角色） ====================
    public partial class JC015Tests : TestRunner
    {
        /// <summary>构造 JC015 激活效果：消灭敌方/非人类/费≥3 角色。</summary>
        private PlayerCardData MakeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "JC015_qumoren",
                CardName = "驱魔人",
                CategoryFlag = CardCategory.Character,
            };
            // 【2026-10-18】与真实 JC015 一致：**不配卡级 TargetRules**（真实卡已移除）。
            // 激活目标精筛现在只由 `EffectData.Target.Restrictions/Param` 承担 ——
            // `IsLegalActivateTarget` 的无 TargetRules 分支自 P3·K21 起同样走
            // `TargetValidity.CheckRestrictions`，与 `MatchesRule` 同一函数 ⇒ 下方矩阵仍应逐条成立。
            // （原注释"无 TargetRules 时只做大类粗筛"已过期：给角色卡配 TargetRules 会让
            //  `GetRequiresTarget()` 误判"打出需选目标"，反而破坏 JC119 免费打出链路。）
            // 注（2026-09 用户口径）：卡文只写"非人类"→ 子类别判定走**实例当前类别**（NotCurrentSubCategory）。
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC015_QuMoRen_Activate",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色,牺牲本角色",
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = false,
                    Restrictions = new Array<TargetRestriction>
                    {
                        TargetRestriction.Enemy,
                        TargetRestriction.NotCurrentSubCategory,
                        TargetRestriction.CostAtLeast,
                    },
                    Param = "人类|3",
                },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Destroy } },
            });
            return tmpl;
        }

        private CardInstance NewInstance(string uid, int ctrl, int cost = 0, params string[] subcats)
        {
            var tmpl = new PlayerCardData { UID = uid, CardName = uid, CategoryFlag = CardCategory.Character, Cost = cost };
            foreach (var s in subcats) tmpl.SubCategories.Add(s);
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = CardZone.AreaZone;
            inst.ZoneDetailId = "areaA";
            return inst;
        }

        private EffectData ActivateAbility() => MakeTemplate().EffectDataList[0];

        /// <summary>JC015 来源：用带激活效果的模板构造（EffectDataList 非空）。
        /// 若用 NewInstance 的空模板，IsLegalActivateTarget 里 GetAllowedTargets 返回空 → 误判非法（用例修正）。</summary>
        private CardInstance NewSource(int ctrl = 1)
        {
            var inst = new CardInstance(MakeTemplate(), "驱魔人_src");
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = CardZone.AreaZone;
            inst.ZoneDetailId = "areaA";
            return inst;
        }

        public void TestJC015_T1_目标合法性矩阵()
        {
            var src = NewSource(1);
            var ability = ActivateAbility();

            // 敌方 + 非人类 + 费≥3 → 合法
            var okTarget = NewInstance("血族长老", 2, cost: 4, "非人类");
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(src, ability, okTarget, null),
                "敌方非人类费≥3 应合法");

            // 己方（Enemy 不匹配）→ 非法
            var selfSide = NewInstance("己方角色", 1, cost: 4, "非人类");
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(src, ability, selfSide, null),
                "己方目标应非法（Enemy）");

            // 人类（NotCurrentSubCategory 不匹配）→ 非法
            var human = NewInstance("人类角色", 2, cost: 4, "人类");
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(src, ability, human, null),
                "人类目标应非法（NotCurrentSubCategory：卡文『非人类』读实例当前类别）");

            // 费<3（CostAtLeast 不匹配）→ 非法
            var cheap = NewInstance("弱小角色", 2, cost: 2, "非人类");
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(src, ability, cheap, null),
                "费<3 目标应非法（CostAtLeast）");

            // 自身 → 非法
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(src, ability, src, null),
                "自身不能为目标");
        }

        public void TestJC015_T2_激活代价横置牺牲()
        {
            var ability = ActivateAbility();
            Assert.IsTrue(ability.ActivateCondition.Contains("横置本角色"), "激活代价应含横置本角色");
            Assert.IsTrue(ability.ActivateCondition.Contains("牺牲本角色"), "激活代价应含牺牲本角色");
            Assert.IsTrue(ability.Target.NeedsSelection, "激活需要选目标");
            Assert.IsFalse(ability.Target.CanTargetSelf, "不能指定自身为目标");
        }

        public void TestJC015_T3_夺控换边判丢失()
        {
            var src = NewInstance("驱魔人_src", 1);
            var ability = ActivateAbility();
            var tgt = NewInstance("血族长老", 2, cost: 4, "非人类");
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);   // 来源控制者=1

            // T3a 正常通过：目标仍敌方（2 vs 1）→ Enemy 复检通过
            var before = TargetValidity.Check(src, tgt, ability.Target, snap);
            Assert.IsTrue(before.valid, $"夺控前目标敌方应合法（reason={before.reason}）");

            // T3b 夺控判丢失：目标变己方（1 vs 1）→ Enemy 复检不通过
            tgt.ControllerId = 1;
            var after = TargetValidity.Check(src, tgt, ability.Target, snap);
            Assert.IsFalse(after.valid, "夺控换边成己方后 Enemy 复检应判丢失");
        }
    }

    // ==================== JC030 巨型蝙蝠（角色） ====================
    public partial class JC030Tests : TestRunner
    {
        /// <summary>构造 JC030 风格模板：袭击1(现身触发)+静止(不可装备结附)+条件持续(血忠诚≥2→黑眼+吸血鬼)。</summary>
        private PlayerCardData MakeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "JC030_juxingbianfu",
                CardName = "巨型蝙蝠",
                CategoryFlag = CardCategory.Character,
            };
            // JC030-A 袭击1
            var strike = new EffectData
            {
                EffectId = "JC030_A_Strike",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = true,
                    IsLocalZone = true,     // 规则⑥本地区
                },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.DealDamage, Args = new Array<string> { "1" } } },
            };
            tmpl.EffectDataList.Add(strike);
            // JC030-B 静止：不能被装备结附
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC030_B_CannotBeAttached",
                Timing = EffectTiming.Static,
                Scope = ZoneScope.AreaZone,
                StaticEffects = new Array<StaticEffect> { new StaticEffect { Kind = StaticKind.CannotBeAttached, Param = "" } },
            });
            // JC030-C 条件持续：血忠诚≥2 → 黑眼(调查+1) + 吸血鬼
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC030_C_BloodEyes",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "血领域忠诚≥2",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    Kind = PrereqKind.DomainLoyalty,
                    Domain = MagicDomain.Blood,
                    Amount = 2,
                    Zone = CardZone.AssetZone,
                },
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Attribute", Value = "调查", Amount = 1 },
                    new EffectGrant { Kind = "SubCategory", Value = "吸血鬼", Amount = 1 },
                },
            });
            return tmpl;
        }

        private CardInstance NewInstance(string uid, int ctrl = 1)
        {
            var tmpl = MakeTemplate();
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = CardZone.AreaZone;
            inst.ZoneDetailId = "areaA";
            return inst;
        }

        public void TestJC030_T1_静止不可装备结附()
        {
            var card = NewInstance("蝙蝠_a");
            Assert.IsFalse(card.HasStaticFlag(StaticKind.CannotBeAttached), "注册前不应有静止标记");
            EffectEngine.Instance.RegisterPersistent(card);
            Assert.IsTrue(card.HasStaticFlag(StaticKind.CannotBeAttached), "注册后应有 CannotBeAttached（争夺区生效）");
            EffectEngine.Instance.UnregisterPersistent(card);
            Assert.IsFalse(card.HasStaticFlag(StaticKind.CannotBeAttached), "注销后应清除静止标记");
        }

        public void TestJC030_T2_条件持续黑眼与吸血鬼()
        {
            // 数据层授予验证（与 JC001Tests 的白眼一致）：直接模拟持续授予"调查+1 + 吸血鬼类别"
            var card = NewInstance("蝙蝠_b");
            card.Grant("Attribute", "调查", 1);
            card.Grant("SubCategory", "吸血鬼", 1);
            Assert.AreEqual(1, card.GetAttribute("调查"), "黑眼应给调查+1");
            Assert.AreEqual(1, card.CurrentInvestigation, "黑眼应使基础调查+1");
            Assert.IsTrue(card.HasSubCategory("吸血鬼"), "应追加吸血鬼类别");
        }

        public void TestJC030_T3_袭击1现身触发本地区()
        {
            var ability = MakeTemplate().EffectDataList[0];
            Assert.AreEqual(EffectTiming.UncoverTrigger, ability.Timing, "袭击1为现身触发");
            Assert.IsTrue(ability.Target.NeedsSelection, "袭击1需选目标");
            Assert.IsTrue(ability.Target.CanTargetSelf, "袭击1可指定自身");
            Assert.IsTrue(ability.Target.IsLocalZone, "袭击1限定本地区（规则⑥）");
        }

        public void TestJC030_T4_袭击1换地区判丢失()
        {
            var src = NewInstance("蝙蝠_src");
            var ability = MakeTemplate().EffectDataList[0];   // 袭击1：IsLocalZone=true
            var tgt = NewInstance("袭击目标", 2);
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            // T4a 正常通过：目标留发动地
            tgt.ZoneDetailId = "areaA";
            var ok = TargetValidity.Check(src, tgt, ability.Target, snap);
            Assert.IsTrue(ok.valid, $"袭击1 目标留本地区应合法（reason={ok.reason}）");

            // T4b 换地区判丢失：目标移出发动地
            tgt.ZoneDetailId = "areaB";
            var lost = TargetValidity.Check(src, tgt, ability.Target, snap);
            Assert.IsFalse(lost.valid, "袭击1 目标移出发动地应判丢失（规则⑥）");
            Assert.IsTrue(lost.reason == TargetLossReason.OutOfLocalZone, $"丢失原因应为 OutOfLocalZone，实际={lost.reason}");
        }

        public void TestJC030_T5_袭击1目标翻面判丢失()
        {
            var src = NewInstance("蝙蝠_src");
            var ability = MakeTemplate().EffectDataList[0];   // IncludeCovert 未设=false
            var tgt = NewInstance("袭击目标", 2);
            tgt.ZoneDetailId = "areaA";
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            // 翻面前正面角色：合法
            Assert.IsTrue(TargetValidity.Check(src, tgt, ability.Target, snap).valid, "翻面前正面角色应合法");

            // 翻面成暗藏者：IncludeCovert=false → CheckZoneAndFace 判 COVERT 丢失
            tgt.SetCovert(true);
            var lost = TargetValidity.Check(src, tgt, ability.Target, snap);
            Assert.IsFalse(lost.valid, "袭击1 目标翻面成暗藏者应判丢失（IncludeCovert=false）");
        }
    }

    // ==================== JC035 川美魔咒（附属·结附地区） ====================
    public partial class JC035Tests : TestRunner
    {
        private PlayerCardData MakeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "JC035_chuanmeimozhou",
                CardName = "川美魔咒",
                CategoryFlag = CardCategory.Attachment,
            };
            tmpl.TargetRules = new Array<TargetRule> { TargetRule.Make(TargetKind.Area) };
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC035_Prepare_PlacePowerMark",
                Timing = EffectTiming.OnTurnStarted,
                Scope = ZoneScope.AttachTarget,
                Condition = new EffectCondition { Predicate = "OpponentHandLe3" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PlaceToken, Args = new Array<string> { "势力标志", "1" } },
                },
            });
            return tmpl;
        }

        public void TestJC035_T1_打出目标地区()
        {
            var tmpl = MakeTemplate();
            var allowed = tmpl.GetAllowedTargets();
            Assert.IsTrue(allowed.Contains(TargetKind.Area), $"打出目标应含 Area，实际={string.Join(",", allowed)}");
        }

        public void TestJC035_T2_触发条件对手手牌3()
        {
            // 直接验证谓词判定（数据层，不依赖完整手牌环境）
            // 用可控 source：控制者=1，对手=2。手牌数通过 EvaluateCondition 内部读 CardManager，
            // 这里以 source 非空为前提，验证谓词名存在且走 OpponentHandLe3 分支逻辑。
            var card = new CardInstance(MakeTemplate(), "jc035_a");
            card.ControllerId = 1;
            // 不实际构造手牌，仅确认条件分支可调用（对手手牌>3 时返回 false）
            bool met = EffectEngine.Instance.EvaluateCondition("OpponentHandLe3", card, null);
            // 由于测试环境无 CardManager 实例注入，GetHandCardsByOwner 返回 0（≤3），应为 true
            Assert.IsTrue(met, "对手手牌(0)≤3，OpponentHandLe3 条件应满足");
        }

        public void TestJC035_T3_生效区仅附属区()
        {
            var tmpl = MakeTemplate();
            var ability = tmpl.EffectDataList[0];
            Assert.AreEqual(ZoneScope.AttachTarget, ability.Scope, "触发效果 Scope 应为 AttachTarget（Z1）");

            // 结附区（AttachedZone）生效
            var attached = new CardInstance(tmpl, "jc035_att");
            attached.ControllerId = 1; attached.OwnerId = 1;
            attached.CurrentZone = CardZone.AttachedZone;
            Assert.IsTrue(EffectEngine.IsEffectActiveInZone(ability, attached), "附属在 AttachedZone 应生效");

            // 未结附（AreaZone）不生效
            var area = new CardInstance(tmpl, "jc035_area");
            area.ControllerId = 1; area.OwnerId = 1;
            area.CurrentZone = CardZone.AreaZone;
            Assert.IsFalse(EffectEngine.IsEffectActiveInZone(ability, area), "附属在 AreaZone（未结附）不应生效");
        }
    }

    // ==================== JC060 神秘学顾问（角色·光环） ====================
    public partial class JC060Tests : TestRunner
    {
        private PlayerCardData MakeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "JC060_shenmixueguwen",
                CardName = "神秘学顾问",
                CategoryFlag = CardCategory.Character,
            };
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC060_Aura",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                IsAura = true,
                AuraRequiresNoDomain = true,
                AuraFilterExcludeDomain = MagicDomain.Mind,
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "心", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "战斗", Amount = 1 },
                    new EffectGrant { Kind = "MagicDomain", Value = "Mind", Amount = 1 },
                },
            });
            return tmpl;
        }

        private void 搭建光环环境(out CardInstance src, out CardInstance noDomain, out CardInstance hasDomain, out CardInstance enemy)
        {
            var cm = new CardManager(); cm._Ready();
            var zm = new ZoneManager(); zm.Initialize(cm, null);   // 无牌库管理器
            cm.SetZoneManager(zm);
            // 注入引擎引用（光环 ApplyAura 需 ZoneManagerRef；还原需 CardManagerRef）
            EffectEngine.Instance.ZoneManagerRef = zm;
            EffectEngine.Instance.CardManagerRef = cm;

            // 源卡（JC060 自身，无领域）
            src = new CardInstance(MakeTemplate(), "jc060_src");
            src.OwnerId = 1; src.ControllerId = 1; src.CurrentZone = CardZone.Hand;
            // 无领域本方角色 A
            noDomain = new CardInstance(new PlayerCardData { UID = "a", CardName = "A", CategoryFlag = CardCategory.Character }, "a");
            noDomain.OwnerId = 1; noDomain.ControllerId = 1; noDomain.CurrentZone = CardZone.Hand;
            // 已有魔法领域(心灵)本方角色 B
            // 注：CardInstance 构造函数不把模板 MagicDomains 同步到实例（仅 CurrentSubCategories 会），
            // 必须手动给实例加领域，否则 AuraRequiresNoDomain 会把 B 误判为"无领域"而被光环授予（用例修正）。
            var bTmpl = new PlayerCardData { UID = "b", CardName = "B", CategoryFlag = CardCategory.Character };
            bTmpl.MagicDomains.Add(MagicDomain.Mind);
            hasDomain = new CardInstance(bTmpl, "b");
            hasDomain.MagicDomains.Add(MagicDomain.Mind);
            hasDomain.OwnerId = 1; hasDomain.ControllerId = 1; hasDomain.CurrentZone = CardZone.Hand;
            // 敌方角色 C
            enemy = new CardInstance(new PlayerCardData { UID = "c", CardName = "C", CategoryFlag = CardCategory.Character }, "c");
            enemy.OwnerId = 2; enemy.ControllerId = 2; enemy.CurrentZone = CardZone.Hand;

            // 全部从手牌进争夺区（避免 Deck→RemoveFromAllZones 调 null 牌库 NRE）
            foreach (var c in new[] { src, noDomain, hasDomain, enemy })
            {
                c.CurrentZone = CardZone.Hand;
                zm.MoveToArea(c, "areaA", c.ControllerId);
            }
        }

        public void TestJC060_T1_光环授予()
        {
            搭建光环环境(out var src, out var noDomain, out var hasDomain, out var enemy);

            EffectEngine.Instance.RegisterPersistent(src);

            // 无领域本方角色 A：获得心图标、战斗+1、魔法领域 Mind
            Assert.IsTrue(noDomain.HasIcon("心"), "无领域角色应获得【心】图标");
            Assert.AreEqual(1, noDomain.GetAttribute("战斗"), "无领域角色应战斗+1（黑刀）");
            Assert.IsTrue(noDomain.HasMagicDomain(MagicDomain.Mind), "无领域角色应获得心灵领域");
            Assert.AreEqual(1, noDomain.CurrentStrength, "无领域角色 CurrentStrength 应+1");

            // 已有领域角色 B：不受影响（不追加心/战斗；其固有心灵领域保持）
            Assert.IsFalse(hasDomain.HasIcon("心"), "已有领域角色不应获得【心】");
            Assert.AreEqual(0, hasDomain.GetAttribute("战斗"), "已有领域角色不应战斗+1");
            Assert.IsTrue(hasDomain.HasMagicDomain(MagicDomain.Mind), "已有领域角色固有心灵领域保持不变");

            // 敌方角色 C：不受影响（不同控制者区域）
            Assert.IsFalse(enemy.HasIcon("心"), "敌方角色不应获得光环");
        }

        public void TestJC060_T2_光环撤销()
        {
            搭建光环环境(out var src, out var noDomain, out var hasDomain, out var enemy);
            EffectEngine.Instance.RegisterPersistent(src);
            Assert.IsTrue(noDomain.HasIcon("心"), "前置：授予应生效");

            EffectEngine.Instance.UnregisterPersistent(src);
            Assert.IsFalse(noDomain.HasIcon("心"), "撤销后应移除【心】图标");
            Assert.AreEqual(0, noDomain.GetAttribute("战斗"), "撤销后应还原战斗");
            Assert.IsFalse(noDomain.HasMagicDomain(MagicDomain.Mind), "撤销后应移除运行时授予的心灵领域");
        }

        public void TestJC060_T3_源卡自身不授予()
        {
            搭建光环环境(out var src, out var noDomain, out var hasDomain, out var enemy);
            EffectEngine.Instance.RegisterPersistent(src);
            // 源卡 JC060 自身（无领域）不应被自身光环授予
            Assert.IsFalse(src.HasIcon("心"), "源卡自身不应获得光环授予（ApplyAura 排除源卡）");
            Assert.AreEqual(0, src.GetAttribute("战斗"), "源卡自身不应战斗+1");
        }
    }
}
