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
    /// 【P3·K4】**实例级形态转换** → JC027 花园地精像 / JC028 鲜血奴仆（用户口径 **A15/A28/A32** ＋ 本轮追问答复）：
    ///  · 形态**只改实例**（`CardInstance` 的类别覆盖 / 卡名覆盖 / 印刷效果失活 + 形态效果表 / 印刷图标与防御清零 /
    ///    追加子类别），**没有任何效果改变卡的印刷类别**（A32）；
    ///  · **JC027**「快速行动2：成为一个**地精角色**，且具有［白眼］［黑刀］［黑星］和 1 防御力 直到回合结束
    ///    （**它仍然是附属**）」→ `FormCategory.CharacterOrAttachment` ＋ 子类别「地精」＋ 四项 `Gain`，
    ///    期限＝**直到回合结束**（时效项 `TimedMod{Kind="Form"}` 到期撤销）；
    ///  · **JC028**「快速行动，**牺牲鲜血奴仆**：将它移回场上，成为一个名为**鲜血**的附属并结附于目标**吸血鬼角色**。
    ///    鲜血不具有能力图标和防御力，且具有"受此结附的角色获得 1 防御力 和［黑刀］"的能力」→
    ///    代价＝**牺牲自身**（入堆叠前）→ 同一张卡**以附属身份重新进场**（`Attach`）＋ 形态（改名/改类别/失活印刷效果/
    ///    清零图标与防御）；**无合法目标不能发动**；**结算时目标不在场则不回到场上**。
    /// </summary>
    public class P4FormChangeTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 2828 };
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

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        private static PlayerCardData Probe(string uid, CardCategory category, int cost = 0,
            int defense = 5, params string[] subCategories)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Defense = category == CardCategory.Character ? defense : -1,
            };
            foreach (var s in subCategories) tmpl.SubCategories.Add(s);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveMana(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var asset = game.CardManager.CreateCardInstance(
                    Probe($"TS_k4_asset_{playerId}_{i}", CardCategory.Attachment), playerId);
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

        /// <summary>把一张附属结附到**地区**（与生产路径 `attacharea`/结附地区同款：IsAttached=true ＋ 附属区落地 ＋ 持续登记）。</summary>
        private static CardInstance AttachToArea(GameInitializer game, string uid, string areaUid, int playerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(Load(uid), playerId);
            card.IsAttached = true;
            card.AttachedTo = null;                       // 结附于地区（无宿主）
            game.ZoneManager.MoveToAttachedZone(card, areaUid, playerId);
            EffectEngine.Instance.RegisterPersistent(card);
            return card;
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        // ==================== ① JC027 结构 ====================

        public void TestForm_JC027结构()
        {
            var tmpl = Load("JC027_huayuandijingxiang");
            var e = FindEffect(tmpl, "JC027_A_BecomeGoblinCharacter");
            Assert.IsNotNull(e, "JC027 应有『成为地精角色』效果");
            Assert.AreEqual(EffectTiming.AttachmentActivated, e.Timing, "附属激活能力");
            Assert.AreEqual("快速", e.ActivateWindow, "快速行动");
            Assert.AreEqual(2, e.Cost, "快速行动 2");
            Assert.AreEqual("直到回合结束", e.Duration, "期限＝本回合");
            Assert.IsNotNull(e.FormChange, "声明了实例级形态");
            Assert.AreEqual(FormCategory.CharacterOrAttachment, e.FormChange.Category,
                "类别覆盖＝角色/附属组合（卡文『它仍然是附属』）");
            Assert.AreEqual(1, e.FormChange.AddSubCategories.Count, "追加一个子类别");
            Assert.AreEqual("地精", e.FormChange.AddSubCategories[0], "子类别＝地精（用户口径：实例层加）");
            Assert.IsFalse(e.FormChange.SuppressPrintedEffects, "保留自身印刷效果（形态后仍可再用该能力）");
            Assert.AreEqual(ActionVerb.TransformForm, e.Actions[0].Verb, "第一个动作＝施加形态");
            Assert.AreEqual("直到回合结束", e.Actions[0].Args[1], "形态期限＝直到回合结束");
            Assert.AreEqual(5, e.Actions.Count, "形态 + 白眼/黑刀/黑星/防御 共 5 个动作");
        }

        // ==================== ② JC027：成为角色（参与目标合法性/统计）＋ 回合结束还原 ====================

        public void TestForm_JC027成为角色并回合结束还原()
        {
            var game = NewGame();
            GiveMana(game, 1, 4);
            var goblin = AttachToArea(game, "JC027_huayuandijingxiang", FirstArea(game));   // 结附于地区
            var ability = FindEffect(goblin.PlayerTemplate, "JC027_A_BecomeGoblinCharacter");
            game.TurnManager.PriorityPlayerId = 1;

            Assert.AreEqual(TargetKind.Attachment, CardManager.MapInstanceToTargetKind(goblin),
                "前置：印刷类别＝附属");
            int defBefore = goblin.CurrentDefense;

            game.EffectEngine.ActivateAbility(goblin, ability, null);
            ResolveStackOnce(game);

            Assert.IsTrue(goblin.HasSubCategory("地精"), "形态追加子类别『地精』");
            Assert.AreEqual(CardCategory.Character | CardCategory.Attachment, goblin.EffectiveCategoryFlag,
                "实例类别覆盖＝角色/附属组合");
            Assert.IsTrue(goblin.HasStaticSubCategory("雕像"), "**印刷子类别不变**（只改实例）");
            Assert.AreEqual(TargetKind.Character, CardManager.MapInstanceToTargetKind(goblin),
                "**参与『目标角色』类判定**（用户口径）");
            Assert.AreEqual(CardZone.AreaZone, goblin.CurrentZone,
                "【2026-09 用户口径】成为角色 ⇒ **迁入争夺区**（形态撤销时迁回附属区）");
            // 注：本卡印刷"无防御"（-1 哨兵）→ 授予 1 点后按引擎既有语义从 0 起算，结果为 1

            Assert.AreEqual(1, goblin.CurrentDefense, "获得 1 防御力");
            Assert.AreEqual(1, goblin.GetAttribute("战斗"), "［黑刀］＝+1 战力（属性通道）");
            Assert.IsTrue(goblin.HasIcon("黑刀"), "［黑刀］图标存在");
            Assert.IsTrue(goblin.HasIcon("白眼"), "［白眼］图标存在");
            Assert.IsTrue(goblin.HasIcon("黑星"), "［黑星］图标存在");

            // 回合结束：形态与授予一起还原
            game.EffectEngine.ExpireTurnEndEffects(game.TurnManager.TurnCount);

            Assert.AreEqual(CardCategory.None, goblin.CategoryFlagOverride, "形态类别覆盖已撤销");
            Assert.IsFalse(goblin.IsPrintedEffectsSuppressed, "形态标记已清除");
            Assert.IsFalse(goblin.HasSubCategory("地精"), "子类别『地精』已移除");
            Assert.AreEqual(TargetKind.Attachment, CardManager.MapInstanceToTargetKind(goblin),
                "回到附属（不再被当作目标角色）");
            Assert.AreEqual(-1, goblin.CurrentDefense, "防御力还原为印刷值（本卡无防御＝-1 哨兵；形态到期会按印刷数据重算）");
            Assert.IsFalse(goblin.HasIcon("黑刀"), "图标还原");
        }

        // ==================== ②b JC027：属性可叠加 + 重算不丢（2026-09 修复） ====================

        /// <summary>
        /// 【2026-09 修复·用户实测"JC027 效果给的属性不可叠加，成为角色后再次使用效果不会获得更多属性"】
        /// 根因：`Gain{"防御"}` 原先**只直接改 `CurrentDefense`**（时效项 `Kind="Defense"`），而属性重算
        /// （`ResetToBaseValues`）重建防御的基准是"模板防御 + `GetAttribute("防御")`" ⇒ 一重算就把授予的
        /// 防御冲掉（白眼/黑刀走属性通道故不受影响）。修复：防御也走属性通道（`Grant/Revoke("Attribute","防御")`）。
        /// </summary>
        public void TestForm_JC027属性可叠加且重算不丢()
        {
            var game = NewGame();
            GiveMana(game, 1, 8);
            var goblin = AttachToArea(game, "JC027_huayuandijingxiang", FirstArea(game));
            var ability = FindEffect(goblin.PlayerTemplate, "JC027_A_BecomeGoblinCharacter");
            game.TurnManager.PriorityPlayerId = 1;

            // ① 第一次发动
            game.EffectEngine.ActivateAbility(goblin, ability, null);
            ResolveStackOnce(game);

            Assert.AreEqual(1, goblin.GetAttribute("先手调查"), "第 1 次：白眼＝先手调查 +1");
            Assert.AreEqual(1, goblin.GetAttribute("战斗"), "第 1 次：黑刀＝战斗 +1");
            Assert.AreEqual(1, goblin.GetAttribute("势力"), "第 1 次：黑星＝势力 +1");
            Assert.AreEqual(1, goblin.GetAttribute("防御"), "第 1 次：获得 1 防御力（走属性通道）");
            Assert.AreEqual(1, goblin.CurrentDefense, "第 1 次：当前防御＝1");

            // 强制一次属性重算（模拟先手/光环/形态引起的 Recompute）：授予必须靠属性通道重建而不被冲掉
            goblin.ResetToBaseValues(false);
            Assert.AreEqual(1, goblin.CurrentDefense, "重算后防御仍为 1（旧实现此处会掉回『无防御』）");
            Assert.AreEqual(1, goblin.GetAttribute("先手调查"), "重算后先手调查仍在");

            // ② 第二次发动（卡文"它仍然是附属" ⇒ 可再次发动）：必须**叠加**
            game.TurnManager.PriorityPlayerId = 1;
            game.EffectEngine.ActivateAbility(goblin, ability, null);
            ResolveStackOnce(game);

            Assert.AreEqual(2, goblin.GetAttribute("先手调查"), "第 2 次：先手调查叠加到 2");
            Assert.AreEqual(2, goblin.GetAttribute("战斗"), "第 2 次：战斗叠加到 2");
            Assert.AreEqual(2, goblin.GetAttribute("势力"), "第 2 次：势力叠加到 2");
            Assert.AreEqual(2, goblin.GetAttribute("防御"), "第 2 次：防御叠加到 2（用户报的『不可叠加』）");
            Assert.AreEqual(2, goblin.CurrentDefense, "第 2 次：当前防御＝2");

            goblin.ResetToBaseValues(false);
            Assert.AreEqual(2, goblin.CurrentDefense, "第 2 次后重算仍为 2（授予可被正确重建）");
            Assert.AreEqual(2, goblin.GetAttribute("先手调查"), "第 2 次后重算先手调查＝2");
        }

        // ==================== ②c JC027：形态使其**迁入争夺区**（用户 2026-09 口径） ====================

        /// <summary>
        /// 【2026-09 修复·用户实测"JC027 使用效果后成为角色，现在不能被以角色卡为目标的效果取为目标"】
        /// 用户口径的最终实现：形态使其"成为角色"时**从附属区迁入争夺区**（`ZoneManager.MoveFormZone`，
        /// **刷新版本号**、不发进场/移动触发），形态撤销时**迁回附属区**。
        /// 这样它天然参与地区角色统计、"对地区所有角色"的伤害，以及所有"目标角色"类候选枚举——不需要取目标侧补丁。
        /// </summary>
        public void TestForm_JC027成为角色后迁入争夺区并可被取为目标()
        {
            var game = NewGame();
            GiveMana(game, 1, 4);
            string area = FirstArea(game);
            var goblin = AttachToArea(game, "JC027_huayuandijingxiang", area);
            var ability = FindEffect(goblin.PlayerTemplate, "JC027_A_BecomeGoblinCharacter");
            game.TurnManager.PriorityPlayerId = 1;
            int verBefore = goblin.VersionId;

            Assert.AreEqual(CardZone.AttachedZone, goblin.CurrentZone, "前置：结附于地区（附属区）");
            Assert.IsFalse(game.CardManager.GetAreaInstancesBySlot(area).Contains(goblin),
                "前置：未变形时不在争夺区成员里");

            game.EffectEngine.ActivateAbility(goblin, ability, null);
            ResolveStackOnce(game);

            Assert.AreEqual(TargetKind.Character, CardManager.MapInstanceToTargetKind(goblin), "形态后按角色参与判定");
            Assert.AreEqual(CardZone.AreaZone, goblin.CurrentZone, "形态使其成为角色 ⇒ **迁入争夺区**");
            Assert.AreEqual(area, goblin.ZoneDetailId, "所属地区不变");
            Assert.IsTrue(goblin.IsAttached, "身份仍是附属（卡文『它仍然是附属』）");
            Assert.IsTrue(goblin.VersionId > verBefore, "迁区必须**刷新版本号**（以它为目标的效果按 D16 重新校验）");
            Assert.IsTrue(game.CardManager.GetAreaInstancesBySlot(area).Contains(goblin),
                "⇒ 争夺区成员集合包含它（『目标角色』候选/地区统计/群伤全部天然生效）");
            var inArea = game.ZoneManager.GetAllCardsInAreaIncludingAttached(area);
            int cnt = 0;
            foreach (var c in inArea) if (c == goblin) cnt++;
            Assert.AreEqual(1, cnt, "对抗统计全集里**只出现一次**（不得因既在争夺区又在附属表而重复计入）");

            // 以角色为目标的**激活能力**门禁同样能取到它
            var probeAbility = new EffectData
            {
                EffectId = "TS_jc027_probe",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true, IsLocalZone = true },
            };
            var source = game.CardManager.CreateCardInstance(Probe("TS_jc027_src", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(source, area, 1);
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(source, probeAbility, goblin, game.CardManager, game.PlayerManager),
                "『目标角色』类激活能力可取它为目标");
            Assert.IsTrue(ActivateEligibility.HasLegalTarget(source, probeAbility, game.CardManager, game.PlayerManager),
                "⇒ 该能力判定为『存在合法目标』");
        }

        /// <summary>
        /// 用户目标场景：**"对地区所有角色造成伤害"必须命中形态角色**（迁入争夺区后天然生效）。
        /// </summary>
        public void TestForm_JC027形态角色会被地区群体伤害命中()
        {
            var game = NewGame();
            GiveMana(game, 1, 4);
            string area = FirstArea(game);
            var goblin = AttachToArea(game, "JC027_huayuandijingxiang", area);
            var ability = FindEffect(goblin.PlayerTemplate, "JC027_A_BecomeGoblinCharacter");
            game.TurnManager.PriorityPlayerId = 1;

            int hitBefore = game.PlayerActionService.DealAreaDamage(area, 1);
            Assert.AreEqual(0, goblin.Damage, "前置：未变形时『对地区所有角色』的伤害打不到它（当时它只是地区附属）");

            game.EffectEngine.ActivateAbility(goblin, ability, null);
            ResolveStackOnce(game);
            Assert.IsTrue(goblin.CurrentZone == CardZone.AreaZone, "前置：已迁入争夺区");

            goblin.CurrentDefense = 5;   // 临时提高防御：否则 1 点就打死、Damage 随死亡清零，读不到数值
            int hit = game.PlayerActionService.DealAreaDamage(area, 1);
            Assert.IsTrue(hit >= 1, "地区群体伤害的目标数应包含它");
            Assert.AreEqual(1, goblin.Damage, "形态角色被『对地区所有角色』的伤害命中（用户目标场景）");
            Assert.IsTrue(hitBefore == 0, "对照：变形前同一效果打不到它");
        }

        /// <summary>形态到期（回合结束）⇒ 迁回附属区；若它已离场（墓地）则不得被迁回场上。</summary>
        public void TestForm_JC027形态到期迁回附属区()
        {
            var game = NewGame();
            GiveMana(game, 1, 4);
            string area = FirstArea(game);
            var goblin = AttachToArea(game, "JC027_huayuandijingxiang", area);
            var ability = FindEffect(goblin.PlayerTemplate, "JC027_A_BecomeGoblinCharacter");
            game.TurnManager.PriorityPlayerId = 1;

            game.EffectEngine.ActivateAbility(goblin, ability, null);
            ResolveStackOnce(game);
            Assert.AreEqual(CardZone.AreaZone, goblin.CurrentZone, "前置：形态中在争夺区");
            int verInForm = goblin.VersionId;

            game.EffectEngine.ExpireTurnEndEffects(game.TurnManager.TurnCount);

            Assert.AreEqual(CardZone.AttachedZone, goblin.CurrentZone, "形态撤销 ⇒ 迁回附属区");
            Assert.AreEqual(area, goblin.ZoneDetailId, "仍在同一地区");
            Assert.IsTrue(goblin.IsAttached, "回到附属身份");
            Assert.IsTrue(goblin.VersionId > verInForm, "迁回同样刷新版本号");
            Assert.AreEqual(TargetKind.Attachment, CardManager.MapInstanceToTargetKind(goblin), "不再按角色参与判定");

            // 反向：形态中死亡（进墓地）→ 回合结束的形态还原**不得**把它迁回场上
            var dead = AttachToArea(game, "JC027_huayuandijingxiang", area);
            game.TurnManager.PriorityPlayerId = 1;
            game.EffectEngine.ActivateAbility(dead, ability, null);
            ResolveStackOnce(game);
            Assert.AreEqual(CardZone.AreaZone, dead.CurrentZone, "前置：形态中在争夺区");
            dead.CurrentDefense = 1;
            game.CardManager.DealDamageToInstance(dead, 5);   // 打死 → 进墓地（ResetToBaseState 顺带清形态）
            Assert.AreEqual(CardZone.Graveyard, dead.CurrentZone, "前置：已进墓地");

            game.EffectEngine.ExpireTurnEndEffects(game.TurnManager.TurnCount);
            Assert.AreEqual(CardZone.Graveyard, dead.CurrentZone, "已离场的卡不得被形态还原迁回场上");
        }

        // ==================== ③ JC028 结构 ====================
        public void TestForm_JC028结构()
        {
            var tmpl = Load("JC028_xianxuenupu");
            var e = FindEffect(tmpl, "JC028_A_ReturnAsBloodAttachment");
            Assert.IsNotNull(e, "JC028 应有『牺牲 → 成为鲜血附属』效果");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活能力");
            Assert.AreEqual("快速", e.ActivateWindow, "快速行动");
            Assert.IsTrue(e.ActivateCondition.Contains("牺牲本角色"), "代价＝牺牲本卡自身（卡文点名）");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.IsTrue(e.Target.Restrictions.Contains(TargetRestriction.CurrentSubCategory), "按实例当前子类别筛选");
            Assert.AreEqual("吸血鬼", e.Target.Param, "目标＝吸血鬼角色");
            Assert.IsFalse(e.Target.IsLocalZone, "不限本地区（任意地区）");
            Assert.IsNotNull(e.FormChange, "声明了实例级形态");
            Assert.AreEqual("鲜血", e.FormChange.Name, "改卡名为『鲜血』");
            Assert.AreEqual(FormCategory.Attachment, e.FormChange.Category, "类别改为附属（不保留角色）");
            Assert.IsTrue(e.FormChange.SuppressPrintedEffects, "损失原有（印刷）效果");
            Assert.IsTrue(e.FormChange.ZeroPrintedStatistics, "不具有能力图标和防御力");
            Assert.AreEqual(1, e.FormChange.Effects.Count, "形态只保留『鲜血』自己的效果");
            Assert.IsTrue(e.FormChange.Effects[0].GrantToHost, "该效果授予宿主");
            Assert.AreEqual(ActionVerb.TransformForm, e.Actions[0].Verb, "① 施加形态");
            Assert.AreEqual(ActionVerb.Attach, e.Actions[1].Verb, "② 以附属身份重新进场结附");
        }

        // ==================== ④ JC028 端到端：牺牲自身 → 以「鲜血」附属进场并结附 ====================

        public void TestForm_JC028端到端()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var vampire = game.CardManager.CreateCardInstance(
                Probe("TS_k4_vampire", CardCategory.Character, subCategories: "吸血鬼"), 1);
            game.ZoneManager.MoveToArea(vampire, area, 1);
            int defBefore = vampire.CurrentDefense;

            var nupu = game.CardManager.CreateCardInstance(Load("JC028_xianxuenupu"), 1);
            game.ZoneManager.MoveToArea(nupu, area, 1);
            var ability = FindEffect(nupu.PlayerTemplate, "JC028_A_ReturnAsBloodAttachment");
            game.TurnManager.PriorityPlayerId = 1;

            game.EffectEngine.ActivateAbility(nupu, ability, vampire);
            Assert.AreEqual(CardZone.Graveyard, nupu.CurrentZone, "代价：牺牲自身（入堆叠前支付）");
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.AttachedZone, nupu.CurrentZone, "以附属身份**重新进场**");
            Assert.IsTrue(ReferenceEquals(vampire, nupu.AttachedTo), "结附于目标吸血鬼角色");
            Assert.AreEqual("鲜血", nupu.EffectiveCardName, "改卡名『鲜血』（影响卡名判定）");
            Assert.AreEqual(CardCategory.Attachment, nupu.EffectiveCategoryFlag, "类别＝附属（不保留角色）");
            Assert.IsTrue(nupu.IsPrintedEffectsSuppressed, "损失原有印刷效果");
            Assert.AreEqual(-1, nupu.CurrentDefense, "不具有防御力");
            Assert.AreEqual(0, nupu.CurrentStrength, "不具有能力图标（战力 0）");
            // 形态效果：宿主获得 1 防御力 + 黑刀
            Assert.AreEqual(defBefore + 1, vampire.CurrentDefense, "宿主获得 1 防御力");
            Assert.IsTrue(vampire.HasIcon("黑刀"), "宿主获得［黑刀］");
        }

        // ==================== ⑤ JC028：场上没有符合条件的角色 → 不能发动 ====================

        public void TestForm_JC028无合法目标不能发动()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var plain = game.CardManager.CreateCardInstance(Probe("TS_k4_plain", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(plain, area, 1);   // 非吸血鬼
            var nupu = game.CardManager.CreateCardInstance(Load("JC028_xianxuenupu"), 1);
            game.ZoneManager.MoveToArea(nupu, area, 1);
            var ability = FindEffect(nupu.PlayerTemplate, "JC028_A_ReturnAsBloodAttachment");
            game.TurnManager.PriorityPlayerId = 1;

            game.EffectEngine.ActivateAbility(nupu, ability, plain);   // 指定非吸血鬼（非法目标）

            Assert.AreEqual(CardZone.AreaZone, nupu.CurrentZone, "未牺牲、未离场");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠（无合法目标 → 不能发动）");
        }

        // ==================== ⑥ JC028：结算时目标已不在场 → 不回到场上 ====================

        public void TestForm_JC028目标离场则不回场()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var vampire = game.CardManager.CreateCardInstance(
                Probe("TS_k4_vampire2", CardCategory.Character, subCategories: "吸血鬼"), 1);
            game.ZoneManager.MoveToArea(vampire, area, 1);
            var nupu = game.CardManager.CreateCardInstance(Load("JC028_xianxuenupu"), 1);
            game.ZoneManager.MoveToArea(nupu, area, 1);
            var ability = FindEffect(nupu.PlayerTemplate, "JC028_A_ReturnAsBloodAttachment");
            game.TurnManager.PriorityPlayerId = 1;

            game.EffectEngine.ActivateAbility(nupu, ability, vampire);
            Assert.AreEqual(CardZone.Graveyard, nupu.CurrentZone, "前置：已牺牲自身");

            // 目标在堆叠期间离场（版本号变化 → D16 目标丢失 → 整条不结算）
            game.ZoneManager.MoveToHand(vampire, 1);
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.Graveyard, nupu.CurrentZone, "**结算时目标不在场 → 不回到场上**（留在墓地）");
            Assert.IsNull(nupu.AttachedTo, "没有任何结附关系");
        }
    }
}
