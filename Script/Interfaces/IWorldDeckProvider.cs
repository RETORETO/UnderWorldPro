using Script.Data.Cards;
using System.Collections.Generic;

namespace Script.Interfaces
{
    /// <summary>
    /// 世界牌库数据供给抽象。WorldDeckManager 只依赖本接口，不依赖具体加载方式，
    /// 便于以后把本地资源加载（LocalWorldDeckProvider）替换为服务端下发（C/S 预留）。
    /// 本接口不依赖 Godot 与 Script.UI，可随 Controller 一起编译进服务端。
    /// </summary>
    public interface IWorldDeckProvider
    {
        List<AreaCardData> LoadAllAreas();
    }
}
