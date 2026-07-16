using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Diagnostics;
using NUnit.Framework;

namespace Framework.Tests
{
    public class CommandRegistryTests
    {
        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();

        private static CommandRegistry NewDevRegistry()
        {
            var registry = new CommandRegistry();
            registry.SetGrantedAccess(CommandAccessLevel.Development);
            return registry;
        }

        // ── 注册 / 注销 ─────────────────────────────────────────────────────

        [Test]
        public void 注册后可执行_同名重复注册抛异常()
        {
            CommandRegistry registry = NewDevRegistry();
            registry.Register(new CommandInfo("echo", "回显"), args => CommandResult.Ok(args.GetString(0)));

            CommandResult result = Wait(registry.ExecuteAsync("echo hello"));
            Assert.IsTrue(result.Success);
            Assert.AreEqual("hello", result.Message);

            // 同名（含大小写不同）重复注册是装配错误，必须炸出来
            Assert.Throws<InvalidOperationException>(() =>
                registry.Register(new CommandInfo("ECHO", "重复"), _ => CommandResult.Ok()));
        }

        [Test]
        public void 注销句柄Dispose即注销_且不误删后来的同名注册()
        {
            CommandRegistry registry = NewDevRegistry();
            IDisposable first = registry.Register(new CommandInfo("cmd", "旧"), _ => CommandResult.Ok("old"));

            registry.Unregister("cmd");
            registry.Register(new CommandInfo("cmd", "新"), _ => CommandResult.Ok("new"));

            // 旧句柄迟到 Dispose：不得把后来者的同名注册删掉
            first.Dispose();
            first.Dispose(); // 幂等

            CommandResult result = Wait(registry.ExecuteAsync("cmd"));
            Assert.IsTrue(result.Success);
            Assert.AreEqual("new", result.Message);
        }

        [Test]
        public void 命令名校验_空名与含空白拒绝_None级别拒绝()
        {
            Assert.Throws<ArgumentException>(() => new CommandInfo("", "x"));
            Assert.Throws<ArgumentException>(() => new CommandInfo("a b", "x"));
            Assert.Throws<ArgumentException>(() =>
                new CommandInfo("ok", "x", requiredAccess: CommandAccessLevel.None));
        }

        // ── 权限门禁 ────────────────────────────────────────────────────────

        [Test]
        public void 默认None授权_任何命令不可执行()
        {
            var registry = new CommandRegistry();
            registry.Register(
                new CommandInfo("gm", "白名单命令", requiredAccess: CommandAccessLevel.Privileged),
                _ => CommandResult.Ok());

            CommandResult result = Wait(registry.ExecuteAsync("gm"));
            Assert.IsFalse(result.Success);
            StringAssert.Contains("权限不足", result.Message);
        }

        [Test]
        public void Privileged授权_可执行白名单命令_不可执行Dev命令()
        {
            var registry = new CommandRegistry();
            registry.Register(
                new CommandInfo("gm", "白名单命令", requiredAccess: CommandAccessLevel.Privileged),
                _ => CommandResult.Ok());
            registry.Register(
                new CommandInfo("dev", "开发命令"), // 默认 Development
                _ => CommandResult.Ok());

            registry.SetGrantedAccess(CommandAccessLevel.Privileged);
            Assert.IsTrue(Wait(registry.ExecuteAsync("gm")).Success);
            Assert.IsFalse(Wait(registry.ExecuteAsync("dev")).Success);

            // 登出撤销授权后全部拒绝（可降级）
            registry.SetGrantedAccess(CommandAccessLevel.None);
            Assert.IsFalse(Wait(registry.ExecuteAsync("gm")).Success);
        }

        [Test]
        public void ListAvailable按授权过滤并按名排序()
        {
            var registry = new CommandRegistry();
            registry.Register(new CommandInfo("zeta", "dev 命令"), _ => CommandResult.Ok());
            registry.Register(
                new CommandInfo("alpha", "白名单命令", requiredAccess: CommandAccessLevel.Privileged),
                _ => CommandResult.Ok());

            Assert.AreEqual(0, registry.ListAvailable().Count, "None 授权不暴露任何命令");

            registry.SetGrantedAccess(CommandAccessLevel.Privileged);
            IReadOnlyList<CommandInfo> privileged = registry.ListAvailable();
            Assert.AreEqual(1, privileged.Count);
            Assert.AreEqual("alpha", privileged[0].Name);

            registry.SetGrantedAccess(CommandAccessLevel.Development);
            IReadOnlyList<CommandInfo> all = registry.ListAvailable();
            Assert.AreEqual(2, all.Count);
            Assert.AreEqual("alpha", all[0].Name);
            Assert.AreEqual("zeta", all[1].Name);
        }

        // ── 执行与失败路径 ──────────────────────────────────────────────────

        [Test]
        public void 未知命令与空行_返回失败不抛出()
        {
            CommandRegistry registry = NewDevRegistry();

            CommandResult unknown = Wait(registry.ExecuteAsync("nope"));
            Assert.IsFalse(unknown.Success);
            StringAssert.Contains("未知命令", unknown.Message);

            Assert.IsFalse(Wait(registry.ExecuteAsync("")).Success);
            Assert.IsFalse(Wait(registry.ExecuteAsync("   ")).Success);
            Assert.IsFalse(Wait(registry.ExecuteAsync(null)).Success);
        }

        [Test]
        public void 处理器异常被兜住_转为失败结果()
        {
            CommandRegistry registry = NewDevRegistry();
            registry.Register(new CommandInfo("boom", "抛异常"),
                (Func<CommandArgs, CommandResult>)(_ => throw new InvalidOperationException("炸了")));

            CommandResult result = Wait(registry.ExecuteAsync("boom"));
            Assert.IsFalse(result.Success);
            StringAssert.Contains("InvalidOperationException", result.Message);
            StringAssert.Contains("炸了", result.Message);
        }

        [Test]
        public void 参数错误_失败信息附带Usage()
        {
            CommandRegistry registry = NewDevRegistry();
            registry.Register(
                new CommandInfo("loglevel", "设置日志级别", usage: "loglevel <debug|log|warning|error>"),
                args => CommandResult.Ok(args.GetString(0)));

            CommandResult result = Wait(registry.ExecuteAsync("loglevel"));
            Assert.IsFalse(result.Success);
            StringAssert.Contains("参数错误", result.Message);
            StringAssert.Contains("loglevel <debug|log|warning|error>", result.Message);
        }

        [Test]
        public void 异步处理器_挂起后完成才返回结果()
        {
            // 不用 UniTask.Yield：EditMode 下同步 Wait 一个依赖 PlayerLoop 的挂起任务会直接抛
            // "Not yet completed"。改用完成源手动控制挂起→完成，真实覆盖异步路径。
            CommandRegistry registry = NewDevRegistry();
            var gate = new UniTaskCompletionSource();
            registry.Register(new CommandInfo("async", "异步命令"), async _ =>
            {
                await gate.Task;
                return CommandResult.Ok("done");
            });

            UniTask<CommandResult> pending = registry.ExecuteAsync("async");
            Assert.AreEqual(UniTaskStatus.Pending, pending.Status, "处理器挂起期间执行不得提前返回");

            gate.TrySetResult();
            CommandResult result = Wait(pending);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("done", result.Message);
        }

        // ── 参数解析 ────────────────────────────────────────────────────────

        [Test]
        public void Tokenize_空白分隔_双引号保持整体_引号不闭合宽松取到行尾()
        {
            CollectionAssert.AreEqual(
                new[] { "cmd", "a", "b c", "d" },
                (System.Collections.ICollection)CommandRegistry.Tokenize("cmd  a \"b c\"  d"));

            CollectionAssert.AreEqual(
                new[] { "cmd", "tail with spaces" },
                (System.Collections.ICollection)CommandRegistry.Tokenize("cmd \"tail with spaces"));

            Assert.AreEqual(0, CommandRegistry.Tokenize("").Count);
            Assert.AreEqual(0, CommandRegistry.Tokenize("   ").Count);
        }

        [Test]
        public void 类型化取参_合法转换_非法抛CommandArgumentException()
        {
            var args = new CommandArgs("cmd 42 3.5 on oops",
                new[] { "42", "3.5", "on", "oops" });

            Assert.AreEqual(42, args.GetInt(0));
            Assert.AreEqual(3.5f, args.GetFloat(1));
            Assert.IsTrue(args.GetBool(2));
            Assert.AreEqual("oops", args.GetString(3));

            Assert.Throws<CommandArgumentException>(() => args.GetInt(3));
            Assert.Throws<CommandArgumentException>(() => args.GetBool(3));
            Assert.Throws<CommandArgumentException>(() => args.GetString(4), "越界缺参");

            // OrDefault：缺参回默认值；参数存在但格式不合法仍抛（写错了应该被看见）
            Assert.AreEqual(7, args.GetIntOrDefault(9, 7));
            Assert.Throws<CommandArgumentException>(() => args.GetIntOrDefault(3, 7));
        }

        // ── 审计事件 ────────────────────────────────────────────────────────

        [Test]
        public void 每次执行尝试落审计记录_观察者异常被隔离()
        {
            CommandRegistry registry = NewDevRegistry();
            registry.Register(new CommandInfo("ok", "成功命令"), _ => CommandResult.Ok("done"));

            var records = new List<CommandExecutionRecord>();
            registry.Executed += r => records.Add(r);
            registry.Executed += _ => throw new Exception("观察者炸了");

            Assert.IsTrue(Wait(registry.ExecuteAsync("ok")).Success, "观察者异常不得反噬命令结果");
            Wait(registry.ExecuteAsync("nope"));

            Assert.AreEqual(2, records.Count);
            Assert.AreEqual("ok", records[0].Name);
            Assert.IsTrue(records[0].Success);
            Assert.AreEqual("nope", records[1].Name);
            Assert.IsFalse(records[1].Success);
        }
    }
}
