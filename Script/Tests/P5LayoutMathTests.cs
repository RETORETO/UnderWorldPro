using Godot;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-09·UI 布局】锁定"横置卡视觉中心"的**唯一正确算法**：
    /// Godot 的 `Control` 旋转是**绕 `PivotOffset`** 进行的 ⇒ 旋转后视觉中心**仍在** `Position + PivotOffset`
    /// （`GetGlobalTransform()` 的四角 AABB 中心），而**不是** `Position + PivotOffset.Rotated(角度)`。
    ///
    /// 为什么值得单测：附属/封印排版与 `PlaceCardInContainer` 都依赖这条几何关系；
    /// 历史上用 `PivotOffset.Rotated(90°)` 估算 ⇒ 宿主横置时算出的中心偏 **(120,20)**，
    /// 附属被摆到宿主的**左上方**（用户实测："部分情况下附属 UI 会显示在宿主上方"）。
    /// 若将来 Godot 语义变化（或有人"顺手"改回旋转公式），本用例会立刻失败。
    /// </summary>
    public class P5LayoutMathTests : TestRunner
    {
        public void TestLayout_旋转卡的视觉中心等于Position加PivotOffset()
        {
            var c = new Control { Size = new Vector2(100, 140) };
            c.PivotOffset = new Vector2(50, 70);
            c.Position = new Vector2(0, 0);
            c.RotationDegrees = 90f;

            var tree = Engine.GetMainLoop() as SceneTree;
            Assert.IsNotNull(tree, "测试运行于 Godot 场景树内");
            tree.Root.AddChild(c);

            var gt = c.GetGlobalTransform();
            Vector2 p00 = gt * new Vector2(0, 0);
            Vector2 pW0 = gt * new Vector2(100, 0);
            Vector2 p0H = gt * new Vector2(0, 140);
            Vector2 pWH = gt * new Vector2(100, 140);
            float minX = Mathf.Min(Mathf.Min(p00.X, pW0.X), Mathf.Min(p0H.X, pWH.X));
            float maxX = Mathf.Max(Mathf.Max(p00.X, pW0.X), Mathf.Max(p0H.X, pWH.X));
            float minY = Mathf.Min(Mathf.Min(p00.Y, pW0.Y), Mathf.Min(p0H.Y, pWH.Y));
            float maxY = Mathf.Max(Mathf.Max(p00.Y, pW0.Y), Mathf.Max(p0H.Y, pWH.Y));
            var aabbCenter = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
            var visSize = new Vector2(maxX - minX, maxY - minY);

            Assert.AreEqual(140f, visSize.X, "横置后视觉宽＝卡高 140");
            Assert.AreEqual(100f, visSize.Y, "横置后视觉高＝卡宽 100");
            Assert.AreEqual(c.Position + c.PivotOffset, aabbCenter,
                "视觉中心＝Position + PivotOffset（**不旋转**）——附属/封印排版与 PlaceCardInContainer 的基准");
            Assert.IsFalse(aabbCenter == c.Position + c.PivotOffset.Rotated(Mathf.Pi / 2f),
                "对照：Position + PivotOffset.Rotated(90°) 是**错的**（偏 (120,20)）");

            c.QueueFree();
        }

        /// <summary>
        /// 【2026-10·卡平滑归位】锁定"**旋转 Control 的 GlobalPosition 含枢轴项**"：
        /// 旋转后的 transform origin = Position + pivot − R·pivot（scale=1），90° 时多出 **(120,20)**。
        ///
        /// 为什么值得单测：`CardUI._Process` 用 `GlobalPosition.Lerp(TargetPosition, 0.15/帧)` 逼近目标。
        /// 历史上 `PlaceCardInContainer` 把目标写成 `container.GlobalPosition + localPos`（**漏了枢轴项**），
        /// ⇒ 横置卡到目标的距离恒为 121.7 > 0.5 ⇒ 每帧都被拉、**稳定收敛到 localPos − (120,20)**，
        /// 表现为"横置卡整体偏移"；而结附于它的附属用自锁写法 `TargetPosition = GlobalPosition`（不漂移）
        /// ⇒ **宿主漂移、附属不动 ⇒ 两者分离**。
        /// 现由 `CardLayoutHelper.SetSmoothTarget` 统一取 `card.GlobalPosition`，本用例防止有人改回旧算式。
        /// </summary>
        public void TestLayout_横置卡全局位置含枢轴项故平滑目标须取自GlobalPosition()
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            Assert.IsNotNull(tree, "测试运行于 Godot 场景树内");

            var parent = new Control { Size = new Vector2(400, 240), Position = new Vector2(500, 300) };
            tree.Root.AddChild(parent);

            var localPos = new Vector2(120, 50);
            var pivot = new Vector2(50, 70);

            // ---- ① 横置卡：GlobalPosition 比 Position 多出枢轴项 (120,20) ----
            var tapped = new Control { Size = new Vector2(100, 140) };
            tapped.PivotOffset = pivot;
            tapped.RotationDegrees = 90f;
            tapped.Position = localPos;
            parent.AddChild(tapped);

            // 旋转枢轴项推导：pivot − R(90°)·pivot = (50,70) − (−70,50) = (120,20)
            var pivotTerm = new Vector2(120, 20);
            Assert.AreEqual(parent.GlobalPosition + localPos + pivotTerm, tapped.GlobalPosition,
                "横置卡 GlobalPosition = Position + 枢轴项 (120,20)（旋转绕 PivotOffset ⇒ transform origin 带该项）");
            Assert.IsFalse(parent.GlobalPosition + localPos == tapped.GlobalPosition,
                "对照：`容器.GlobalPosition + localPos` 是**错的**平滑目标（少 (120,20)）");

            // Bug 现场：旧目标与 Lerp 起点距离 > 0.5 ⇒ _Process 每帧把横置卡拉偏；正确目标距离为 0 ⇒ 零漂移
            float distOld = tapped.GlobalPosition.DistanceTo(parent.GlobalPosition + localPos);
            float distCorrect = tapped.GlobalPosition.DistanceTo(tapped.GlobalPosition);
            Assert.IsTrue(distOld > 0.5f, "旧目标：距离 > 0.5 ⇒ _Process 每帧拉偏横置卡（Bug 现场）");
            Assert.IsTrue(distCorrect <= 0.5f,
                "正确目标＝卡自身 GlobalPosition ⇒ 与 Lerp 起点距离为 0 ⇒ 不漂移（SetSmoothTarget 的做法）");

            // ---- ② 未旋转卡：GlobalPosition == 容器全局 + localPos（新旧写法等价 ⇒ 修复无副作用） ----
            var upright = new Control { Size = new Vector2(100, 140) };
            upright.PivotOffset = pivot;
            upright.RotationDegrees = 0f;
            upright.Position = localPos;
            parent.AddChild(upright);
            Assert.AreEqual(parent.GlobalPosition + localPos, upright.GlobalPosition,
                "未旋转卡：GlobalPosition 恰为 容器.GlobalPosition + localPos（两种写法等价）");

            tapped.QueueFree();
            upright.QueueFree();
            parent.QueueFree();
        }
    }
}
