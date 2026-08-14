namespace Script.UI
{
    /// <summary>
    /// 蒙版（基础行动）类型枚举（MVC 修复 M-08 提取为共享类型）。
    /// 原定义于 Arena 内部（private enum），但 CardDragController 需读取蒙版状态以分支出牌/基础行动，
    /// 提取为 Script.UI 下的 internal 枚举，供 Arena 与各协调器共用单一真相。
    /// </summary>
    internal enum MaskMode { None, DeployCovert, EstablishAsset }
}
