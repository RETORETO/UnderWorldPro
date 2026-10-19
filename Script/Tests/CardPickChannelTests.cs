using System.Collections.Generic;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;
using Script.UI;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-10-19 用户口径】选卡呈现通道（cat-1 / cat-2）的判据锁定。
    ///
    /// 用户最终口径：
    ///  · **cat-1**（可走场上高亮 + 左键点选）：争夺区角色｜结附于**地区**的附属｜封印于**地区**的封印｜
    ///    自己手牌｜地区本身｜秘社**卡本身**；
    ///  · **cat-2**（必须弹选择窗）：**资产区（无条件）**｜**结附/封印于角色或秘社卡的附属/封印**｜
    ///    对手手牌｜墓地｜牌堆｜放逐｜隐藏占位；
    ///  · **判据是 All(cat-1)**：候选里混进任意一张 cat-2 ⇒ 整单走选择窗。
    ///
    /// 为什么这条必须单测：混合候选若被误判成 cat-1，那些被折叠成角标的附属在场上**没有可点 UI**
    /// （`Arena.Layout.CollapseMember` 把它们 `Visible=false`），玩家会看到"高亮了却点不动"，
    /// 效果直接卡死（用户实测：JC107 目标含资产区/角色附属时不弹窗）。
    /// </summary>
    public class CardPickChannelTests : TestRunner
    {
        private static CardInstance NewCard(string uid, CardZone zone, int ownerId = 1, int controllerId = 1)
        {
            var tmpl = new PlayerCardData { UID = uid, CardName = uid, CategoryFlag = CardCategory.Character };
            var inst = new CardInstance(tmpl, uid)
            {
                OwnerId = ownerId,
                ControllerId = controllerId,
                CurrentZone = zone,
            };
            return inst;
        }

        /// <summary>结附于地区的附属（AttachedTo == null）＝cat-1；结附于卡（角色/秘社）＝cat-2。</summary>
        private static CardInstance NewAttachment(string uid, CardInstance host, int controllerId = 1)
        {
            var inst = NewCard(uid, CardZone.AttachedZone, controllerId, controllerId);
            inst.IsAttached = true;
            inst.AttachedTo = host;
            return inst;
        }

        private static CardInstance NewSeal(string uid, CardInstance host, int controllerId = 1)
        {
            var inst = NewCard(uid, CardZone.SealedZone, controllerId, controllerId);
            inst.IsSeal = true;
            inst.SealedBy = host;
            return inst;
        }

        // ============================================================
        // 单卡判定
        // ============================================================

        /// <summary>cat-1 全表：争夺区角色 / 秘社卡本身 / 自己手牌 / 结附于地区的附属 / 封印于地区的封印。</summary>
        public void TestCat1_场上可见卡与自己手牌()
        {
            Assert.IsTrue(CardPickChannel.IsCat1(NewCard("area", CardZone.AreaZone), 1),
                "争夺区角色是 cat-1（场上直接可见可点）");
            Assert.IsTrue(CardPickChannel.IsCat1(NewCard("council", CardZone.CouncilZone), 1),
                "秘社**卡本身**是 cat-1");
            Assert.IsTrue(CardPickChannel.IsCat1(NewCard("hand1", CardZone.Hand, ownerId: 1), 1),
                "自己手牌是 cat-1");
            Assert.IsFalse(CardPickChannel.IsCat1(NewCard("hand2", CardZone.Hand, ownerId: 2), 1),
                "对手手牌是 cat-2（本机没有可信卡面）");

            var areaAttach = NewAttachment("att_area", host: null);
            Assert.IsTrue(CardPickChannel.IsCat1(areaAttach, 1),
                "结附于**地区**的附属是 cat-1（平铺在地区上，点得到）");
            var areaSeal = NewSeal("seal_area", host: null);
            Assert.IsTrue(CardPickChannel.IsCat1(areaSeal, 1),
                "封印于**地区**的封印是 cat-1");
        }

        /// <summary>cat-2 全表：资产区 / 结附或封印于卡（角色、秘社）/ 墓地 / 牌堆 / 放逐 / 隐藏占位。</summary>
        public void TestCat2_资产与结附封印于卡()
        {
            Assert.IsFalse(CardPickChannel.IsCat1(NewCard("asset", CardZone.AssetZone), 1),
                "**资产区无条件 cat-2**（用户口径）");
            Assert.IsFalse(CardPickChannel.IsCat1(NewCard("grave", CardZone.Graveyard), 1),
                "墓地是 cat-2");
            Assert.IsFalse(CardPickChannel.IsCat1(NewCard("deck", CardZone.Deck), 1),
                "牌堆是 cat-2");
            Assert.IsFalse(CardPickChannel.IsCat1(NewCard("exile", CardZone.Exiled), 1),
                "放逐区是 cat-2");

            var charHost = NewCard("char", CardZone.AreaZone);
            Assert.IsFalse(CardPickChannel.IsCat1(NewAttachment("att_char", charHost), 1),
                "**结附于角色**的附属是 cat-2（被折叠成角标，场上点不到）");

            var councilHost = NewCard("council_host", CardZone.CouncilZone);
            Assert.IsFalse(CardPickChannel.IsCat1(NewAttachment("att_council", councilHost), 1),
                "**结附于秘社卡**的附属是 cat-2（用户口径）");
            Assert.IsFalse(CardPickChannel.IsCat1(NewSeal("seal_council", councilHost), 1),
                "**封印于秘社卡**的封印是 cat-2（用户口径）");
            Assert.IsFalse(CardPickChannel.IsCat1(NewSeal("seal_char", charHost), 1),
                "**封印于角色**的封印是 cat-2（用户口径）");

            var hidden = NewCard("hidden", CardZone.Hand, ownerId: 1);
            hidden.IsHidden = true;
            Assert.IsFalse(CardPickChannel.IsCat1(hidden, 1),
                "隐藏占位一律 cat-2（fail-closed：没有可读卡面）");
        }

        // ============================================================
        // 列表汇总（关键：All(cat1)，不是 Any(cat1)）
        // ============================================================

        /// <summary>混合候选（地区附属 cat-1 + 角色附属 cat-2）⇒ **必须弹选择窗**。</summary>
        public void TestClassify_混合候选必须弹选择窗()
        {
            var charHost = NewCard("char", CardZone.AreaZone);
            var list = new List<CardInstance>
            {
                NewAttachment("att_area", host: null),      // cat-1
                NewAttachment("att_char", charHost),        // cat-2
            };
            Assert.IsTrue(CardPickChannel.Classify(list, 1) == CardPickChannelKind.Window,
                "同时含『结附于地区的附属(cat-1)』与『结附于角色的附属(cat-2)』⇒ 必须走选择窗");
        }

        /// <summary>加一张资产也一样：混合即弹窗。</summary>
        public void TestClassify_地区附属加资产必须弹选择窗()
        {
            var list = new List<CardInstance>
            {
                NewAttachment("att_area", host: null),      // cat-1
                NewCard("asset", CardZone.AssetZone),       // cat-2
            };
            Assert.IsTrue(CardPickChannel.Classify(list, 1) == CardPickChannelKind.Window,
                "地区附属 + 资产区 ⇒ 必须走选择窗（资产无条件 cat-2）");
        }

        /// <summary>只有全 cat-1 才不弹窗。</summary>
        public void TestClassify_全cat1才走场上点选()
        {
            var list = new List<CardInstance>
            {
                NewAttachment("att_a", host: null),
                NewAttachment("att_b", host: null),
                NewCard("char", CardZone.AreaZone),
                NewCard("hand", CardZone.Hand, ownerId: 1),
                NewCard("council", CardZone.CouncilZone),
            };
            Assert.IsTrue(CardPickChannel.Classify(list, 1) == CardPickChannelKind.FieldClick,
                "全 cat-1 ⇒ 场上高亮 + 左键点选（不开卡面窗）");
        }

        /// <summary>空列表 / null ⇒ FieldClick；未知 UID ⇒ Window（fail-closed）。</summary>
        public void TestClassify_空列表与未知Uid()
        {
            Assert.IsTrue(CardPickChannel.Classify(null, 1) == CardPickChannelKind.FieldClick,
                "null 候选按空处理（调用方会自己给『无合法目标』兜底）");
            Assert.IsTrue(CardPickChannel.Classify(new List<CardInstance>(), 1) == CardPickChannelKind.FieldClick,
                "空候选按 cat-1 处理（无窗可开）");

            Assert.IsTrue(CardPickChannel.ClassifyUids(new List<string> { "not_found" }, _ => null, 1)
                    == CardPickChannelKind.Window,
                "解析不到的 UID 必须按 cat-2 处理（宁多开一个窗，也绝不『高亮了点不到』）");
        }

        // ============================================================
        // 联机装配契约（源码级：headless 点不了 UI，锁调用点不丢）
        // ============================================================

        /// <summary>联机两条取目标链路都必须过分类器（单机由协调器统一把关，联机没有协调器）。</summary>
        public void TestWiring_联机取目标必须接分类器()
        {
            string ol = ReadSrc("Script/UI/ArenaOl.cs");
            Assert.IsFalse(string.IsNullOrEmpty(ol), "必须能读到 ArenaOl.cs");

            int n = 0;
            int idx = 0;
            while (true)
            {
                idx = ol.IndexOf("CardPickChannel.ClassifyUids", idx, System.StringComparison.Ordinal);
                if (idx < 0) break;
                n++;
                idx += 26;
            }
            Assert.IsTrue(n >= 2,
                $"联机『事务卡取目标』与『服务端 SelectTarget』两处都必须过 cat-1/cat-2 分类（当前 {n} 处）");
            Assert.IsTrue(ol.Contains("CardPickChannel.Classify("),
                "开局调度（自己手牌＝全 cat-1）也必须过分类器，不能再无条件弹卡面多选窗");
        }

        /// <summary>跨局残留：先 true 再 false 的快照必须把结束标志清回 false（否则第二局墓地行动永久被拒）。</summary>
        public void TestSnapshot_跨局复位结束标志()
        {
            var pm = new PlayerManager();
            pm.ApplySnapshot(new List<PlayerSnapshot>
            {
                new PlayerSnapshot { PlayerId = 1, IsGameOver = true },
                new PlayerSnapshot { PlayerId = 2, IsGameOver = true },
            });
            Assert.IsTrue(pm.IsGameOver, "快照说结束了 ⇒ 投影必须是结束");

            // 同一实例开第二局：服务端快照会把 IsGameOver 写回 false
            pm.ApplySnapshot(new List<PlayerSnapshot>
            {
                new PlayerSnapshot { PlayerId = 1, IsGameOver = false },
                new PlayerSnapshot { PlayerId = 2, IsGameOver = false },
            });
            Assert.IsFalse(pm.IsGameOver,
                "新一局的快照必须把上一局的结束标志清零（否则 CanPlayFromGraveyardNow 恒拒『对局已结束』）");
        }

        private static string ReadSrc(string resRelative)
        {
            using var f = global::Godot.FileAccess.Open("res://" + resRelative,
                global::Godot.FileAccess.ModeFlags.Read);
            return f == null ? string.Empty : f.GetAsText();
        }
    }
}
