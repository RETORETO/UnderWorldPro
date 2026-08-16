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

        /// <summary>运行所有以 Test 开头的方法（无参，返回 void，内部用 Assert）。</summary>
        public void RunAll()
        {
            _failures.Clear();
            _passed.Clear();
            var methods = GetType().GetMethods();
            foreach (var m in methods)
            {
                if (!m.Name.StartsWith("Test") || m.GetParameters().Length != 0) continue;
                try
                {
                    m.Invoke(this, null);
                    _passed.Add(m.Name);
                }
                catch (AssertFailedException ex)
                {
                    _failures.Add($"[FAIL] {m.Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    var inner = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    // 打印完整堆栈，便于定位异常出处（诊断用；定位后可视情改回精简）
                    var stack = inner.StackTrace?.Replace("\n", " | ") ?? "";
                    _failures.Add($"[ERR ] {m.Name}: 异常 {inner.GetType().Name}: {inner.Message} @ {stack}");
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
