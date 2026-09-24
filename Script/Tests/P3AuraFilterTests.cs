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
    }
}