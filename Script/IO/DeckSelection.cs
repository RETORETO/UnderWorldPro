using Godot;

namespace Script.IO
{
    /// <summary>
    /// 【卡组选择记忆 · 2026-10-20】**"上次使用的卡组"的唯一取数口径**。
    ///
    /// 为什么要单独收一个类：
    ///   联机房间（`RoomOl`）与卡组编辑页（`DeckBuilder`）都要回答同一个问题 ——
    ///   "这个玩家现在应该用哪个卡组？"。各写一份必然会漂移（一边保存了、另一边还停在旧值），
    ///   所以把「枚举目录 → 解析上次使用 → 写回偏好 + 同步本场 GameManager」收在这里。
    ///
    /// 解析优先级**与房间页完全一致**（见 <see cref="DeckCatalog.ResolveInitialDeck"/>）：
    ///   偏好里记录的卡组（且文件确实存在） > `res://Decks/default_deck.json` > 目录第一个。
    ///
    /// 数据落地：`user://online_client.cfg` 的 `[online] last_deck_path`
    /// （沿用 <see cref="OnlineClientPrefs"/>，不另开配置文件，避免两份记忆互相打架）。
    /// </summary>
    public static class DeckSelection
    {
        /// <summary>
        /// 解析"当前应使用的卡组"路径。返回值保证非空（目录整个不可读时退回 <see cref="DeckCatalog.DefaultDeckPath"/>）。
        /// </summary>
        public static string ResolveDefault()
        {
            OnlineClientPrefs.EnsureLoaded();
            var paths = DeckCatalog.ListDeckPaths();
            return DeckCatalog.ResolveInitialDeck(paths, OnlineClientPrefs.LastDeckPath);
        }

        /// <summary>
        /// 记住某卡组为"上次使用/默认"：写盘 + 同步本场 <see cref="GameManager"/>
        /// （房间页与开局读的是 `SelectedDeckPath` / `UseSelectedDeck`）。
        ///
        /// `path` 为空表示"不用 JSON 卡组"（房间页的「默认卡组（硬编码）」）：
        /// 此时**只同步内存态、不写盘** —— 因为"硬编码"不是一个可持久化的文件，
        /// 写空值会把用户上次真正选过的卡组记录冲掉。
        /// </summary>
        public static void Remember(string path)
        {
            string p = path ?? "";
            OnlineClientPrefs.EnsureLoaded();
            _sessionChosen = true;          // 从此刻起，房间页以本场的值为准（含"硬编码"的空值）

            if (!string.IsNullOrEmpty(p))
            {
                if (!string.Equals(OnlineClientPrefs.LastDeckPath, p, System.StringComparison.Ordinal))
                    OnlineClientPrefs.SaveDeck(p);
            }

            var gm = GameManager.Instance;
            if (gm == null) return;
            gm.SelectedDeckPath = p;
            gm.UseSelectedDeck = !string.IsNullOrEmpty(p);
        }

        /// <summary>
        /// 本场是否已经"明确选过一次"（房间下拉 / 编辑页选卡组 / 保存）。
        /// 用来区分 `GameManager.SelectedDeckPath == ""` 到底是「用户选了硬编码卡组」还是「还没选过、待解析」。
        /// </summary>
        private static bool _sessionChosen;

        /// <summary>
        /// 房间页取"当前应选中的卡组"用：
        ///   · 本场已明确选过 → 用本场的（**可能是空串**＝「默认卡组（硬编码）」，必须保住这个选择）；
        ///   · 还没选过 → 用持久化偏好解析（跨重启恢复"上次使用的卡组"）。
        ///
        /// 注意：**不要**在 GameManager 里做启动注入 —— 那会改变"单机开局默认卡组"的来源，
        /// 实测会打挂一批与本需求无关的门禁/触发用例（详见 GameManager._Ready 的注释）。
        /// </summary>
        public static string ResolveForUi()
        {
            var gm = GameManager.Instance;
            if (_sessionChosen && gm != null) return gm.SelectedDeckPath ?? "";
            return ResolveDefault();
        }

        /// <summary>本场是否已明确选择过卡组（诊断用）。</summary>
        public static bool SessionChosen => _sessionChosen;
    }
}
