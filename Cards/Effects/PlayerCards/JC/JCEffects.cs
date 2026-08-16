namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC 系列效果统一注册入口。
    /// 加载本系列卡时调用 RegisterAll() 即可；按扩展包划分时，各系列独立注册。
    /// 幂等安全：CardEffectFactory.Register 以最新覆盖，重复调用无副作用。
    /// </summary>
    public static class JCEffects
    {
        public static void RegisterAll()
        {
            JC001_tonglingmaotouying_Effects.Register();
            JC002_jushizhenkanhuren_Effects.Register();
            JC003_weimuhuwei_Effects.Register();
            JC012_weimushouwanghuiji_Effects.Register();
            JC014_gongluqishi_Effects.Register();
            JC015_qumoren_Effects.Register();
            JC018_morenchuanren_Effects.Register();
            JC020_yinbidefeidao_Effects.Register();
            JC030_juxingbianfu_Effects.Register();
            JC035_chuanmeimozhou_Effects.Register();
            JC060_shenmixueguwen_Effects.Register();
            JC068_agesiIV_Effects.Register();
        }
    }
}
