using Godot;
using System.Collections.Generic;

namespace Script.Data.Cards
{
    /// <summary>
    /// 所有卡牌静态模板的基类，继承 Resource 以便作为 .tres 文件保存
    /// </summary>
    [GlobalClass] // 加上这个，可以在编辑器中右键创建资源
    public abstract partial class BaseCardData : Resource
    {
        [Export] public string UID { get; set; }           // 唯一标识
        [Export] public string CardName { get; set; }      // 卡名
        [Export] public int Score { get; set; } = 0;       // 分数
    }
}