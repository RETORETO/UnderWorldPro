using Godot;
using Godot.Collections;
using Script.Data.Effects;
using Script.Enums;

namespace Script.Data.Cards
{
    /// <summary>
    /// 议会（秘社）激活选项（CouncilActivationOption）
    /// 秘社卡可含多个激活效果，玩家双击秘社 UI 时弹出窗口从中二选一发动（或取消不发动）。
    /// 每个选项独立承载：名称、费用、声明式目标规则、结构化效果列表（EffectDataList）。
    /// 本类为 Godot Resource（可序列化），可在 .cs 构造函数或 .tres 中配置；
    /// 但 EffectData 是 RefCounted（不序列化进 .tres），故 EffectDataList 仍由秘社卡 .cs 在构造时填充。
    /// </summary>
    [GlobalClass]
    public partial class CouncilActivationOption : Resource
    {
        /// <summary>选项展示名（UI 窗口中显示），如"效果A：抽取密文"</summary>
        [Export] public string OptionName { get; set; } = "";

        /// <summary>激活费用（Cost），-1 表示该选项不可主动发动</summary>
        [Export] public int Cost { get; set; } = 0;

        /// <summary>声明式目标规则（TargetRules）—— UI 选目标用，tres 可配，cs 亦可构造</summary>
        [Export]
        public Godot.Collections.Array<Script.Data.Effects.TargetRule> TargetRules { get; set; }
            = new Godot.Collections.Array<Script.Data.Effects.TargetRule>();

        /// <summary>结构化效果列表（EffectDataList）—— 由秘社卡 .cs 构造时填充，不序列化进 .tres</summary>
        public Godot.Collections.Array<Script.Data.Effects.EffectData> EffectDataList { get; set; }
            = new Godot.Collections.Array<Script.Data.Effects.EffectData>();

        /// <summary>防御性兜底：确保 EffectDataList 始终非空，避免运行时 NRE</summary>
        public CouncilActivationOption()
        {
            EffectDataList ??= new Godot.Collections.Array<Script.Data.Effects.EffectData>();
        }
    }
}
