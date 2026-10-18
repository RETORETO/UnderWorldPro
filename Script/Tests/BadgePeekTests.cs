namespace Script.Tests
{
    /// <summary>
    /// 【2026-10-18 用户口径·角标展开面板】锁定两条**交互契约**（源码级，headless 下点不了 UI，故用文本锚点）：
    ///
    /// ① **封印卡与附属卡分开展示**——两者来自两个不同区域，绝不能混在一个列表里：
    ///    每类角标（`封×N` / `附×N`）各自是独立 hover 命中区，悬浮哪一类就只展开那一类。
    /// ② **面板里的卡双击＝发动激活能力**（如 JC020 引匕的飞刀：横置自身 + 横置目标暗藏者）：
    ///    附属/封印卡被折叠成角标后，场上已经没有可双击的卡面 ⇒ 双击入口必须由展开面板补回来，
    ///    且必须走**既有双击链路**（`_onAttachmentDoubleClickedForward` / `OnAttachmentDoubleClicked`），
    ///    不能自己另开一条绕开门禁的通道。
    ///
    /// 为什么值得单测：这两条都是"看着像小 UI、实际决定效果能不能用"的契约。
    ///   · 混排 ⇒ 玩家在"附"里点开却看到封印卡，选错对象；
    ///   · 双击入口丢失 ⇒ JC020 这类附属彻底变成死卡（用户实测反馈的原始问题）。
    /// </summary>
    public class BadgePeekTests : TestRunner
    {
        private static string Read(string resRelative)
        {
            using var f = global::Godot.FileAccess.Open("res://" + resRelative,
                global::Godot.FileAccess.ModeFlags.Read);
            if (f == null) return string.Empty;
            return f.GetAsText();
        }

        /// <summary>① 角标分家：每类角标独立 hover，回调带 `CardBadgeKind`。</summary>
        public void TestBadge_封印与附属各自独立悬浮()
        {
            string src = Read("Script/UI/CardBadges.cs");
            Assert.IsFalse(string.IsNullOrEmpty(src), "必须能读到 CardBadges.cs");

            Assert.IsTrue(src.Contains("enum CardBadgeKind"), "必须用枚举区分角标种类（封印/附属）");
            Assert.IsTrue(src.Contains("CardBadgeKind.Seal") && src.Contains("CardBadgeKind.Attach"),
                "封印与附属必须各成一种角标");
            Assert.IsTrue(src.Contains("OnBadgeHover?.Invoke(capturedHost, capturedKind)"),
                "hover 回调必须携带角标种类（否则视图无法只展开对应那一种）");
            Assert.IsTrue(src.Contains("MouseFilter = Control.MouseFilterEnum.Ignore"),
                "角标根节点只作布局容器（Ignore），hover 必须由两个标签各自接收");
        }

        /// <summary>② 展开列表必须按种类过滤：封印只认 SealedBy，附属只认 AttachedTo；禁止无过滤混排。</summary>
        public void TestBadge_展开列表按种类过滤()
        {
            foreach (var path in new[] { "Script/UI/Arena.BadgePeek.cs", "Script/UI/ArenaOl.cs" })
            {
                string src = Read(path);
                Assert.IsFalse(string.IsNullOrEmpty(src), $"必须能读到 {path}");

                Assert.IsTrue(src.Contains("kind == CardBadgeKind.Seal"),
                    $"{path}：必须按角标种类分支（封印 / 附属）");
                Assert.IsTrue(src.Contains("ReferenceEquals(s.SealedBy, inst)"),
                    $"{path}：封印卡必须校验 SealedBy == 宿主（不混入封印于地区的卡）");
                Assert.IsTrue(src.Contains("ReferenceEquals(m.AttachedTo, inst)"),
                    $"{path}：附属卡必须校验 AttachedTo == 宿主（不混入结附于地区的卡）");
                Assert.IsFalse(src.Contains("if (m != null) members.Add(m);"),
                    $"{path}：**禁止**无过滤地把附属塞进列表（旧实现＝封印/附属混排，用户已否决）");
            }
        }

        /// <summary>③ 双击＝激活：面板卡片双击必须转调既有双击激活链路，单击仍只置选中。</summary>
        public void TestBadge_面板内双击发动激活能力()
        {
            string peek = Read("Script/UI/CardBadgePeek.cs");
            Assert.IsFalse(string.IsNullOrEmpty(peek), "必须能读到 CardBadgePeek.cs");

            Assert.IsTrue(peek.Contains("Action<CardUI> onActivate"),
                "展开面板必须暴露 onActivate 出口（双击发动）");
            Assert.IsTrue(peek.Contains("mb.DoubleClick") && peek.Contains("_onActivate?.Invoke(capturedUi)"),
                "双击（DoubleClick）必须走 onActivate；单击仍走 onPick（只置选中、不提交）");

            string arena = Read("Script/UI/Arena.BadgePeek.cs");
            Assert.IsTrue(arena.Contains("_onAttachmentDoubleClickedForward?.Invoke(ui)"),
                "单机：双击必须复用既有双击链路（来源判定/能力枚举/门禁日志一致），不得另开通道");

            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsTrue(ol.Contains("OnAttachmentDoubleClicked(ui)"),
                "联机：双击必须走上行激活意图的既有链路（服务端权威校验）");
        }
    }
}
