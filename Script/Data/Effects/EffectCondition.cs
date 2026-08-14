using Godot;
using Godot.Collections;

namespace Script.Data.Effects
{
    /// <summary>结算时条件判定（含否则改为分支）。区别于 ActivationRequirement（发动许可）。
    /// 继承 RefCounted：见 EffectAction 说明（引用计数，避免 GC 误回收）。</summary>
    [GlobalClass]
    public partial class EffectCondition : RefCounted
    {
        /// <summary>条件谓词文本（如"心灵领域忠诚≥2""成功封印""已横置"）。</summary>
        [Export] public string Predicate { get; set; } = "Always";

        /// <summary>条件不满足时执行的动作序列（"否则改为…"）。</summary>
        [Export] public Array<EffectAction> ElseBranch { get; set; } = new Array<EffectAction>();
    }
}
