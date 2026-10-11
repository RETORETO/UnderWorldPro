using System;
using Godot;
using Script.Data.Cards;

namespace Script.UI
{
    /// <summary>
    /// 触发排序窗口内的触发卡条目（最小可拖拽单元）。
    /// 展示触发来源卡卡图、触发名称、效果文本、是否强制标记。
    /// 数据由 Arena / TurnManager 在打开排序窗口时注入。
    /// </summary>
    public partial class TriggerEntryUI : Control
    {
        [Export] public TextureRect CardArt;
        [Export] public Label NameLabel;
        [Export] public Label EffectText;
        [Export] public Label MandatoryBadge;

        /// <summary>触发数据载体。</summary>
        public object TriggerData { get; private set; }

        /// <summary>
        /// 该触发是否强制（强制触发不可拖入放弃区）。
        /// 新系统（EffectData 承载触发）已无"强制触发"概念——所有触发均为非强制、可由玩家选择入栈或放弃。
        /// 保留此字段仅为兼容 TriggerEntryUI 历史展示逻辑（徽章恒显"非强制"），不应在新逻辑中依赖。
        /// </summary>
        public bool IsMandatory { get; private set; }

        public void Setup(string name, string effectText, bool isMandatory, object triggerData = null, PlayerCardData sourceTemplate = null)
        {
            TriggerData = triggerData;
            IsMandatory = isMandatory;
            if (NameLabel != null) NameLabel.Text = name;
            if (EffectText != null) EffectText.Text = effectText;
            if (MandatoryBadge != null)
            {
                MandatoryBadge.Text = isMandatory ? "强制" : "非强制";
                MandatoryBadge.Modulate = isMandatory ? new Color(1f, 0.5f, 0.5f) : new Color(0.7f, 0.7f, 0.7f);
            }

            // 加载来源卡卡图（与 CardUI 一致的路径解析规则）
            if (CardArt != null && sourceTemplate != null && !string.IsNullOrEmpty(sourceTemplate.UID))
            {
                string path = ResolveCardImagePath(sourceTemplate.UID);
                var tex = ResourceLoader.Load<Texture2D>(path);
                if (tex != null) CardArt.Texture = tex;
                else GD.PrintErr($"[TriggerEntryUI] 卡图加载失败: {path}");
            }

            Name = $"TriggerEntry_{name}";
        }

        /// <summary>
        /// 该条目当前所在的放置区（由父窗口在拖拽/重排时维护）。
        /// 0 = 未设定；1 = OrderZone；2 = DiscardZone。
        /// </summary>
        public int CurrentZone { get; set; } = 0;

        /// <summary>
        /// 根据 UID 推断卡图目录（与 CardUI.ResolveCardImagePath 规则一致）：取开头连续字母作为目录名。
        /// </summary>
        private static string ResolveCardImagePath(string uid)
        {
            // 【2026-10-12 Web 修复】char.IsLetter 走 Unicode 类别表（全球化路径），Web 上会 abort；
            // 改用共享的 ASCII 前缀判定（卡号 UID 均为 ASCII，行为一致）。
            string folder = Script.IO.WebSafeText.LeadingAsciiLetters(uid, "JC");
            return $"res://Cards/Image/PlayerCards/{folder}/{uid}.jpg";
        }
    }
}
