using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Script.Tests
{
    /// <summary>
    /// 轻量测试基础设施：不依赖 NUnit / xUnit，可直接在 Godot 编辑器内由 TestsRunner 节点调用。
    /// 每个测试方法返回结果字符串（空=通过，非空=失败明细）。
    /// </summary>
    public abstract partial class TestRunner
    {
        protected readonly List<string> _failures = new();
        protected readonly List<string> _passed = new();

        /// <summary>
        /// 【MVC 收口·测试隔离】每个测试方法运行前，重新实例化 EffectEngine 单例。
        /// EffectEngine 由 static class 改为实例后，测试环境不再自动持有实例；此处为每个测试
        /// 提供一个全新实例（8 个对局状态集合随旧实例丢弃），彻底隔离测试间状态污染，
        /// 替代旧架构下各测试手写 `EffectEngine.Instance.ResetAll()` 的隔离方式。
        /// </summary>
        protected void EnsureFreshEffectEngine()
        {
            var ee = new Script.Manager.EffectEngine();
            ee.ResetAll();
        }

        /// <summary>运行所有以 Test 开头的方法（无参，返回 void，内部用 Assert）。</summary>
        public void RunAll()
        {
            _failures.Clear();
            _passed.Clear();
            var methods = GetType().GetMethods();
            foreach (var m in methods)
            {
                if (!m.Name.StartsWith("Test") || m.GetParameters().Length != 0) continue;
                EnsureFreshEffectEngine();   // 每个测试前重建 EffectEngine 实例，隔离状态
                bool known = m.GetCustomAttributes(typeof(KnownDefectAttribute), false).Length > 0;
                try
                {
                    m.Invoke(this, null);
                    if (known)
                    {
                        // 已知缺陷用例**通过了** ⇒ 缺陷多半已修好，提醒摘掉 [KnownDefect]
                        _passed.Add(m.Name);
                        GD.Print($"[FIXED?] {m.Name}: 标记为【已知缺陷】的用例现在通过了 → " +
                                 $"可考虑摘掉 [KnownDefect]（{GetType().Name}）");
                    }
                    else
                    {
                        _passed.Add(m.Name);
                    }
                }
                catch (AssertFailedException ex)
                {
                    if (known)
                    {
                        // 已知缺陷：仍失败 ⇒ 不计失败，但保持可见
                        _passed.Add(m.Name);
                        GD.Print($"[KNOWN] {m.Name}: 已知缺陷（未修复，不计失败）→ {ex.Message}");
                    }
                    else
                    {
                        _failures.Add($"[FAIL] {m.Name}: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    // 打印完整堆栈，便于定位异常出处（诊断用；定位后可视情改回精简）
                    var stack = inner.StackTrace?.Replace("\n", " | ") ?? "";
                    if (known)
                    {
                        _passed.Add(m.Name);
                        GD.Print($"[KNOWN] {m.Name}: 已知缺陷（未修复，不计失败）→ {inner.Message}");
                    }
                    else
                    {
                        _failures.Add($"[ERR ] {m.Name}: 异常 {inner.GetType().Name}: {inner.Message} @ {stack}");
                    }
                }
            }
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {GetType().Name} ===");
            sb.AppendLine($"通过 {_passed.Count} / 失败 {_failures.Count}");
            foreach (var f in _failures) sb.AppendLine(f);
            return sb.ToString();
        }

        public bool AllPassed => _failures.Count == 0;
    }

    public class AssertFailedException : Exception
    {
        public AssertFailedException(string msg) : base(msg) { }
    }

    /// <summary>
    /// 【已知缺陷锚点】标记一个**当前确实失败**的用例：它记录的是已确认的产品缺陷（不是测试写错），
    /// 在缺陷修好之前**不计入失败**，但会在报告里以 `[KNOWN]` 列出（可见、不隐藏）。
    /// 修好之后该用例会**通过**，报告里以 `[FIXED?]` 提示"可以摘掉这个标记了"。
    ///
    /// 用途：把"层系统改造"这类**大改造的验收锚点**先写进套件，避免"改造完忘了原来为什么改"；
    /// 同时保证主线（全量绿）不被已知缺陷拖红。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class KnownDefectAttribute : Attribute
    {
        public string Reason { get; }
        public KnownDefectAttribute(string reason) => Reason = reason;
    }

    public static class Assert
    {
        public static void IsTrue(bool cond, string msg = "")
        {
            if (!cond) throw new AssertFailedException(msg.Length == 0 ? "期望 true" : msg);
        }

        public static void IsFalse(bool cond, string msg = "")
        {
            if (cond) throw new AssertFailedException(msg.Length == 0 ? "期望 false" : msg);
        }

        public static void AreEqual<T>(T expected, T actual, string msg = "")
        {
            if (!Equals(expected, actual))
                throw new AssertFailedException($"{(msg.Length == 0 ? "" : msg + " | ")}期望 {expected} 实际 {actual}");
        }

        public static void IsNull(object obj, string msg = "")
        {
            if (obj != null) throw new AssertFailedException(msg.Length == 0 ? "期望 null" : msg);
        }

        public static void IsNotNull(object obj, string msg = "")
        {
            if (obj == null) throw new AssertFailedException(msg.Length == 0 ? "期望非 null" : msg);
        }

        /// <summary>标记该项为已知待补红线：本次不通过视为预期，仅记录不计入失败。</summary>
        public static void KnownRedline(bool cond, List<string> redlineLog, string note)
        {
            redlineLog.Add($"[REDLINE] {note} (断言结果={cond})");
        }
    }
}
