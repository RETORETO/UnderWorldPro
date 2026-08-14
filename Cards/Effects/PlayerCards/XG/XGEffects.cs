namespace Cards.Effects.PlayerCards.XG
{
    /// <summary>
    /// XG 系列效果统一注册入口。
    /// 幂等安全：CardEffectFactory.Register 以最新覆盖，重复调用无副作用。
    /// </summary>
    public static class XGEffects
    {
        public static void RegisterAll()
        {
            XG001_yinyaotanqiuzhe_Effects.Register();
        }
    }
}
