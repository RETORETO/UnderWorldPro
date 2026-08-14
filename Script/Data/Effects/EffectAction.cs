using Godot;
using Godot.Collections;

namespace Script.Data.Effects
{
    /// <summary>动作原语（EffectAction.Verb）。未实现的执行器后续补全，枚举值保留即可。</summary>
    public enum ActionVerb
    {
        // 基础姿态
        Tap,            // 横置
        Untap,          // 重置
        // 存亡/位移
        Destroy,        // 消灭
        Bounce,         // 移回拥有者手牌/牌库
        Move,           // 移动至另一地区（机动）
        Sacrifice,      // 牺牲（自毁换效果）
        // 属性/图标增减
        Gain,           // 获得属性/图标（+X防御、［黑刀］等）
        Lose,           // 失去属性/图标
        // 结附/封印/遏制
        Attach,         // 结附
        Seal,           // 封印（由封印系统实现）
        Curb,           // 遏制X（动作：敌方玩家选择并移除X个任意地区的敌方势力标记，不取目标）
        // 搜牌/展示/洗牌
        Search,         // 从牌库寻牌
        Reveal,         // 展示
        Shuffle,        // 洗牌
        // 资源/费用
        Pay,            // 支付费用（名誉点等）
        PrepareUpkeep,  // 准备步骤维护：支付N费用，否则横置来源（JC068 阿格斯IV二选一）
        // 伤害
        DealDamage,     // 造成伤害
        PreventDamage,  // 防止伤害
        // 标志/指示物
        PlaceToken,     // 放置标志/指示物
        RemoveToken,    // 移除标志/指示物
        GenerateToken,  // 生成 token/附属
        // 对抗（特殊原语，接入 ComatManager/InvestigationManager）
        Investigate,    // 调查对抗
        Fight,          // 战斗对抗
        Power,          // 势力对抗
        // 胜负
        Win,            // 胜负判定
        // 资源/抽牌
        Draw,           // 抓牌（从牌库顶加入手牌）
        DrawFromBottom, // 抓牌（从牌库底加入手牌）
    }

    /// <summary>单个动作原语及其参数。
    /// 继承 RefCounted（而非 GodotObject）：效果数据由卡片 C# 脚本构建并挂入 EffectDataList，
    /// 不序列化进 .tres（故无需 Resource）；但 RefCounted 提供引用计数，避免 GodotObject
    /// 在 GC 时被误回收导致 EffectDataList 元素失效。无 [Export] 即不进 .tres。</summary>
    [GlobalClass]
    public partial class EffectAction : RefCounted
    {
        /// <summary>动作类型。</summary>
        [Export] public ActionVerb Verb { get; set; }

        /// <summary>动作参数列表（数值/图标/属性类型/区域等，由具体 Verb 解释）。</summary>
        [Export] public Array<string> Args { get; set; } = new Array<string>();
    }
}
