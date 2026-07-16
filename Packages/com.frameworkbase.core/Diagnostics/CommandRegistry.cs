using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Cysharp.Threading.Tasks;

namespace Framework.Diagnostics
{
    /// <summary>
    /// 命令授权级别（有序：高级别涵盖低级别的全部权限）。
    /// 注册表默认 <see cref="None"/>（fail-closed）：未经组合根 / 业务显式授权，任何命令不可执行。
    /// </summary>
    public enum CommandAccessLevel
    {
        /// <summary>未授权：任何命令不可执行。正式包的默认态。</summary>
        None = 0,

        /// <summary>白名单授权：正式包中 GM 白名单账号经业务侧验证后授予，可执行 Privileged 命令。</summary>
        Privileged = 1,

        /// <summary>开发授权：Editor / Development Build 由组合根授予，可执行全部命令。</summary>
        Development = 2,
    }

    /// <summary>单条命令的元数据（注册后不可变）。</summary>
    public sealed class CommandInfo
    {
        /// <param name="name">命令名：非空、不含空白字符；查找不区分大小写。</param>
        /// <param name="description">一句话说明（help 列表展示用）。</param>
        /// <param name="usage">参数用法示例（如 "loglevel &lt;debug|log|warning|error&gt;"），无参命令可空。</param>
        /// <param name="requiredAccess">
        /// 执行所需授权级别。默认 <see cref="CommandAccessLevel.Development"/>（最保守）：
        /// 只有明确评估过「白名单账号在正式包可用」的命令才降到 Privileged。
        /// </param>
        public CommandInfo(
            string name,
            string description,
            string usage = null,
            CommandAccessLevel requiredAccess = CommandAccessLevel.Development)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("命令名不能为空。", nameof(name));
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c))
                    throw new ArgumentException($"命令名 '{name}' 不能含空白字符。", nameof(name));
            }
            if (requiredAccess == CommandAccessLevel.None)
                throw new ArgumentException("命令不允许声明 None 级别（等于无门禁）。", nameof(requiredAccess));

            Name = name;
            Description = description ?? string.Empty;
            Usage = usage ?? string.Empty;
            RequiredAccess = requiredAccess;
        }

        /// <summary>命令名（注册时原样保留大小写，查找不区分）。</summary>
        public string Name { get; }

        /// <summary>一句话说明。</summary>
        public string Description { get; }

        /// <summary>参数用法示例；空串表示无参。</summary>
        public string Usage { get; }

        /// <summary>执行所需授权级别。</summary>
        public CommandAccessLevel RequiredAccess { get; }
    }

    /// <summary>命令执行结果。</summary>
    public readonly struct CommandResult
    {
        private CommandResult(bool success, string message)
        {
            Success = success;
            Message = message ?? string.Empty;
        }

        /// <summary>是否执行成功。</summary>
        public bool Success { get; }

        /// <summary>结果说明（成功回执或失败原因），控制台原样展示。</summary>
        public string Message { get; }

        /// <summary>成功结果。</summary>
        public static CommandResult Ok(string message = null) => new CommandResult(true, message);

        /// <summary>失败结果。</summary>
        public static CommandResult Fail(string message) => new CommandResult(false, message);
    }

    /// <summary>
    /// 类型化取参失败（缺参 / 格式不合法）。由 <see cref="CommandRegistry.ExecuteAsync"/> 统一兜住，
    /// 转换为带 Usage 提示的失败结果——命令实现内直接用 GetXxx 取参即可，无需自行校验再报错。
    /// </summary>
    public sealed class CommandArgumentException : Exception
    {
        public CommandArgumentException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// 命令参数：token 化后的位置参数与类型化取值。
    /// GetXxx 系列在缺参 / 格式不合法时抛 <see cref="CommandArgumentException"/>（执行器兜住转失败结果）；
    /// GetXxxOrDefault 系列在缺参时返回默认值，但参数存在而格式不合法仍抛出——写错了应该被看见，而不是静默吞掉。
    /// </summary>
    public sealed class CommandArgs
    {
        private readonly IReadOnlyList<string> _tokens;

        internal CommandArgs(string rawLine, IReadOnlyList<string> tokens)
        {
            RawLine = rawLine;
            _tokens = tokens;
        }

        /// <summary>原始命令行（含命令名本身），需要整行透传的命令用。</summary>
        public string RawLine { get; }

        /// <summary>位置参数个数（不含命令名）。</summary>
        public int Count => _tokens.Count;

        /// <summary>取第 <paramref name="index"/> 个字符串参数（0 起）。</summary>
        public string GetString(int index)
        {
            if (index < 0 || index >= _tokens.Count)
                throw new CommandArgumentException($"缺少第 {index + 1} 个参数。");
            return _tokens[index];
        }

        /// <summary>取字符串参数；缺参返回 <paramref name="defaultValue"/>。</summary>
        public string GetStringOrDefault(int index, string defaultValue = null)
            => index >= 0 && index < _tokens.Count ? _tokens[index] : defaultValue;

        /// <summary>取整型参数。</summary>
        public int GetInt(int index)
        {
            string token = GetString(index);
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new CommandArgumentException($"第 {index + 1} 个参数 '{token}' 不是整数。");
            return value;
        }

        /// <summary>取整型参数；缺参返回默认值（存在但格式不合法仍抛出）。</summary>
        public int GetIntOrDefault(int index, int defaultValue)
            => index >= 0 && index < _tokens.Count ? GetInt(index) : defaultValue;

        /// <summary>取浮点参数。</summary>
        public float GetFloat(int index)
        {
            string token = GetString(index);
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                throw new CommandArgumentException($"第 {index + 1} 个参数 '{token}' 不是数字。");
            return value;
        }

        /// <summary>取浮点参数；缺参返回默认值（存在但格式不合法仍抛出）。</summary>
        public float GetFloatOrDefault(int index, float defaultValue)
            => index >= 0 && index < _tokens.Count ? GetFloat(index) : defaultValue;

        /// <summary>取布尔参数：true/false/1/0/on/off（不区分大小写）。</summary>
        public bool GetBool(int index)
        {
            string token = GetString(index);
            switch (token.ToLowerInvariant())
            {
                case "true":
                case "1":
                case "on":
                    return true;
                case "false":
                case "0":
                case "off":
                    return false;
                default:
                    throw new CommandArgumentException(
                        $"第 {index + 1} 个参数 '{token}' 不是布尔值（true/false/1/0/on/off）。");
            }
        }

        /// <summary>取布尔参数；缺参返回默认值（存在但格式不合法仍抛出）。</summary>
        public bool GetBoolOrDefault(int index, bool defaultValue)
            => index >= 0 && index < _tokens.Count ? GetBool(index) : defaultValue;
    }

    /// <summary>一次命令执行的审计记录（GM 命令使用审计 / 埋点上报的挂接点）。</summary>
    public readonly struct CommandExecutionRecord
    {
        internal CommandExecutionRecord(string name, string rawLine, bool success, string message, TimeSpan duration)
        {
            Name = name;
            RawLine = rawLine;
            Success = success;
            Message = message;
            Duration = duration;
        }

        /// <summary>命令名；未知命令时为输入的首 token。</summary>
        public string Name { get; }

        /// <summary>原始命令行。</summary>
        public string RawLine { get; }

        /// <summary>是否执行成功。</summary>
        public bool Success { get; }

        /// <summary>结果说明。</summary>
        public string Message { get; }

        /// <summary>执行耗时。</summary>
        public TimeSpan Duration { get; }
    }

    /// <summary>
    /// 调试命令总线：显式注册（不走反射扫描——IL2CPP 裁剪与热更程序集的扫描时机都是坑）、
    /// fail-closed 权限门禁（默认 <see cref="CommandAccessLevel.None"/>，未授权任何命令不可执行）。
    /// <para>
    /// 纯 C# 无 Unity 依赖，EditMode 可直接实例化测试。线程约定：全部成员仅主线程访问
    /// （调试命令的注册与执行天然发生在主线程，不为不存在的并发场景付锁的代价）。
    /// </para>
    /// <para>
    /// 授权由组合根 / 业务显式驱动：Editor 与 Development Build 由 GameEntry 授予 Development；
    /// 正式包默认 None，GM 白名单账号经业务侧服务端验证后授予 Privileged，登出时撤销。
    /// </para>
    /// </summary>
    public sealed class CommandRegistry
    {
        private sealed class Entry
        {
            public CommandInfo Info;
            public Func<CommandArgs, UniTask<CommandResult>> Handler;
        }

        private readonly Dictionary<string, Entry> _commands =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>注销句柄：Dispose 即注销（幂等）。命令被同名重注册前提是先注销，句柄只注销自己那次注册。</summary>
        private sealed class Registration : IDisposable
        {
            private CommandRegistry _owner;
            private Entry _entry;

            public Registration(CommandRegistry owner, Entry entry)
            {
                _owner = owner;
                _entry = entry;
            }

            public void Dispose()
            {
                CommandRegistry owner = _owner;
                Entry entry = _entry;
                _owner = null;
                _entry = null;
                if (owner == null || entry == null)
                    return;
                // 只在表里仍是自己这次注册时才移除，避免误删后来者的同名注册。
                if (owner._commands.TryGetValue(entry.Info.Name, out Entry current) && ReferenceEquals(current, entry))
                    owner._commands.Remove(entry.Info.Name);
            }
        }

        /// <summary>当前授权级别。默认 None（fail-closed）。</summary>
        public CommandAccessLevel GrantedAccess { get; private set; } = CommandAccessLevel.None;

        /// <summary>已注册命令总数（不按授权过滤）。</summary>
        public int Count => _commands.Count;

        /// <summary>
        /// 每次执行尝试（含未知命令与权限拒绝）后触发，用于 GM 使用审计 / 埋点。
        /// 观察者异常被隔离，不影响命令结果。
        /// </summary>
        public event Action<CommandExecutionRecord> Executed;

        /// <summary>
        /// 设置授权级别（覆盖式，可升可降——登出撤销白名单即降回 None）。
        /// 只允许组合根与业务鉴权路径调用，命令实现内不得自我提权。
        /// </summary>
        public void SetGrantedAccess(CommandAccessLevel level)
        {
            GrantedAccess = level;
        }

        /// <summary>
        /// 注册命令（异步处理器）。同名（不区分大小写）已存在时抛异常——命令名冲突是装配错误，
        /// 应当在开发期炸出来而不是静默覆盖；确需替换先 Unregister。
        /// </summary>
        /// <returns>注销句柄：Dispose 即注销（幂等，且不会误删后来的同名注册）。</returns>
        public IDisposable Register(CommandInfo info, Func<CommandArgs, UniTask<CommandResult>> handler)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (_commands.ContainsKey(info.Name))
                throw new InvalidOperationException($"命令 '{info.Name}' 已注册；同名注册是装配错误，确需替换先 Unregister。");

            var entry = new Entry { Info = info, Handler = handler };
            _commands.Add(info.Name, entry);
            return new Registration(this, entry);
        }

        /// <summary>注册命令（同步处理器）。</summary>
        public IDisposable Register(CommandInfo info, Func<CommandArgs, CommandResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return Register(info, args => UniTask.FromResult(handler(args)));
        }

        /// <summary>按名注销。返回是否存在并被移除。</summary>
        public bool Unregister(string name)
        {
            return !string.IsNullOrEmpty(name) && _commands.Remove(name);
        }

        /// <summary>按名查询命令元数据（不受授权过滤——查询不等于执行）。</summary>
        public bool TryGet(string name, out CommandInfo info)
        {
            if (!string.IsNullOrEmpty(name) && _commands.TryGetValue(name, out Entry entry))
            {
                info = entry.Info;
                return true;
            }
            info = null;
            return false;
        }

        /// <summary>列出当前授权级别下可执行的命令（按名排序），供 help 与自动补全。</summary>
        public IReadOnlyList<CommandInfo> ListAvailable()
        {
            var result = new List<CommandInfo>();
            foreach (Entry entry in _commands.Values)
            {
                if (entry.Info.RequiredAccess <= GrantedAccess)
                    result.Add(entry.Info);
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// 解析并执行一行命令。所有失败路径（空行 / 未知命令 / 权限不足 / 参数错误 / 处理器异常）
        /// 一律返回失败结果而不向外抛——控制台输入不该炸掉调用方。
        /// </summary>
        public async UniTask<CommandResult> ExecuteAsync(string commandLine)
        {
            long started = Stopwatch.GetTimestamp();

            if (string.IsNullOrWhiteSpace(commandLine))
                return Finish("", commandLine ?? string.Empty, CommandResult.Fail("空命令。"), started);

            IReadOnlyList<string> tokens = Tokenize(commandLine);
            string name = tokens[0];

            if (!_commands.TryGetValue(name, out Entry entry))
            {
                return Finish(name, commandLine,
                    CommandResult.Fail($"未知命令 '{name}'（输入 help 查看可用命令）。"), started);
            }

            if (entry.Info.RequiredAccess > GrantedAccess)
            {
                // 不区分「命令不存在」与「权限不足」会更隐蔽，但调试总线的用户是自己人，明说更省排障时间。
                return Finish(name, commandLine,
                    CommandResult.Fail($"权限不足：'{entry.Info.Name}' 需要 {entry.Info.RequiredAccess} 授权（当前 {GrantedAccess}）。"),
                    started);
            }

            var argTokens = new string[tokens.Count - 1];
            for (int i = 1; i < tokens.Count; i++)
                argTokens[i - 1] = tokens[i];
            var args = new CommandArgs(commandLine, argTokens);

            try
            {
                CommandResult result = await entry.Handler(args);
                return Finish(entry.Info.Name, commandLine, result, started);
            }
            catch (CommandArgumentException ex)
            {
                string usage = string.IsNullOrEmpty(entry.Info.Usage) ? entry.Info.Name : entry.Info.Usage;
                return Finish(entry.Info.Name, commandLine,
                    CommandResult.Fail($"参数错误：{ex.Message} 用法：{usage}"), started);
            }
            catch (Exception ex)
            {
                return Finish(entry.Info.Name, commandLine,
                    CommandResult.Fail($"命令执行异常：{ex.GetType().Name}: {ex.Message}"), started);
            }
        }

        /// <summary>落审计记录并返回结果；观察者异常隔离（诊断出口不反噬命令结果）。</summary>
        private CommandResult Finish(string name, string rawLine, CommandResult result, long startedTimestamp)
        {
            long ticks = Stopwatch.GetTimestamp() - startedTimestamp;
            var duration = TimeSpan.FromSeconds(Math.Max(0, ticks) / (double)Stopwatch.Frequency);
            try
            {
                Executed?.Invoke(new CommandExecutionRecord(name, rawLine, result.Success, result.Message, duration));
            }
            catch
            {
                // 审计观察者自身的异常没有更下游的去处，不能反噬命令执行结果。
            }
            return result;
        }

        /// <summary>
        /// 命令行 token 化：按空白分隔，双引号内保持整体（含空格），引号不闭合时宽松处理为取到行尾。
        /// 调试输入以宽松不炸为先，不实现转义等 shell 级语法。
        /// </summary>
        public static IReadOnlyList<string> Tokenize(string line)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(line))
                return tokens;

            int i = 0;
            int length = line.Length;
            var buffer = new System.Text.StringBuilder(32);
            while (i < length)
            {
                // 跳过分隔空白
                while (i < length && char.IsWhiteSpace(line[i])) i++;
                if (i >= length) break;

                buffer.Length = 0;
                bool inQuotes = false;
                while (i < length)
                {
                    char c = line[i];
                    if (c == '"')
                    {
                        inQuotes = !inQuotes;
                        i++;
                        continue;
                    }
                    if (!inQuotes && char.IsWhiteSpace(c))
                        break;
                    buffer.Append(c);
                    i++;
                }
                tokens.Add(buffer.ToString());
            }
            return tokens;
        }
    }
}
