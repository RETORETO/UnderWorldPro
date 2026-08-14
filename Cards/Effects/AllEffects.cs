namespace Cards.Effects
{
    /// <summary>
    /// 全卡效果统一注册入口。
    /// 正式发布 / 全量测试时调用 RegisterAll() 一次性注册所有系列的效果。
    /// 按扩展包迭代时，可只调用目标系列（如 Cards.Effects.PlayerCards.JC.JCEffects.RegisterAll()），
    /// 而非本类，以减少当前编译/注册范围。
    /// 幂等安全：CardEffectFactory.Register 以最新覆盖，重复调用无副作用。
    /// </summary>
    public static class AllEffects
    {
        public static void RegisterAll()
        {
            Cards.Effects.PlayerCards.JC.JCEffects.RegisterAll();
            Cards.Effects.PlayerCards.XG.XGEffects.RegisterAll();
            // 后续新增系列在此追加一行。
        }
    }
}
