using Godot;
using Script.Enums;
using Script.Game.Entities;
using Script.UI;
using System.Collections.Generic;

/// <summary>
/// 【P3a/P3b】角标悬浮展开（单机 Arena 侧）。
///
/// 流程：`CardBadges`（宿主卡右下角）被悬浮 → 本文件按**角标种类**解析宿主实例上的成员
/// （「封×N」只取封印卡、「附×N」只取附属卡，两者来自不同区域**不混排**）→
/// `CardBadgePeek` 在宿主旁横向展开卡面 → 单击成员：若当前有 `CardPickWindow` 且该卡在候选内，
/// 调 `SelectByUid`（**只置选中、不提交**）；否则纯浏览；**双击成员：发动该卡的激活能力**。
///
/// 为什么双击必须在这里补：附属/封印卡被折叠成角标后，场上已经**没有可双击的卡面**，
/// 而它们的能力（`EffectTiming.AttachmentActivated`，如 JC020「横置自身 + 横置目标暗藏者」）
/// 原本只能靠"双击场上那张附属卡"进入。折叠把入口藏起来 ⇒ 由展开面板把入口接回来。
///
/// 语义红线：展开面板**不参与任何游戏逻辑**——双击只是转调既有的双击链路
/// （`OpenAttachmentActivateChoice`），是否可发动、要不要取目标、横置与付费全由
/// Controller 把关；本文件不写 Model、不触发结算、不替代「确认」。
/// </summary>
public partial class Arena
{
    private CardBadgePeek _badgePeek;
    /// <summary>最近一次打开的选择窗（`CardPickWindow`），供展开面板"只置选中"用。</summary>
    private CardPickWindow _activePickWindow;
    /// <summary>鼠标连续离开"角标 + 面板"的时长（秒）；超过阈值才收起，避免角标↔面板间移动时闪断。</summary>
    private double _badgePeekMiss;

    // ---- 【2026-10-19】资产区（折叠成一摞）/ 墓地区：悬停弹卡列表 ----
    private Control _pilePeekHost;      // 当前弹出列表的那一块（资产容器 / 墓地）
    private bool _pilePeekInside;       // 鼠标是否已在某一块里（用于"只在进入时重建面板"）

    /// <summary>把角标悬浮接到本视图（在 `_Ready` 里调用一次）。</summary>
    private void InitBadgePeek()
    {
        CardBadges.OnBadgeHover = ShowBadgePeek;
        // 离开角标不再"立刻排收起"：收起统一由 `TickBadgePeek` 按矩形轮询判定，
        // 否则鼠标从面板移向空白处时（角标早已 exit）没有任何触发，面板会一直挂着。
        CardBadges.OnBadgeHoverEnd = (host, kind) => _badgePeekMiss = 0.0;
    }

    private void DetachBadgePeek()
    {
        if (CardBadges.OnBadgeHover == (System.Action<Control, CardBadgeKind>)ShowBadgePeek)
            CardBadges.OnBadgeHover = null;
        CardBadges.OnBadgeHoverEnd = null;
        _badgePeek?.HideNow();
    }

    /// <summary>展开宿主卡上**某一种**角标对应的成员列表（封印 / 附属二选一）。</summary>
    private void ShowBadgePeek(Control host, CardBadgeKind kind)
    {
        if (host == null || !GodotObject.IsInstanceValid(host)) return;
        var inst = host is CardUI c ? c.Instance : (host as CouncilCardUI)?.Instance;
        if (inst == null) return;

        var members = new List<CardInstance>();
        if (kind == CardBadgeKind.Seal)
        {
            // 封印卡：只认「封印于本卡」（SealedBy == 宿主）。封印于地区的（SealedBy == null）不在角标里。
            if (inst.SealedCards != null)
                foreach (var s in inst.SealedCards)
                    if (s != null && ReferenceEquals(s.SealedBy, inst)) members.Add(s);
        }
        else
        {
            // 附属卡：只认「结附于本卡」（AttachedTo == 宿主）。结附于地区的（AttachedTo == null）在地区内平铺。
            if (inst.AttachedCards != null)
                foreach (var m in inst.AttachedCards)
                    if (m != null && ReferenceEquals(m.AttachedTo, inst)) members.Add(m);
        }
        if (members.Count == 0) return;

        _badgePeekMiss = 0.0;                        // 新面板从"鼠标在内"开始计时
        CardPreviewBus.Overlay?.HidePreview();       // 同一时刻只留一个浮层（展开面板优先）

        EnsureBadgePeek();
        // 标题直接写明"双击发动能力"：折叠成角标后玩家看不到卡面、也没有任何提示，
        // 此前只能靠猜（用户实测"点了没反应"）。封印卡通常没有激活能力，不加这句避免误导。
        string title = kind == CardBadgeKind.Seal ? "封印卡" : "附属卡（双击发动能力）";
        _badgePeek.ShowNear(host, title, members, OnBadgeMemberPicked, OnBadgeMemberActivated);
    }

    /// <summary>展开面板是"场上的唯一浮层"⇒ 角标与堆叠区共用同一个实例。</summary>
    private void EnsureBadgePeek()
    {
        if (_badgePeek != null && GodotObject.IsInstanceValid(_badgePeek)) return;
        // 【2026-10-19】注入"可发动"探针：面板只负责画，判定与场上绿框**同一实现**。
        _badgePeek = new CardBadgePeek
        {
            Name = "CardBadgePeek",
            ActivatableProbe = IsCardActivatableForUi,
        };
        AddChild(_badgePeek);
    }

    private void OnBadgeMemberPicked(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        // 只置选中：仍需用户在选择窗里按「确认」才提交（与方案 4.5 的场上点选同一语义）
        if (_activePickWindow != null && GodotObject.IsInstanceValid(_activePickWindow))
        {
            _activePickWindow.SelectByUid(uid);
            GD.Print($"[Arena] 角标展开面板点选 {uid}（置选中，不提交）");
            return;
        }
        // 没有选择窗＝纯浏览，**不收起面板**（双击的第一次按下就是这次单击）。
        // 留一条日志：下次"点了没反应"能立刻区分是"单击到了但没东西可选"还是"事件压根没进面板"。
        GD.Print($"[Arena] 角标展开面板单击 {uid}（纯浏览；双击＝发动激活能力）");
    }

    /// <summary>
    /// 展开面板里**双击**成员 ⇒ 发动激活能力。转调 `_onAttachmentDoubleClickedForward`
    /// ——与"场上双击这张卡"**同一出口**（来源判定 `IsDoubleClickActivationSource`、能力枚举
    /// `模板 ∪ 运行期授予`、门禁 `CanActivate` 与可读拒绝日志全部复用），
    /// 门禁/取目标/横置/付费仍由 Controller 把关；面板先收起，避免浮层压住随后弹出的取目标窗。
    /// </summary>
    private void OnBadgeMemberActivated(CardUI ui)
    {
        if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) return;
        var inst = ui.Instance;
        _badgePeek?.HideNow();
        GD.Print($"[Arena] 角标展开面板双击激活：{inst.PlayerTemplate?.CardName}（{inst.UID}）");
        _onAttachmentDoubleClickedForward?.Invoke(ui);
    }

    /// <summary>
    /// 收起判定：**每帧按矩形轮询**，不依赖 hover 事件。
    ///
    /// 为什么不能只靠 `mouse_exited`：面板是浮层，鼠标常走的路径是「角标 → 面板 → 空白处」，
    /// 而角标的 exit 早在第一步就发完了；从面板移出时没有任何一方会收到通知 ⇒ 面板会一直挂在那里。
    /// 轮询同时覆盖"面板 → 另一张卡"（整卡预览弹出）等路径，保证同一时刻只留一个浮层。
    /// </summary>
    private void TickBadgePeek(double delta)
    {
        if (_badgePeek == null || !GodotObject.IsInstanceValid(_badgePeek) || !_badgePeek.Visible)
        {
            _badgePeekMiss = 0.0;
            return;
        }

        Vector2 mouse = GetGlobalMousePosition();
        bool inside = _badgePeek.ContainsGlobalPoint(mouse);
        if (!inside && _badgePeek.Host != null && GodotObject.IsInstanceValid(_badgePeek.Host))
            inside = _badgePeek.Host.GetGlobalRect().Grow(8f).HasPoint(mouse);
        if (!inside) inside = IsMouseInsidePileHost(mouse);   // 墓地卡以 5px 递增铺开，会超出面板矩形

        if (inside)
        {
            _badgePeekMiss = 0.0;
            return;
        }

        _badgePeekMiss += delta;
        if (_badgePeekMiss >= 0.25)
        {
            _badgePeek.HideNow();
            _badgePeekMiss = 0.0;
        }
    }

    // ============================================================
    // 【2026-10-19】资产区 / 墓地区：悬停弹卡列表
    //
    // 用户口径（同日二次修订）：「资产区的折叠UI回退，但是也要支持hover显示所有资产卡」。
    //  · 资产区：**层叠平铺**（`CardLayoutHelper.LayoutStacked`，每张露 36px，不再叠成一摞 + ×N）；
    //    悬停整块区仍弹卡列表——层叠态下卡片互相压着，靠列表才能一次看全所有卡面。
    //  · 墓地区：本就是缩略堆，视觉不动，悬停弹列表即可（张数多时按视口宽度截断并提示总数）。
    // 面板复用角标那一个实例（场上同一时刻只允许一个浮层），单击仍走"选择窗置选中（不提交）"。
    // ============================================================

    /// <summary>每帧判定"鼠标是否落在某个可悬停的堆叠区"；进入瞬间弹列表，离开交给 `TickBadgePeek` 收起。</summary>
    private void TickPilePeek(double delta)
    {
        Vector2 mouse = GetGlobalMousePosition();

        // 鼠标已经在面板上 ⇒ 保持现状（否则从容器移向面板时会被判成"离开"而重建）
        if (_badgePeek != null && GodotObject.IsInstanceValid(_badgePeek)
            && _badgePeek.Visible && _badgePeek.ContainsGlobalPoint(mouse))
        {
            _pilePeekInside = true;
            return;
        }

        Control host = null;
        string title = null;
        List<CardInstance> members = null;

        if (TryAssetPile(mouse, out host, out members)) title = "资产";
        else if (TryGravePile(mouse, out host, out members)) title = "墓地";

        if (host == null)
        {
            _pilePeekInside = false;
            _pilePeekHost = null;
            return;
        }

        // 同一块上不重复重建（每帧重建会产生大量临时节点）
        if (_pilePeekInside && ReferenceEquals(host, _pilePeekHost)) return;

        _pilePeekInside = true;
        _pilePeekHost = host;
        CardPreviewBus.Overlay?.HidePreview();
        EnsureBadgePeek();
        // 【2026-10-19 修复·JC085】资产/墓地列表的**双击出口**同样不能是空实现：
        // 墓地里的「墓地行动」卡（`PlayableFromGraveyard`）双击要走 `OnGraveyardPlayRequested`
        // （弹选地区→从墓地正面打出），其它（资产区的角色激活等）走 `OnAttachmentDoubleClicked`。
        // 旧写法 `_ => { }` 让 JC085 这类在折叠 UI 里彻底发不动（用户实测：墓地卡点不动）。
        _badgePeek.ShowNear(host, title, members, OnPileMemberPicked, OnPileMemberActivated);
        _badgePeekMiss = 0.0;
        GD.Print($"[Arena] 堆叠区悬停展开：{title} {members.Count} 张");
    }

    private bool TryAssetPile(Vector2 mouse, out Control host, out List<CardInstance> members)
    {
        host = null; members = null;
        foreach (var ctr in new Control[] { _assetContainer1, _assetContainer2 })
        {
            if (ctr == null || !GodotObject.IsInstanceValid(ctr)) continue;
            if (!ctr.GetGlobalRect().HasPoint(mouse)) continue;
            var list = CollectCardInstances(ctr);
            if (list.Count < 2) continue;
            host = ctr; members = list;
            return true;
        }
        return false;
    }

    private bool TryGravePile(Vector2 mouse, out Control host, out List<CardInstance> members)
    {
        host = null; members = null;
        foreach (var zone in new Control[] { _graveyardZone, _graveyardZone2 })
        {
            if (zone == null || !GodotObject.IsInstanceValid(zone)) continue;
            var list = CollectCardInstances(zone);
            if (list.Count < 2) continue;              // 一张卡本来就看得到，不必弹
            bool inside = zone.GetGlobalRect().Grow(8f).HasPoint(mouse);
            if (!inside)
            {
                foreach (Node ch in zone.GetChildren())
                {
                    if (ch is not CardUI c || !GodotObject.IsInstanceValid(c)) continue;
                    if (c.GetGlobalRect().HasPoint(mouse)) { inside = true; break; }
                }
            }
            if (!inside) continue;
            host = zone; members = list;
            return true;
        }
        return false;
    }

    private static List<CardInstance> CollectCardInstances(Control container)
    {
        var list = new List<CardInstance>();
        foreach (Node ch in container.GetChildren())
            if (ch is CardUI c && GodotObject.IsInstanceValid(c) && c.Instance != null) list.Add(c.Instance);
        return list;
    }

    /// <summary>鼠标是否还在当前堆叠区内（含铺开的子卡矩形；墓地卡会超出面板）。</summary>
    private bool IsMouseInsidePileHost(Vector2 mouse)
    {
        var host = _pilePeekHost;
        if (host == null || !GodotObject.IsInstanceValid(host)) return false;
        if (host.GetGlobalRect().Grow(8f).HasPoint(mouse)) return true;
        foreach (Node ch in host.GetChildren())
            if (ch is CardUI c && GodotObject.IsInstanceValid(c) && c.GetGlobalRect().HasPoint(mouse))
                return true;
        return false;
    }

    /// <summary>列表里单击：有选择窗 ⇒ 只置选中（不提交）；没有 ⇒ 纯浏览。</summary>
    private void OnPileMemberPicked(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        if (_activePickWindow != null && GodotObject.IsInstanceValid(_activePickWindow))
        {
            _activePickWindow.SelectByUid(uid);
            GD.Print($"[Arena] 堆叠区列表点选 {uid}（置选中，不提交）");
        }
        else
        {
            // 【2026-10-19 诊断】与联机端对齐：纯浏览也要留痕，否则"点了没反应"无法区分
            // 是面板没收到点击、还是收到了但没有选择窗可置选中。
            GD.Print($"[Arena] 堆叠区列表单击 {uid}（纯浏览；双击＝发动激活能力）");
        }
    }

    /// <summary>
    /// 列表里双击 ⇒ 按卡所处区域分派到正确的激活链路（与"场上双击那张卡"**同一出口**）：
    ///  · 墓地 + `PlayableFromGraveyard`（JC085 等「墓地行动」）→ `OnGraveyardPlayRequested`
    ///    （弹选地区 → 从墓地正面打出）；
    ///  · 其余（资产区角色激活、争夺区角色激活等）→ `OnBadgeMemberActivated`
    ///    （走 `_onAttachmentDoubleClickedForward`，能力枚举/门禁/取目标全由 Controller 把关）。
    /// 面板先收起，避免浮层压住随后弹出的选择窗。
    /// </summary>
    private void OnPileMemberActivated(CardUI ui)
    {
        if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) return;
        var inst = ui.Instance;
        _badgePeek?.HideNow();

        if (inst.CurrentZone == CardZone.Graveyard && inst.PlayerTemplate?.PlayableFromGraveyard == true)
        {
            GD.Print($"[Arena] 堆叠区列表双击（墓地行动）：{inst.PlayerTemplate.CardName}（{inst.UID}）");
            _onGraveyardPlayRequestedForward?.Invoke(ui);
            return;
        }

        GD.Print($"[Arena] 堆叠区列表双击激活：{inst.PlayerTemplate?.CardName}（{inst.UID}）");
        _onAttachmentDoubleClickedForward?.Invoke(ui);
    }
}
