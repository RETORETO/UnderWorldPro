using Godot;
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
    /// <summary>收起延迟令牌：鼠标在角标↔面板间移动时不闪断。</summary>
    private int _badgePeekHideToken;

    /// <summary>把角标悬浮接到本视图（在 `_Ready` 里调用一次）。</summary>
    private void InitBadgePeek()
    {
        CardBadges.OnBadgeHover = ShowBadgePeek;
        CardBadges.OnBadgeHoverEnd = (host, kind) => ScheduleHideBadgePeek();
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

        _badgePeekHideToken++;                       // 取消待执行的收起
        CardPreviewBus.Overlay?.HidePreview();       // 同一时刻只留一个浮层（展开面板优先）

        if (_badgePeek == null || !GodotObject.IsInstanceValid(_badgePeek))
        {
            _badgePeek = new CardBadgePeek { Name = "CardBadgePeek" };
            AddChild(_badgePeek);
        }
        string title = kind == CardBadgeKind.Seal ? "封印卡" : "附属卡";
        _badgePeek.ShowNear(host, title, members, OnBadgeMemberPicked, OnBadgeMemberActivated);
    }

    private void OnBadgeMemberPicked(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        // 只置选中：仍需用户在选择窗里按「确认」才提交（与方案 4.5 的场上点选同一语义）
        if (_activePickWindow != null && GodotObject.IsInstanceValid(_activePickWindow))
        {
            _activePickWindow.SelectByUid(uid);
            GD.Print($"[Arena] 角标展开面板点选 {uid}（置选中，不提交）");
        }
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

    /// <summary>延迟收起：鼠标离开角标后再等一小会儿；若期间又进入角标/面板则取消。</summary>
    private void ScheduleHideBadgePeek()
    {
        if (_badgePeek == null || !GodotObject.IsInstanceValid(_badgePeek) || !_badgePeek.Visible) return;
        int token = ++_badgePeekHideToken;
        var timer = GetTree()?.CreateTimer(0.25);
        if (timer == null) { _badgePeek.HideNow(); return; }
        timer.Timeout += () =>
        {
            if (token != _badgePeekHideToken) return;
            if (_badgePeek == null || !GodotObject.IsInstanceValid(_badgePeek)) return;
            if (_badgePeek.ContainsGlobalPoint(GetGlobalMousePosition())) return;   // 鼠标还在面板上
            _badgePeek.HideNow();
        };
    }
}
