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

        /// <summary>
        /// 【2026-09·JC031 用户口径】门禁与分支**都满足**时，"是否**改为**执行主分支"由发动者
        /// **在结算时**选择（卡文"…则**可以**改为…"的那一个"可以"）：
        ///  · `true` → 结算时弹 **0/1 二选一**窗口（`AskPrompt`／`AskYesLabel`／`AskNoLabel`），
        ///    询问**来源控制者**（＝"发动效果的玩家"）：选「是」→ `Actions`；选「否」/取消 → `ElseBranch`；
        ///  · `false`（缺省）→ 行为与既有实现完全一致（门禁∧分支满足就直接 `Actions`）。
        ///
        /// 交互原语与 `SacrificeSelfOptional`／`PrepareUpkeep` 同款：单机
        /// `IGameUIHooks.PromptNumberInput(0/1)`、联机 `ChoiceRequest.Kind.NumberInput`（网关 0/1 回包）、
        /// 无 UI/网关的确定性兜底 = **「是」**（保留"分支成立即走主分支"的既有行为，便于测试与 headless）。
        /// 用于 JC031 唤灵师："若死亡的是吸血鬼角色，则**可以**令目标玩家随机弃一张手牌"
        /// （选是＝随机弃；选否＝走 ElseBranch＝由该玩家自选弃）。
        /// </summary>
        [Export] public bool AskBranch { get; set; } = false;

        /// <summary>`AskBranch` 的提问文案（空则用通用文案）。</summary>
        [Export] public string AskPrompt { get; set; } = "";

        /// <summary>`AskBranch` 的"是"按钮文案（空＝"是"）。</summary>
        [Export] public string AskYesLabel { get; set; } = "";

        /// <summary>`AskBranch` 的"否"按钮文案（空＝"否"）。</summary>
        [Export] public string AskNoLabel { get; set; } = "";

        /// <summary>条件不满足时执行的动作序列（"否则改为…"）。</summary>
        [Export] public Array<EffectAction> ElseBranch { get; set; } = new Array<EffectAction>();
    }
}
