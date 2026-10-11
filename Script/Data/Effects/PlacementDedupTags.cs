namespace Script.Data.Effects
{
    /// <summary>
    /// 【放置去重标签】`ActionVerb.PlaceToken` 的 `Args[2]`＝**去重标签**（单一真相源）。
    ///
    /// 机制（广播窗口去重）：
    ///  · `EffectEngine.PlaceToken` 读 `Args[2]`：**非空即启用去重**（不再有硬编码白名单），
    ///    去重桶＝该标签，键＝`地区UID|控制者`；
    ///  · 窗口的边界由**广播点**划定：每个广播点（如 `GameEvent.OnCombatWon` / `OnPowerCombatEnded`）
    ///    在 Emit 前调用 `EffectEngine.BeginPlacementDedupWindow(tag)` ⇒ **只清该标签的桶**；
    ///  · 因此"同一次广播内，同一控制者同一地区只放 1 枚"，而**不同标签互不干扰**（各自独立分桶）。
    ///
    /// 不变式（窗口之间不会交错）：两次广播之间堆叠必已清空
    /// （`TurnManager.Pass`：堆叠非空只结算栈顶、不推进阶段；堆叠为空才 `AdvancePhase`）。
    /// 若将来新增"堆叠非空时也能广播"的触点，必须先改成"收集触发时打窗口戳"再广播。
    ///
    /// 使用：关键词模板（`KeywordEffects.WeiMing()` / `Renown()`）与广播点
    /// （`TriggerEmitPoints.OnCombatWon` / `PowerCombatEnded`）**必须引用同一常量**，避免字符串漂移。
    /// </summary>
    public static class PlacementDedupTags
    {
        /// <summary>【威名】战斗对抗获胜（`OnCombatWon`）：单次战斗对抗内每控制者每地区只放 1 枚。</summary>
        public const string WeiMing = "威名";

        /// <summary>【声望】每次势力对抗结束（`OnPowerCombatEnded`）：单次势力对抗内每控制者每地区只放 1 枚。</summary>
        public const string Renown = "声望";
    }
}
