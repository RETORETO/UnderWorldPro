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
    }
}
