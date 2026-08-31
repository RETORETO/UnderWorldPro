using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;
using Script.Net.Godot;

namespace Script.Tests
{
    /// <summary>
    /// 【临时验证文件】伪跨进程联机 + ResolveInstance 从模板重建（跨进程最核心的技术验证点）。
    ///
    /// ⚠️ 临时性标注：本文件仅用于验证"跨进程联机"的客户端独立 CardManager 重建路径是否可行，
    ///    不是正式对局逻辑。验证通过后应移除本文件（以及测试用例）。
    ///
    /// 目的：当前 NetworkMatchLauncher 是单进程回环（客户端与服务端【共享】CardManager，ResolveInstance
    ///    直接命中服务端已建实例）。真实跨进程时，客户端进程没有服务端实例，必须从模板（.tres）按权威 UID
    ///    新建自己的实例。本文件用一个"独立 ClientCardManager"在单进程内模拟跨进程，验证这条重建路径：
    ///    服务端建局 → 快照 → 客户端独立 CardManager 从模板重建 → 断言投影手牌数与卡 UID 一致。
    /// </summary>
    public class NetCrossProcessSimTests : TestRunner
    {
        /// <summary>
        /// 客户端独立 CardManager：重载 ResolveInstance，本地无实例时从模板（.tres）按权威 UID 新建。
        /// 这模拟"跨进程客户端"的行为——它与服务端不共享实例，只靠快照里的 TemplateUid 重建卡。
        /// </summary>
        private class ClientCardManager : CardManager
        {
            private readonly Dictionary<string, BaseCardData> _templates; // TemplateUid → 模板数据
            public ClientCardManager(Dictionary<string, BaseCardData> templates) { _templates = templates; }

            protected override CardInstance ResolveInstance(ZoneCardRef r)
            {
                // 1) 本地已建（重复快照场景）
                var hit = GetInstance(r.Uid);
                if (hit != null) return hit;
                // 2) 从模板按权威 UID 新建（跨进程核心：无共享实例，靠 TemplateUid 反查模板）
                if (r != null && _templates.TryGetValue(r.TemplateUid, out var tmpl))
                {
                    var inst = CreateCardInstance(tmpl, r.OwnerId);
                    AdoptInstanceWithUid(inst, r.Uid); // 重置为权威 UID 并登记
                    return inst; // 日志已降噪：每张卡一条的「从模板重建」过于刷屏
                }
                return null;
            }
        }

        /// <summary>运行时扫描 res://Cards/Data 下全部 .tres，构建 TemplateUid → 模板 索引（模拟跨进程客户端本地模板库）。</summary>
        private static Dictionary<string, BaseCardData> BuildTemplateIndex()
        {
            var index = new Dictionary<string, BaseCardData>();
            var dirs = new[]
            {
                "res://Cards/Data/PlayerCards",
                "res://Cards/Data/AreaCards",
                "res://Cards/Data/CouncilCards",
            };
            foreach (var dir in dirs)
                foreach (var path in EnumerateTres(dir))
                {
                    var template = ResourceLoader.Load<BaseCardData>(path);
                    if (template == null || string.IsNullOrEmpty(template.UID)) continue;
                    index[template.UID] = template;
                }
            GD.Print($"[NetCrossProcessSim] 模板索引就绪：{index.Count} 张卡");
            return index;
        }

        private static List<string> EnumerateTres(string dir)
        {
            var result = new List<string>();
            if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(dir))) return result;
            using var d = DirAccess.Open(dir);
            if (d == null) return result;
            d.ListDirBegin();
            string name;
            while ((name = d.GetNext()) != "")
            {
                if (d.CurrentIsDir())
                {
                    if (!name.StartsWith(".")) result.AddRange(EnumerateTres(dir + "/" + name));
                    continue;
                }
                if (name.EndsWith(".tres")) result.Add(dir + "/" + name);
            }
            d.ListDirEnd();
            return result;
        }

        /// <summary>
        /// 核心验证：服务端建局 → 快照 → 客户端【独立 ClientCardManager】从模板重建 → 断言投影一致。
        /// 这证明"跨进程客户端不共享实例、仅靠快照重建"的路径可行。
        /// </summary>
        public void TestCrossProcessClientRebuildFromTemplate()
        {
            // 1) 服务端：正式装配 + 起手（权威建卡）
            var server = new GameInitializer { DebugSeed = 7 };
            server.InitializeAll();
            server.StartMatch();

            // 2) 客户端：独立 CardManager（从模板重建）+ 独立 ZoneManager/DeckManager（模拟跨进程，不共享实例）
            var clientCard = new ClientCardManager(BuildTemplateIndex());
            var clientDeck = new DeckManager();
            clientDeck.Initialize(clientCard, new Script.Game.SeededRandomSource(1));
            var clientZone = new ZoneManager();
            clientZone.Initialize(clientCard, clientDeck);
            clientCard.SetZoneManager(clientZone);

            // 3) 服务端导出快照，客户端 ApplySnapshot（跨进程：只传快照，不传实例）
            var snap = server.BuildSnapshot();
            Assert.IsNotNull(snap, "服务端应能导出快照");
            clientZone.ApplySnapshot(snap.Zones);

            // 4) 断言：客户端投影手牌数 == 服务端权威手牌数（从模板重建成功）
            var serverHand = server.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
            var clientHand = clientZone.GetZoneCards(CardZone.Hand, 1).Count;
            Assert.AreEqual(serverHand, clientHand,
                $"跨进程客户端从模板重建后，投影手牌数({clientHand})应等于服务端权威({serverHand})");

            // 5) 断言：客户端投影中的卡 UID 与权威一致（ResolveInstance 用权威 UID 重建）
            foreach (var c in clientZone.GetZoneCards(CardZone.Hand, 1))
            {
                var serverInst = server.CardManager.GetInstance(c.UID);
                Assert.IsNotNull(serverInst, $"客户端从模板重建的卡 UID={c.UID} 应在服务端权威实例中存在（UID 对齐）");
                Assert.AreEqual(serverInst.UID, c.UID, "客户端重建卡 UID 应与权威一致");
                // 关键：客户端实例必须与服务端是【不同对象】（模拟跨进程），证明确实新建而非共享
                Assert.IsTrue(!ReferenceEquals(serverInst, c),
                    $"客户端卡 UID={c.UID} 应是独立新建实例（非共享服务端实例），模拟跨进程");
            }

            // 6) 断言：客户端重建的卡运行时字段正确（伤害/横置等随快照覆盖）
            if (clientHand > 0)
            {
                var first = clientZone.GetZoneCards(CardZone.Hand, 1)[0];
                var serverFirst = server.CardManager.GetInstance(first.UID);
                Assert.AreEqual(serverFirst.Damage, first.Damage,
                    $"客户端重建卡 UID={first.UID} 的伤害字段应随快照覆盖");
            }
        }

        /// <summary>
        /// 完整闭环（伪跨进程 launcher 版）：用改造后的 NetworkMatchLauncher 注入独立 ClientCardManager，
        /// 验证"服务端权威 → 意图上行 → 快照下行 → 客户端独立实例从模板重建"的端到端投影一致。
        /// 这比纯 ApplySnapshot 更贴近真实跨进程：客户端不共享服务端实例，全靠快照重建。
        /// </summary>
        public void TestCrossProcessLauncherLoopback()
        {
            const ushort port = 18940;

            var server = new GameInitializer { DebugSeed = 8 };
            server.InitializeAll();
            server.StartMatch();

            // 客户端独立 CardManager（从模板重建，不共享服务端实例）+ Loopback 回环
            var clientCard = new ClientCardManager(BuildTemplateIndex());
            var launcher = new NetworkMatchLauncher();
            var loop = new LoopbackTransport();
            launcher.StartLocalLoopback(server, port, loop, loop, clientCard);

            // 客户端上行抽牌意图 → 服务端权威结算 → 快照下行 → 客户端从模板重建
            launcher.SendIntent(PlayerIntent.Kind.DrawCards, "1");
            launcher._Process(0);

            var serverHand = server.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
            var clientHand = launcher.ClientZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
            Assert.AreEqual(serverHand, clientHand,
                $"跨进程 launcher 闭环后，客户端投影手牌数({clientHand})应等于服务端权威({serverHand})");

            // 客户端投影卡是独立实例（从模板重建，非共享），且 UID 对齐
            foreach (var c in launcher.ClientZoneManager.GetZoneCards(CardZone.Hand, 1))
            {
                var serverInst = server.CardManager.GetInstance(c.UID);
                Assert.IsNotNull(serverInst, $"客户端 launcher 重建卡 UID={c.UID} 应对齐服务端权威");
                Assert.IsTrue(!ReferenceEquals(serverInst, c),
                    $"客户端 launcher 卡 UID={c.UID} 应为独立新建实例（非共享服务端实例）");
            }

            launcher.Stop();
        }
    }
}
