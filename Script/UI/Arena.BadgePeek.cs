using Godot;
using Script.Game.Entities;
using Script.UI;
using System.Collections.Generic;

/// <summary>
/// 【P3a/P3b】角标悬浮展开（单机 Arena 侧）。
///
/// 流程：`CardBadges`（宿主卡右下角）被悬浮 → 本文件解析宿主实例上的成员（附属 + 封印）→
/// `CardBadgePeek` 在宿主旁横向展开卡面 → 点击成员 → 若当前有 `CardPickWindow` 且该卡在候选内，
/// 调 `SelectByUid`（**只置选中、不提交**）；否则纯浏览。
///
/// 语义红线：展开面板**不参与任何游戏逻辑**，不写 Model、不触发结算、不替代「确认」。
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
        CardBadges.OnBadgeHoverEnd = _ => ScheduleHideBadgePeek();
    }

    private void DetachBadgePeek()
    {
        if (CardBadges.OnBadgeHover == (System.Action<Control>)ShowBadgePeek) CardBadges.OnBadgeHover = null;
        CardBadges.OnBadgeHoverEnd = null;
        _badgePeek?.HideNow();
    }

    /// <summary>展开宿主卡上的成员列表。</summary>
    private void ShowBadgePeek(Control host)
    {
        if (host == null || !GodotObject.IsInstanceValid(host)) return;
        var inst = host is CardUI c ? c.Instance : (host as CouncilCardUI)?.Instance;
        if (inst == null) return;

        var members = new List<CardInstance>();
        if (inst.AttachedCards != null)
            foreach (var m in inst.AttachedCards)
                if (m != null) members.Add(m);
        if (inst.SealedCards != null)
            foreach (var s in inst.SealedCards)
                if (s != null && ReferenceEquals(s.SealedBy, inst)) members.Add(s);
        if (members.Count == 0) return;

        _badgePeekHideToken++;                       // 取消待执行的收起
        CardPreviewBus.Overlay?.HidePreview();       // 同一时刻只留一个浮层（展开面板优先）

        if (_badgePeek == null || !GodotObject.IsInstanceValid(_badgePeek))
        {
            _badgePeek = new CardBadgePeek { Name = "CardBadgePeek" };
            AddChild(_badgePeek);
        }
        _badgePeek.ShowNear(host, members, OnBadgeMemberPicked);
    }

    private void OnBadgeMemberPicked(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        // 只置选中：仍需用户在选择窗里按「确认」才提交（与方案 4.5 的场上点选同一语义）
        if (_activePickWindow != null && GodotObject.IsInstanceValid(_activePickWindow))
            _activePickWindow.SelectByUid(uid);
        GD.Print($"[Arena] 角标展开面板点选 {uid}（置选中，不提交）");
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
