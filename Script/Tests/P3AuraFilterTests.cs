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
    /// 【P3·E】光环筛选扩展（按用户 2026-09 口径实现并锁定）：
    ///  · **组织筛选** `AuraFilterOrganization`（JC051 鸣钟教派 → 白星）；
    ///  · **关键词筛选** `AuraFilterKeyword`（JC105 灵体 → 白星）；
    ///  · **仅本地区** `AuraLocalZoneOnly` + **防御力授予**（JC059 本地区其他本方角色 +1防御）；
    ///  · 范围口径：**未写"本地区"的光环＝来源控制者的全部争夺区角色**（JC038/JC051/JC105）；写了的只影响本地区（JC059）。
    ///  · 既有领域筛选（JC060）行为不变（三处判定统一走 `IsAuraTargetEligible`）。
    /// </summary>
    public class P3AuraFilterTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 67 };
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

        /// <summary>探针卡：`inherentKeyword` 通过"自身持续效果授予该关键字"来表达（= 卡面固有声明）。</summary>
        private static PlayerCardData Probe(string uid, string org = null, string inherentKeyword = null,
            CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 2,
            };
            if (org != null) tmpl.Organization = org;
            if (inherentKeyword != null)
            {
                // 卡面固有词条＝该卡自带的持续授予（`HasInherentKeyword` 从 EffectDataList 的 Continuous 授予判定）
                tmpl.EffectDataList.Add(new EffectData
                {
                    EffectId = uid + "_inherent",
                    Timing = EffectTiming.Continuous,
                    ContinuousPrereq = "常驻",
                    ContinuousEffect = $"关键字：{inherentKeyword}",
                    ContinuousGrants = new Array<EffectGrant>
                    {
                        new EffectGrant { Kind = "Keyword", Value = inherentKeyword, Amount = 1 },
                    },
                    Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                });
            }
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

        // ==================== ① 结构 ====================

        public void TestAuraFilter_结构()
        {
            var e51 = FindEffect(Load("JC051_nuoennagesita"), "JC051_B_AuraWhiteStarToSect");
            Assert.IsNotNull(e51, "JC051 应有所属组织光环效果");
            Assert.IsTrue(e51.IsAura, "是光环");
            Assert.AreEqual("鸣钟教派", e51.AuraFilterOrganization, "组织筛选＝鸣钟教派");
            Assert.IsFalse(e51.AuraLocalZoneOnly, "未写『本地区』→ 全场本方");
            Assert.AreEqual("白星", e51.ContinuousGrants[0].Value, "授予白星");

            var e105 = FindEffect(Load("JC105_emenglingzhu"), "JC105_A_AuraWhiteStarToSpirits");
            Assert.IsNotNull(e105, "JC105 应有灵体光环效果");
            Assert.AreEqual("灵体", e105.AuraFilterKeyword, "关键词筛选＝灵体");

            var e59 = FindEffect(Load("JC059_anquanbaoweibumen"), "JC059_A_AuraDefenseLocalZone");
            Assert.IsNotNull(e59, "JC059 应有防御力光环效果");
            Assert.IsTrue(e59.AuraLocalZoneOnly, "只影响本地区");
            Assert.AreEqual("Attribute", e59.ContinuousGrants[0].Kind, "以 Attribute 授予防御力");
            Assert.AreEqual("防御", e59.ContinuousGrants[0].Value, "防御");
        }

        // ==================== ② 组织筛选：只有同组织角色获得 ====================

        public void TestAuraFilter_按组织()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];

            var src = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(src, a1, 1);
            var sectMate = game.CardManager.CreateCardInstance(Probe("TS_af_sect", "鸣钟教派"), 1);
            game.ZoneManager.MoveToArea(sectMate, a2, 1);      // 另一地区 → 仍应受影响（全场本方）
            var outsider = game.CardManager.CreateCardInstance(Probe("TS_af_other", "王座会"), 1);
            game.ZoneManager.MoveToArea(outsider, a1, 1);
            var enemySect = game.CardManager.CreateCardInstance(Probe("TS_af_enemy", "鸣钟教派"), 2);
            game.ZoneManager.MoveToArea(enemySect, a1, 2);

            EffectEngine.Instance.RegisterPersistent(src);

            Assert.IsTrue(sectMate.HasIcon("白星"), "同组织的**本方**角色获得白星（不同地区也算，全场本方）");
            Assert.IsFalse(outsider.HasIcon("白星"), "不同组织（王座会）不受影响");
            Assert.IsFalse(enemySect.HasIcon("白星"), "**对手**的同组织角色不受影响（本方光环）");
            Assert.IsFalse(src.HasIcon("白星"), "源卡自身不获得（『其他』）");
        }

        // ==================== ④ 自愈：运行期授予被清空（回合切换/状态重置）后重算必须补回 ====================

        /// <summary>【2026-09 用户实测 JC051】"第一个回合能正常显示，再过两回合先手交移回来了没有正常显示"。
        /// 机制：`ReevaluateAuras` 有"自愈补授"（对已登记目标逐条校验授予是否仍在，缺失则重授），
        /// 但只在被调用时生效；而"换区/状态重置"会清空运行期授予（`ResetToBaseState`）。
        /// 本用例把这个现象钉死：清空后**只要重算**，白星（图标 + 先手势力数值）必须回来。</summary>
        public void TestAuraFilter_授予被清空后重算能补回()
        {
            var game = NewGame();
            var areas = Areas(game);

            var src = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(src, areas[0], 1);
            var mate = game.CardManager.CreateCardInstance(Probe("TS_af_heal", "鸣钟教派"), 1);
            game.ZoneManager.MoveToArea(mate, areas[1], 1);      // 另一地区：仍受全场本方光环影响

            EffectEngine.Instance.RegisterPersistent(src);
            Assert.IsTrue(mate.HasIcon("白星"), "注册后同组织本方角色获得白星");
            Assert.AreEqual(1, mate.GetAttribute("先手势力"), "白星同时授予数值通道【先手势力】");

            // 模拟"回合切换/状态重置"：运行期授予被清空
            mate.ResetToBaseState();
            Assert.IsFalse(mate.HasIcon("白星"), "重置后运行期授予已被清空（用例前置）");

            EffectEngine.Instance.ReevaluateAuras(src.ControllerId);

            Assert.IsTrue(mate.HasIcon("白星"), "【自愈】重算后白星必须补回（否则回合切换后会永久丢失）");
            Assert.AreEqual(1, mate.GetAttribute("先手势力"), "【自愈】数值通道【先手势力】也必须补回");

            // ==================== ⑤ 卡面取值：复用地区合计的同一函数 ValueOf（用户口径规则） ====================

            // 用户口径：卡面势力 ＝ 能力 ＋【先手时】先手能力 ＋ 临时能力（"势力"）＋【先手时】临时先手能力（"先手势力"）；
            //               后手回合 ＝ 能力 ＋ 临时能力。
            // 卡面与地区合计**共用** `AreaStatsCalculator.ValueOf(card, Power, playerManager)`（内部自判先手）。
            var pm = game.PlayerManager;
            pm.SetFirstPlayer(1);     // 本卡控制者＝先手
            int firstTurn = AreaStatsCalculator.ValueOf(mate, CombatManager.CombatType.Power, pm);
            Assert.AreEqual(mate.PlayerTemplate.Power + mate.PlayerTemplate.FirstStrikePower + 1, firstTurn,
                "先手回合：能力 ＋ 先手能力 ＋ 临时先手能力（本例无临时“势力”）");

            pm.SetFirstPlayer(2);     // 本卡控制者＝后手
            int secondTurn = AreaStatsCalculator.ValueOf(mate, CombatManager.CombatType.Power, pm);
            Assert.AreEqual(mate.PlayerTemplate.Power, secondTurn,
                "后手回合：只有能力（不含先手能力与临时先手能力）");

            // 卡面显示值必须与地区合计里"该卡的贡献"恒等（同一个函数 ⇒ 同一状态下调用即等）
            Assert.AreEqual(secondTurn, AreaStatsCalculator.ValueOf(mate, CombatManager.CombatType.Power, pm),
                "卡面取值与地区合计逐卡贡献同源（同一函数、同一先手状态下恒等）");
            Assert.IsTrue(firstTurn != secondTurn,
                "先手/后手回合的卡面数值必须不同（临时先手能力只在前手回合计入）");

            // 再清一次，走**回合开始**用的统一入口（`RealignAllContinuousGrants`，与 OnPostMove 同实现）
            mate.ResetToBaseState();
            Assert.IsFalse(mate.HasIcon("白星"), "再次重置后授予已清空（用例前置）");
            EffectEngine.Instance.RealignAllContinuousGrants();
            Assert.IsTrue(mate.HasIcon("白星"), "【回合开始补算】统一入口也必须把白星补回");
            Assert.AreEqual(1, mate.GetAttribute("先手势力"), "【回合开始补算】先手势力数值同样补回");
        }

        // ==================== ③ 关键词筛选：只有灵体获得 ====================

        public void TestAuraFilter_按关键词()
        {
            var game = NewGame();
            string a1 = Areas(game)[0];

            var src = game.CardManager.CreateCardInstance(Load("JC105_emenglingzhu"), 1);
            game.ZoneManager.MoveToArea(src, a1, 1);
            var spirit = game.CardManager.CreateCardInstance(Probe("TS_af_spirit", inherentKeyword: "灵体"), 1);
            game.ZoneManager.MoveToArea(spirit, a1, 1);
            var mortal = game.CardManager.CreateCardInstance(Probe("TS_af_mortal"), 1);
            game.ZoneManager.MoveToArea(mortal, a1, 1);

            EffectEngine.Instance.RegisterPersistent(src);

            Assert.IsTrue(spirit.HasIcon("白星"), "灵体角色获得白星");
            Assert.IsFalse(mortal.HasIcon("白星"), "非灵体角色不受影响");
        }

        // ==================== ④ 仅本地区 + 防御力授予 ====================

        public void TestAuraFilter_仅本地区与防御力()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];

            // 先造好各目标并记录**基础防御**（此刻场上还没有光环源）
            var sameArea = game.CardManager.CreateCardInstance(Probe("TS_af_same"), 1);
            var otherArea = game.CardManager.CreateCardInstance(Probe("TS_af_far"), 1);
            var enemySameArea = game.CardManager.CreateCardInstance(Probe("TS_af_enemy2"), 2);
            int sameBefore = sameArea.CurrentDefense;
            int farBefore = otherArea.CurrentDefense;
            int enemyBefore = enemySameArea.CurrentDefense;

            game.ZoneManager.MoveToArea(sameArea, a1, 1);
            game.ZoneManager.MoveToArea(otherArea, a2, 1);
            game.ZoneManager.MoveToArea(enemySameArea, a1, 2);

            // 光环源进场（生产路径：MoveToArea 即注册光环；区域变动后再重算）
            var src = game.CardManager.CreateCardInstance(Load("JC059_anquanbaoweibumen"), 1);
            int srcDefenseOnEnter = src.CurrentDefense;
            game.ZoneManager.MoveToArea(src, a1, 1);
            EffectEngine.Instance.RegisterPersistent(src);

            Assert.AreEqual(sameBefore + 1, sameArea.CurrentDefense, "本地区本方角色 +1 防御力");
            Assert.AreEqual(farBefore, otherArea.CurrentDefense, "**其它地区**的本方角色不受影响（卡文：位于本地区）");
            Assert.AreEqual(enemyBefore, enemySameArea.CurrentDefense, "对手角色不受影响");
            Assert.AreEqual(srcDefenseOnEnter, src.CurrentDefense, "源卡自身不获得（『其他』）");

            // 源卡离场 → 撤销
            EffectEngine.Instance.UnregisterPersistent(src);
            Assert.AreEqual(sameBefore, sameArea.CurrentDefense, "光环注销后防御力还原");
        }

        // ==================== ⑤ 目标移出本地区 → 光环撤销（重算路径） ====================

        public void TestAuraFilter_移出本地区则撤销()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];

            // 先造目标并记录基础防御（此刻场上还没有光环源）
            var mate = game.CardManager.CreateCardInstance(Probe("TS_af_move"), 1);
            int baseDefense = mate.CurrentDefense;
            game.ZoneManager.MoveToArea(mate, a1, 1);

            var src = game.CardManager.CreateCardInstance(Load("JC059_anquanbaoweibumen"), 1);
            game.ZoneManager.MoveToArea(src, a1, 1);
            EffectEngine.Instance.RegisterPersistent(src);
            Assert.AreEqual(baseDefense + 1, mate.CurrentDefense, "前置：本地区已获得 +1");

            // 该角色移动到另一个地区 → 重算时撤销光环
            game.ZoneManager.MoveToArea(mate, a2, 1);
            Assert.AreEqual(baseDefense, mate.CurrentDefense, "离开本地区 → 光环撤销，防御力还原");
        }

        // ==================== ⑥ 【2026-09 修复·用户实测 JC059】领域筛选必须"显式声明才生效" ====================

        /// <summary>
        /// 用户实测："**JC059 的增加防御效果对于 JC066 没有生效**（也可能只是 UI 没显示）"。
        /// 查证＝**模型未应用**（不是 UI）：`AuraFilterExcludeDomain` 原先默认 `MagicDomain.Mind`，而枚举里
        /// **Mind == 0（没有 None）** ⇒ "没写领域子句"与"显式排除心灵领域"无法区分 ⇒ **每一张没写领域子句的
        /// 光环卡都静默排除了心灵领域角色**；JC066 数据是 `MagicDomains=[0]`（心灵领域），被这条隐式排除挡掉。
        /// 修复＝未声明（-1 哨兵）不做领域筛选；显式声明某域才排除该域。JC060 走优先级更高的
        /// `AuraRequiresNoDomain`（"未印刷任何魔法领域"）⇒ 语义不变（本用例同时反向对照）。
        /// </summary>
        public void TestAuraFilter_领域筛选必须显式声明()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            // ① 印刷**心灵领域**的本方角色（等同 JC066：`MagicDomains=[Mind]`）
            var mindTmpl = Probe("TS_af_mindally");
            mindTmpl.MagicDomains.Add(MagicDomain.Mind);
            var mindAlly = game.CardManager.CreateCardInstance(mindTmpl, 1);
            // ② 无领域本方角色（对照）
            var plainAlly = game.CardManager.CreateCardInstance(Probe("TS_af_plainally"), 1);
            int mindBefore = mindAlly.CurrentDefense;
            int plainBefore = plainAlly.CurrentDefense;
            game.ZoneManager.MoveToArea(mindAlly, area, 1);
            game.ZoneManager.MoveToArea(plainAlly, area, 1);
            Assert.IsTrue(mindAlly.MagicDomains.Contains(MagicDomain.Mind), "前置：心灵领域已落到实例上");

            var src = game.CardManager.CreateCardInstance(Load("JC059_anquanbaoweibumen"), 1);
            game.ZoneManager.MoveToArea(src, area, 1);
            EffectEngine.Instance.RegisterPersistent(src);

            Assert.AreEqual(mindBefore + 1, mindAlly.CurrentDefense,
                "**心灵领域**的本方角色也应 +1（JC059 卡文无领域限制；修复前被隐式排除 = 用户报障）");
            Assert.AreEqual(plainBefore + 1, plainAlly.CurrentDefense, "无领域角色照常 +1（对照）");

            // 反向对照：JC060「所有**未印刷**魔法领域图标的本方角色」＝`AuraRequiresNoDomain=true`（优先级更高）
            // ⇒ 仍必须排除"有领域"者；本次修复**不**改变这条语义。
            var guru = game.CardManager.CreateCardInstance(Load("JC060_shenmixueguwen"), 1);
            game.ZoneManager.MoveToArea(guru, area, 1);
            EffectEngine.Instance.RegisterPersistent(guru);

            Assert.IsFalse(mindAlly.HasIcon("心"), "有领域的角色**不**受 JC060 光环影响（『未印刷领域』口径不变）");
            Assert.IsTrue(plainAlly.HasIcon("心"), "未印刷领域的角色照常获得 JC060 的【心】图标");
        }

        // ==================== ⑦ 【2026-09 修复·用户实测 JC059】减防必须补死亡判定 ====================

        /// <summary>
        /// 用户实测："**JC059 移动地区后，原有地区的角色防御总值下降，如果已经受伤可能会导致死亡，
        /// 现在没有因为这个原因死亡，需要修正**"。
        /// 死亡判据＝"伤害＋创伤 ≥ **当前**防御力"，故"减防"路径（光环撤销等）必须与受伤一样补做死亡判定。
        /// </summary>
        public void TestAuraFilter_减防导致死亡()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];

            var mate = game.CardManager.CreateCardInstance(Probe("TS_af_die"), 1);   // 印刷防御 2
            game.ZoneManager.MoveToArea(mate, a1, 1);

            var src = game.CardManager.CreateCardInstance(Load("JC059_anquanbaoweibumen"), 1);
            game.ZoneManager.MoveToArea(src, a1, 1);
            EffectEngine.Instance.RegisterPersistent(src);
            Assert.AreEqual(3, mate.CurrentDefense, "前置：光环 +1 ⇒ 当前防御 3");

            game.CardManager.DealDamageToInstance(mate, 2);   // 伤害 2
            Assert.AreEqual(CardZone.AreaZone, mate.CurrentZone, "前置：防御 3、伤害 2 ⇒ 仍存活");

            // 光环源移动到**别的地区** ⇒ 撤销授予 ⇒ 当前防御掉回 2 ⇒ 伤害 2 ≥ 防御 2 ⇒ 应死亡
            game.ZoneManager.MoveToArea(src, a2, 1);

            Assert.AreEqual(2, mate.CurrentDefense, "光环撤销 ⇒ 当前防御回落到印刷值 2");
            Assert.AreEqual(CardZone.Graveyard, mate.CurrentZone,
                "**减防后满足死亡判据 ⇒ 必须死亡**（用户实测报障：此前赖着不死）");
        }
    }
}
