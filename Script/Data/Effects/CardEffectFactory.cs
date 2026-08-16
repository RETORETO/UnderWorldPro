using System.Collections.Generic;
using Script.Data.Cards;

namespace Script.Data.Effects
{
    /// <summary>
    /// 效果工厂：按卡 UID 查表构建该卡的效果（含 TargetRules 等静态字段与 EffectDataList）。
    ///
    /// 背景：原实现每张卡一个继承 PlayerCardData 的 Resource 类（Godot 启动需为每个类注册脚本），
    ///       700+ 卡导致启动/编译开销大。现改为"普通静态注册类 + 统一工厂"：
    ///       - 卡片静态数据仍存 .tres，统一绑定通用 PlayerCardData（不再每卡一个资源类）；
    ///       - 每张卡的效果逻辑迁入静态注册类（不继承 GodotObject），Godot 启动不注册；
    ///       - 加载卡模板时调 EnsureEffects，按 UID 查表构建效果填入模板。
    ///
    /// Builder 直接修改传入的 PlayerCardData（设置 TargetRules / EffectDataList 等），
    /// 因此既能承载效果列表，也能承载 JC012/JC015/JC035/JC068 等在构造函数里写的 TargetRules 声明。
    /// </summary>
    public static class CardEffectFactory
    {
        /// <summary>单卡效果构建器：直接改写 data 的 TargetRules / EffectDataList 等字段。</summary>
        public delegate void Builder(PlayerCardData data);

        private static readonly Dictionary<string, Builder> _builders = new();

        /// <summary>注册一张卡的效果构建器。重复注册以最新覆盖（幂等安全）。</summary>
        public static void Register(string uid, Builder b) => _builders[uid] = b;

        /// <summary>该卡是否已注册效果构建器（未注册=空骨架/未实现，效果为空）。</summary>
        public static bool IsRegistered(string uid) => _builders.ContainsKey(uid);

        /// <summary>
        /// 幂等填充：为卡模板构建效果（仅执行一次，避免共享 .tres 资源被重复 Clear/Add 污染）。
        /// 未注册的卡（空骨架/未实现）效果为空列表，不报错，与旧"空构造函数"行为一致。
        /// </summary>
        public static void EnsureEffects(PlayerCardData data)
        {
            if (data == null) return;
            if (data._effectsInjected) return;

            data.TargetRules?.Clear();
            data.EffectDataList?.Clear();

            // 空 UID（null/空串）视为未注册，跳过：避免 Dictionary<string,...>.TryGetValue(null) 抛
            // ArgumentNullException: key。任何无 UID 的卡模板（测试/空骨架/未实现卡）均走此安全路径，
            // 与下方"未注册的卡效果为空列表、不报错"的设计一致。
            if (string.IsNullOrEmpty(data.UID))
            {
                data._effectsInjected = true;
                return;
            }

            if (_builders.TryGetValue(data.UID, out var b))
                b(data);

            data._effectsInjected = true;
        }
    }
}
