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

        /// <summary>
        /// 【P3·门禁 + 分支】"触发门禁满足后，再按**另一条**条件在两条动作序列间二选一"的谓词。
        ///
        /// 卡文形态："每当 X 时，…（动作 A）；**若 X 是 Y**，则改为…（动作 B）"——需要**两条**独立条件：
        ///  · <see cref="Predicate"/> ＝ **触发/结算门禁**（收集阶段过滤 + 结算复检；不满足 → 走 <see cref="ElseBranch"/>）；
        ///  · `BranchPredicate`（可空）＝ 门禁满足后的**分支选择**：**不满足** → 也走 <see cref="ElseBranch"/>。
        /// 因此"门禁满足 ∧ 分支满足" → 执行 `Actions`；否则执行 `ElseBranch`。
        ///
        /// 用于 JC031 唤灵师：「每当**另一个本方角色**死亡时，你可以令目标玩家弃一张手牌；
        /// **若死亡的是吸血鬼角色**，则改为可以令目标玩家随机弃一张手牌」
        /// → 门禁＝`DiedAllyCharacter`（保证非吸血鬼的本方角色死亡同样能触发），分支＝`DiedVampireCharacter`。
        ///
        /// 留空（缺省）时行为与既有实现**完全一致**（只按 `Predicate` 判定）。
        /// </summary>
        /// <summary>
        /// 【P3·K5】**参数化谓词的参数**（缺省空）。用于需要"带一个数字/标志名"的条件，如
        /// `Predicate = "AreaMarkerAtLeast"` + `Param = "时间标志,3"`（JC127「若该地区有三个或更多时间标志」）。
        /// 由 `EffectEngine.EvaluateCondition(..., param)` 透传给谓词实现；既有谓词忽略它。
        /// </summary>
        [Export] public string Param { get; set; } = "";

        [Export] public string BranchPredicate { get; set; } = "";

        /// <summary>条件不满足时执行的动作序列（"否则改为…"）。</summary>
        [Export] public Array<EffectAction> ElseBranch { get; set; } = new Array<EffectAction>();
    }
}
