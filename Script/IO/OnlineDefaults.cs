namespace Script.IO
{
    /// <summary>
    /// 【联机默认服务端】**唯一真源** —— 与工程根目录 `连服务器.bat` 里的 `HOST` / `PORT` 保持一致。
    ///
    /// 为什么要单独立一个类：默认地址原先散落在 `OnlineClientPrefs`（`127.0.0.1:18950`）、
    /// `OnlineClientContext`、`LobbyOl` 空值兜底、`Scenes/Net/LobbyOl.tscn` 的输入框初始文本四处，
    /// 改一次要改四遍、漏一处就会出现"输入框写着 A、实际连的是 B"。这里收成一份常量，其余地方全部引用它。
    ///
    /// ⚠ 换服务器时**两处一起改**：本文件的 `Host`/`Port` + 根目录 `连服务器.bat` 的 `HOST`/`PORT`。
    ///    （另：`Scenes/Net/LobbyOl.tscn` 里输入框的初始文本是纯字面量，仅作编辑器预览，运行时会被本值覆盖，
    ///      但为免误导也建议一并改。）
    /// </summary>
    public static class OnlineDefaults
    {
        /// <summary>公开测试服地址（见 `连服务器.bat`）。</summary>
        public const string Host = "120.76.248.92";

        /// <summary>公开测试服端口（见 `连服务器.bat`；注意与本地自测默认 18950 不同）。</summary>
        public const ushort Port = 28950;
    }
}
