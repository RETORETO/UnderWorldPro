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
    /// 【K14·目标种类扩展（墓地 / 手牌）】按用户 2026-09 口径实现并锁定：
    ///  · **允许指定对手的手牌 / 墓地**（卡面用 `Restrictions` 的 Friendly/Enemy 限定"你/对手"）；
    ///  · **结算时目标位置版本号变动即丢失**（复用 D16：一切区域移动 `BumpVersion` → 比对入栈版本号）；
    ///  · 候选合法性 = 大类（按实例所在区域映射）＋ `TargetSpec.Filter` 文本筛选（`DeckSearchFilter` 语法）
    ///    ＋ `Restrictions`（`TargetValidity.CheckRestrictions`），单一实现 `TargetZonePool`；
    ///  · 选择窗口：这些牌没有场上 CardUI → 单机 `PromptPickCard` / 联机 `ChoiceRequest.Kind.SelectZoneCard`。
    /// 覆盖卡：**JC086 窃尸人**（进场触发：将你墓地中的目标角色牌置于你的手中）。
    /// </summary>
    public class P3TargetZoneTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 37 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
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

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ⑦ 或种类（KindAlt）：附属 或 资产（JC107）+ 候选池扩大 ====================

        public void TestK14_JC107_结构_单目标附属或资产()
        {
            var tmpl = Load("JC107_bengtadeshikong");
            Assert.IsNotNull(tmpl, "JC107 模板应可加载");
            Assert.IsTrue(tmpl.LimitToActionPhaseOnly, "『行动』→ 限行动阶段打出");
            Assert.AreEqual(2, tmpl.TargetRules.Count, "打出目标＝两条规则（附属 / 资产，OR）");
            Assert.AreEqual(TargetKind.Attachment, tmpl.TargetRules[0].Kind, "第一条＝附属");
            Assert.AreEqual(TargetKind.Asset, tmpl.TargetRules[1].Kind, "第二条＝资产");

            var e = FindEffect(tmpl, "JC107_1_DestroyAttachmentOrAsset");
            Assert.IsNotNull(e, "JC107 应有消灭效果");
            Assert.AreEqual(TargetKind.Attachment, e.Target.Kind, "效果目标主大类＝附属");
            Assert.AreEqual(TargetKind.Asset, e.Target.KindAlt, "或种类＝资产（单目标二选一）");
            Assert.IsTrue(e.Target.NeedsSelection, "需要选择单个目标（只能选一个）");
        }

        /// <summary>或种类候选池：附属与资产合并，且结算复检接受任一（只选一个目标）。</summary>
        public void TestK14_或种类候选池合并附属与资产()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);
            var src = game.CardManager.CreateCardInstance(Probe("TS_k14_or_src"), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            var host = game.CardManager.CreateCardInstance(Probe("TS_k14_or_host"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var attachment = game.CardManager.CreateCardInstance(Probe("TS_k14_or_att", CardCategory.Attachment), 1);
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, area, 1);
            var asset = game.CardManager.CreateCardInstance(Probe("TS_k14_or_asset", CardCategory.Attachment), 1);
            game.ZoneManager.MoveToAsset(asset, 1);

            var spec = new TargetSpec { Kind = TargetKind.Attachment, KindAlt = TargetKind.Asset, NeedsSelection = true };
            var pool = TargetZonePool.Collect(spec, 1, game.ZoneManager, game.PlayerManager);
            Assert.IsTrue(pool.Exists(c => ReferenceEquals(c, attachment)), "候选应含附属（附属区）");
            Assert.IsTrue(pool.Exists(c => ReferenceEquals(c, asset)), "候选应含资产（资产区）");
            Assert.AreEqual(TargetKind.Attachment, CardManager.MapInstanceToTargetKind(attachment), "附属映射为 Attachment");
            Assert.AreEqual(TargetKind.Asset, CardManager.MapInstanceToTargetKind(asset), "资产映射为 Asset");

            Assert.IsTrue(TargetValidity.Check(null, attachment, spec, null, game.PlayerManager).valid, "结算复检：附属合法");
            Assert.IsTrue(TargetValidity.Check(null, asset, spec, null, game.PlayerManager).valid, "结算复检：资产合法（走 KindAlt）");
        }

        /// <summary>端到端：JC107 消灭附属 / 消灭资产。</summary>
        public void TestK14_JC107_消灭附属或资产()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);
            var host = game.CardManager.CreateCardInstance(Probe("TS_k14_107_host"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var attachment = game.CardManager.CreateCardInstance(Probe("TS_k14_107_att", CardCategory.Attachment), 2);
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, area, 2);
            var asset = game.CardManager.CreateCardInstance(Probe("TS_k14_107_asset", CardCategory.Attachment), 2);
            game.ZoneManager.MoveToAsset(asset, 2);

            var card = game.CardManager.CreateCardInstance(Load("JC107_bengtadeshikong"), 1);
            EffectEngine.Instance.ResolveEffect(FindEffect(card.PlayerTemplate, "JC107_1_DestroyAttachmentOrAsset"), card, attachment);
            Assert.AreEqual(CardZone.Graveyard, attachment.CurrentZone, "目标附属被消灭（进墓地）");

            var card2 = game.CardManager.CreateCardInstance(Load("JC107_bengtadeshikong"), 1);
            EffectEngine.Instance.ResolveEffect(FindEffect(card2.PlayerTemplate, "JC107_1_DestroyAttachmentOrAsset"), card2, asset);
            Assert.AreEqual(CardZone.Graveyard, asset.CurrentZone, "目标资产被消灭（进墓地）");
        }

        /// <summary>候选池扩大（用户口径"可以扩大"）：附属区/秘社群也能作为**激活**目标（此前只扫争夺区）。</summary>
        public void TestK14_激活目标候选含附属与秘社()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);
            var src = game.CardManager.CreateCardInstance(Probe("TS_k14_expand_src"), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            var host = game.CardManager.CreateCardInstance(Probe("TS_k14_expand_host"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var attachment = game.CardManager.CreateCardInstance(Probe("TS_k14_expand_att", CardCategory.Attachment), 1);
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, area, 1);

            var ability = new EffectData
            {
                EffectId = "TS_k14_expand_e",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Target = new TargetSpec { Kind = TargetKind.Attachment, NeedsSelection = true },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Tap } },
            };
            Assert.IsTrue(ActivateEligibility.HasLegalTarget(src, ability, game.CardManager, game.PlayerManager),
                "激活目标候选应含『附属区』（此前只扫争夺区 → 永远判无合法目标）");

            var enemyCouncils = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 2);
            if (enemyCouncils.Count > 0)
            {
                var councilAbility = new EffectData
                {
                    EffectId = "TS_k14_expand_c",
                    Timing = EffectTiming.CharacterActivated,
                    ActivateWindow = "快速",
                    Target = new TargetSpec { Kind = TargetKind.Council, NeedsSelection = true },
                    Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Tap } },
                };
                var pile = TargetZonePool.Collect(councilAbility.Target, 1, game.ZoneManager, game.PlayerManager);
                Assert.IsTrue(pile.Exists(c => ReferenceEquals(c, enemyCouncils[0])), "秘社也应能作为目标候选（候选池已扩大）");
            }
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character, string sub = null)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = category, Defense = 1,
            };
            if (sub != null) tmpl.SubCategories.Add(sub);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>把一张探针卡放进某玩家墓地。</summary>
        private static CardInstance InGraveyard(GameInitializer game, string uid, int ownerId,
            CardCategory category = CardCategory.Character, string sub = null)
        {
            var inst = game.CardManager.CreateCardInstance(Probe(uid, category, sub), ownerId);
            game.ZoneManager.MoveToGraveyard(inst, ownerId);
            return inst;
        }

        /// <summary>单机选卡桩：记录候选并按"第一个候选"回包（模拟 PromptPickCard）。</summary>
        private sealed class PickStub : IGameUIHooks
        {
            public int Calls;
            public List<CardInstance> LastCandidates = new List<CardInstance>();
            public string LastPrompt;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptCurbMarks(int curbedPlayerId, int remaining, List<CurbMarkOption> options,
                System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed)
                => onConfirmed?.Invoke(cards, new List<CardInstance>());

            /// <summary>【K15】本套件不涉及多目标：视为玩家取消。</summary>
            public void PromptMultiTargets(CardInstance source, Script.Data.Effects.EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed)
                => onConfirmed?.Invoke(null);

            /// <summary>【K16】本套件不涉及模式分支：视为取消。</summary>
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, System.Collections.Generic.List<int> candidates, System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);   // 【K24】桩：默认取第一个候选

            public void PromptEffectMode(CardInstance source, Script.Data.Effects.EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            /// <summary>【K11】本套件不涉及数值输入：视为取消。</summary>
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue, string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
            {
                Calls++;
                LastPrompt = prompt;
                LastCandidates = new List<CardInstance>(candidates ?? new List<CardInstance>());
                onPicked?.Invoke(LastCandidates.Count > 0 ? LastCandidates[0] : null);
            }
        }

        // ==================== ① 大类映射：墓地/手牌/资产按"所在区域"映射 ====================

        public void TestK14_墓地与手牌按区域映射大类()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var charInGrave = InGraveyard(game, "TS_k14_g1", 1);
            Assert.AreEqual(TargetKind.Graveyard, CardManager.MapInstanceToTargetKind(charInGrave),
                "墓地里的角色牌应映射为『墓地目标』（按所在区域优先，与卡类别无关）");

            var attInGrave = InGraveyard(game, "TS_k14_g2", 1, CardCategory.Attachment);
            Assert.AreEqual(TargetKind.Graveyard, CardManager.MapInstanceToTargetKind(attInGrave),
                "墓地里的附属牌同样映射为『墓地目标』");

            var inHand = game.CardManager.CreateCardInstance(Probe("TS_k14_h1"), 1);
            game.ZoneManager.MoveToHand(inHand, 1);
            Assert.AreEqual(TargetKind.Hand, CardManager.MapInstanceToTargetKind(inHand), "手牌应映射为『手牌目标』");

            // 【资产】用户口径：资产同理（且进入/离开资产区都要刷新版本号）
            var asset = game.CardManager.CreateCardInstance(Probe("TS_k14_a1"), 1);
            int verBefore = asset.VersionId;
            game.ZoneManager.MoveToAsset(asset, 1);
            Assert.AreEqual(TargetKind.Asset, CardManager.MapInstanceToTargetKind(asset),
                "资产区里的牌应映射为『资产目标』");
            Assert.IsTrue(verBefore != asset.VersionId, "进入资产区应刷新版本号（离开同理）");

            var onField = game.CardManager.CreateCardInstance(Probe("TS_k14_f1"), 1);
            game.ZoneManager.MoveToArea(onField, area, 1);
            Assert.AreEqual(TargetKind.Character, CardManager.MapInstanceToTargetKind(onField),
                "场上角色仍映射为『角色』（既有行为零回归）");
        }

        /// <summary>资产目标候选池：本方/对手资产均可枚举，且资产区→其他区（版本号变动）即判丢失。</summary>
        public void TestK14_资产目标候选与离场丢失()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());   // 触发阶段走网关（避免同步路径需要触发视图）
            string area = FirstArea(game);
            var src = game.CardManager.CreateCardInstance(Probe("TS_k14_asrc"), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            var mineAsset = game.CardManager.CreateCardInstance(Probe("TS_k14_mine_asset"), 1);
            game.ZoneManager.MoveToAsset(mineAsset, 1);
            var enemyAsset = game.CardManager.CreateCardInstance(Probe("TS_k14_enemy_asset"), 2);
            game.ZoneManager.MoveToAsset(enemyAsset, 2);

            var specMine = new TargetSpec
            {
                Kind = TargetKind.Asset,
                NeedsSelection = true,
                Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
            };
            var mine = TargetZonePool.Collect(specMine, 1, game.ZoneManager, game.PlayerManager);
            Assert.IsTrue(mine.Exists(c => ReferenceEquals(c, mineAsset)), "『本方资产』候选应含本方资产区的牌");
            Assert.IsFalse(mine.Exists(c => ReferenceEquals(c, enemyAsset)), "『本方资产』不应含对手资产");

            var specEnemy = new TargetSpec
            {
                Kind = TargetKind.Asset,
                NeedsSelection = true,
                Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
            };
            var enemy = TargetZonePool.Collect(specEnemy, 1, game.ZoneManager, game.PlayerManager);
            Assert.IsTrue(enemy.Exists(c => ReferenceEquals(c, enemyAsset)), "『对手资产』允许枚举对手资产区的牌（用户口径）");

            // 丢失：资产在结算前离开资产区（版本号变动）→ 判丢失不结算
            var probeTmpl = new PlayerCardData
            {
                UID = "TS_k14_asset_hit", CardName = "探针_消灭资产", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            probeTmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_k14_asset_hit_e",
                Timing = EffectTiming.EnterTrigger,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Asset,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Destroy } },
            });
            probeTmpl._effectsInjected = true;
            var probe = game.CardManager.CreateCardInstance(probeTmpl, 1);
            game.ZoneManager.MoveToArea(probe, area, 1);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_k14_asset_victim"), 2);
            game.ZoneManager.MoveToAsset(victim, 2);
            int verAtPush = victim.VersionId;
            game.ZoneManager.MoveToGraveyard(victim, 2);   // 离开资产区（例：被牺牲）→ 版本号变动

            EffectEngine.Instance.ResolveEffect(probeTmpl.EffectDataList[0], probe, victim, "", -1, verAtPush);
            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone,
                "资产目标离开资产区（版本号变动）→ 结算判丢失，不做二次处置（仍留在墓地）");
        }

        // ==================== ② 候选池：Filter 文本筛选 + Friendly/Enemy ====================

        public void TestK14_候选池按筛选与阵营枚举()
        {
            var game = NewGame();
            var mineChar = InGraveyard(game, "TS_k14_mc", 1);
            InGraveyard(game, "TS_k14_ma", 1, CardCategory.Attachment);   // 本方附属（应被『类别:角色』筛掉）
            var enemyChar = InGraveyard(game, "TS_k14_ec", 2);

            var specMine = new TargetSpec
            {
                Kind = TargetKind.Graveyard,
                Filter = "类别:角色",
                NeedsSelection = true,
                Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
            };
            var mineList = TargetZonePool.Collect(specMine, 1, game.ZoneManager, game.PlayerManager);
            Assert.AreEqual(1, mineList.Count, "『你墓地中的角色牌』：只枚举本方墓地的角色牌");
            Assert.IsTrue(ReferenceEquals(mineChar, mineList[0]), "候选应为本方墓地的那张角色牌");

            // 对手墓地（Enemy）同样可枚举（用户口径：允许指定对手的墓地）
            var specEnemy = new TargetSpec
            {
                Kind = TargetKind.Graveyard,
                Filter = "类别:角色",
                NeedsSelection = true,
                Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
            };
            var enemyList = TargetZonePool.Collect(specEnemy, 1, game.ZoneManager, game.PlayerManager);
            Assert.AreEqual(1, enemyList.Count, "『对手墓地中的角色牌』：允许枚举对手墓地");
            Assert.IsTrue(ReferenceEquals(enemyChar, enemyList[0]), "候选应为对手墓地的那张角色牌");

            // 手牌目标同理
            var mineHand = game.CardManager.CreateCardInstance(Probe("TS_k14_hand"), 1);
            game.ZoneManager.MoveToHand(mineHand, 1);
            var specHand = new TargetSpec
            {
                Kind = TargetKind.Hand,
                Filter = "类别:角色",
                NeedsSelection = true,
                Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
            };
            var handList = TargetZonePool.Collect(specHand, 1, game.ZoneManager, game.PlayerManager);
            Assert.IsTrue(handList.Exists(c => ReferenceEquals(c, mineHand)), "手牌目标候选应包含本方手牌中的角色牌");
        }

        /// <summary>未知筛选字段 fail-closed（与检索口径一致）：写错字段名 → 无候选，而不是"匹配全部"。</summary>
        public void TestK14_未知筛选字段failclosed()
        {
            var game = NewGame();
            InGraveyard(game, "TS_k14_bad", 1);
            var spec = new TargetSpec
            {
                Kind = TargetKind.Graveyard,
                Filter = "不存在的字段:角色",
                NeedsSelection = true,
                Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
            };
            Assert.AreEqual(0, TargetZonePool.Collect(spec, 1, game.ZoneManager, game.PlayerManager).Count,
                "未知筛选字段 → 无候选（fail-closed，避免静默全匹配）");
        }

        // ==================== ③ 结算：Bounce 到拥有者手牌 ====================

        public void TestK14_JC086_墓地角色牌回手()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());   // 触发阶段走网关（避免同步路径需要触发视图）
            string area = FirstArea(game);
            var thief = game.CardManager.CreateCardInstance(Load("JC086_qieshiren"), 1);
            game.ZoneManager.MoveToArea(thief, area, 1);

            var target = InGraveyard(game, "TS_k14_tg", 1);
            var e = FindEffect(thief.PlayerTemplate, "JC086_1_GraveyardToHand");
            EffectEngine.Instance.ResolveEffect(e, thief, target);

            Assert.AreEqual(CardZone.Hand, target.CurrentZone, "墓地中的角色牌应被移回其拥有者手牌");
            Assert.AreEqual(1, target.OwnerId, "回到 OWNER 手牌");
        }

        // ==================== ④ 丢失判定：目标位置版本号变动 ====================

        /// <summary>入栈后目标离开墓地（版本号变动）→ 结算时判丢失、不结算（复用 D16）。</summary>
        public void TestK14_目标离开墓地即丢失()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);
            var thief = game.CardManager.CreateCardInstance(Load("JC086_qieshiren"), 1);
            game.ZoneManager.MoveToArea(thief, area, 1);
            var target = InGraveyard(game, "TS_k14_lost", 1);

            var e = FindEffect(thief.PlayerTemplate, "JC086_1_GraveyardToHand");
            int versionAtPush = target.VersionId;

            // 目标在结算前被移出墓地（例：被其他效果取回手牌）→ 版本号变动
            game.ZoneManager.MoveToHand(target, 1);
            Assert.IsTrue(versionAtPush != target.VersionId, "前置：区域移动应刷新版本号");

            // 结算时带上入栈版本号：应判丢失，不再结算（卡已在手牌，效果不应再次改变其位置）
            EffectEngine.Instance.ResolveEffect(e, thief, target, "", -1, versionAtPush);
            Assert.AreEqual(CardZone.Hand, target.CurrentZone, "目标丢失 → 不结算（卡仍在手牌，未被二次搬运）");
            Assert.IsTrue(game.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Count == 0,
                "墓地已无该卡（前置动作的结果保持不变）");
        }

        // ==================== ⑤ 选择窗口：单机走选卡窗（无场上 CardUI） ====================

        public void TestK14_单机触发选卡窗选择墓地目标()
        {
            var game = NewGame();
            // 单机语义：**不注入网关**（走同步触发流程 / 本地选卡窗）
            game.TurnManager.SetTriggerView(new SilentTriggerView());
            string area = FirstArea(game);

            var picker = new PickStub();
            EffectEngine.Instance.UIHooks = picker;

            InGraveyard(game, "TS_k14_ui", 1);
            var thief = game.CardManager.CreateCardInstance(Load("JC086_qieshiren"), 1);
            game.ZoneManager.MoveToArea(thief, area, 1);   // 进场 → 进场触发 → 需选墓地目标

            Assert.AreEqual(1, picker.Calls, "墓地目标应走『选卡窗』（PromptPickCard）而非场上点选");
            Assert.AreEqual(1, picker.LastCandidates.Count, "候选＝本方墓地中的角色牌");
            Assert.IsTrue((picker.LastPrompt ?? "").Contains("墓地"), "窗口提示应说明是墓地选择");
            EffectEngine.Instance.UIHooks = null;
        }

        /// <summary>无 UI 时确定性兜底：同步路径下不走"场上点选"（RequestTargetSelection 不应被调用），直接取第一候选。</summary>
        public void TestK14_无UI时确定性兜底()
        {
            var game = NewGame();
            var view = new SilentTriggerView();
            game.TurnManager.SetTriggerView(view);   // 单机语义（无网关）；本用例**不注入** UIHooks
            string area = FirstArea(game);

            var target = InGraveyard(game, "TS_k14_noUI", 1);
            var thief = game.CardManager.CreateCardInstance(Load("JC086_qieshiren"), 1);
            game.ZoneManager.MoveToArea(thief, area, 1);

            Assert.AreEqual(0, view.TargetSelectionCalls,
                "墓地目标**不应**走『场上点选』窗口（这些卡没有场上 CardUI）");
            Assert.IsTrue(game.StackManager.Entries.Count > 0, "进场触发应入堆叠");
            StackEntryInspect(game, thief.UID, target.UID);   // 断言条目目标=墓地那张牌（确定性兜底取第一候选）
        }

        private static void StackEntryInspect(GameInitializer game, string sourceUid, string targetUid)
        {
            foreach (var e in game.StackManager.Entries)
            {
                if (e.SourceCardUid != sourceUid) continue;
                Assert.AreEqual(targetUid, e.TargetCardUid, "堆叠条目的目标应为墓地中的那张牌（确定性兜底取第一候选）");
                return;
            }
            Assert.IsTrue(false, "未找到该来源的堆叠条目");
        }

        // ==================== ⑥ 联机：SelectZoneCard 决策请求 ====================

        public void TestK14_联机下发SelectZoneCard请求()
        {
            var game = NewGame();
            var gateway = new EnterAllGateway();
            gateway.PickZoneCard = req => req.OptionUids.Count > 0 ? req.OptionUids[0] : null;
            game.SetInputGateway(gateway);
            string area = FirstArea(game);

            var target = InGraveyard(game, "TS_k14_on", 1);   // 本方墓地中的角色牌（候选）
            var thief = game.CardManager.CreateCardInstance(Load("JC086_qieshiren"), 1);
            game.ZoneManager.MoveToArea(thief, area, 1);      // 进场触发 → 需选墓地目标

            // 线上路径：触发排序 → 选目标（SelectZoneCard）
            var req = gateway.Requests.Find(r => r.RequestKind == Script.Net.ChoiceRequest.Kind.SelectZoneCard);
            Assert.IsNotNull(req, "墓地目标应下发 SelectZoneCard 决策请求（不走『场上点选』的 SelectTarget）");
            Assert.AreEqual(1, req.PlayerId, "请求发给效果控制者");
            Assert.AreEqual(target.UID, req.OptionUids[0], "候选＝本方墓地中的角色牌");
            Assert.AreEqual(1, req.OptionCardRefs.Count, "必须随请求下发展示引用（这些卡没有场上 CardUI）");
            Assert.IsTrue(req.ForceChoice, "选目标不可取消");
            Assert.IsTrue(game.StackManager.Entries.Count > 0, "选完目标后触发应入堆叠");
        }

        /// <summary>触发排序全部入堆叠的网关；对 SelectZoneCard 回第一个候选。</summary>
        private sealed class EnterAllGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public System.Func<Script.Net.ChoiceRequest, string> PickZoneCard;

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                if (req?.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTriggerOrder)
                {
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                }
                else if (req?.RequestKind == Script.Net.ChoiceRequest.Kind.SelectZoneCard)
                {
                    r.SelectedUid = PickZoneCard != null ? PickZoneCard(req) : (req.OptionUids.Count > 0 ? req.OptionUids[0] : null);
                }
                else if (req?.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTarget
                         && req.OptionUids != null && req.OptionUids.Count > 0)
                {
                    r.SelectedUid = req.OptionUids[0];
                }
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        /// <summary>不弹窗的触发视图：把触发**全部入堆叠**（不排序），并记录"场上点选"调用次数。</summary>
        private sealed class SilentTriggerView : Script.Interfaces.ITriggerView
        {
            public int TargetSelectionCalls;
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                System.Collections.Generic.List<StackEffectInstance> triggers,
                System.Action<System.Collections.Generic.List<StackEffectInstance>> onEnterStack,
                System.Action onDone)
            {
                onEnterStack?.Invoke(triggers);   // 全部入堆叠（本视图不做排序/放弃）
                onDone?.Invoke();
            }
            public void RequestTargetSelection(StackEffectInstance trigger,
                System.Collections.Generic.List<string> validTargetUids, System.Action<string> onPicked)
            {
                TargetSelectionCalls++;
                onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);
            }
            public void RemoveOrderEntries(System.Collections.Generic.List<StackEffectInstance> entries) { }
        }
    }
}
