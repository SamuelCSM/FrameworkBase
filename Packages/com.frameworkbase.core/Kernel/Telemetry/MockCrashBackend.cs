using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace Framework.Core.Telemetry
{
    /// <summary>
    /// 参考 / 测试用崩溃后端：不落盘、不联网，把所有调用记在内存，供单测断言与厂商实现对照。
    /// <b>不要用于正式包</b>——它不具备任何原生崩溃捕获能力（既不装信号处理器也不上报）。
    /// 厂商扩展包实现 <see cref="ICrashBackend"/> 时可对照本类理解各方法的调用时机与语义。
    /// </summary>
    public sealed class MockCrashBackend : ICrashBackend
    {
        /// <inheritdoc />
        public string Name => "mock";

        /// <summary>是否已被 <see cref="Install"/>。</summary>
        public bool Installed { get; private set; }

        /// <summary>Install 时收到的会话信息。</summary>
        public CrashSessionInfo Session { get; private set; }

        /// <summary>最近一次 <see cref="SetUser"/> 的用户 ID。</summary>
        public string UserId { get; private set; } = string.Empty;

        /// <summary>累计设置的自定义键。</summary>
        public readonly Dictionary<string, string> CustomKeys = new Dictionary<string, string>();

        /// <summary>累计留下的面包屑（按时间顺序）。</summary>
        public readonly List<string> Breadcrumbs = new List<string>();

        /// <summary>累计转发的托管异常。</summary>
        public readonly List<ManagedExceptionInfo> ManagedExceptions = new List<ManagedExceptionInfo>();

        /// <summary><see cref="TryFlushPendingAsync"/> 被调用次数。</summary>
        public int FlushCallCount { get; private set; }

        /// <summary>供测试指定 <see cref="TryFlushPendingAsync"/> 的返回值。</summary>
        public bool FlushResult { get; set; } = true;

        /// <inheritdoc />
        public void Install(in CrashSessionInfo session)
        {
            Installed = true;
            Session = session;
        }

        /// <inheritdoc />
        public void SetUser(string userId) => UserId = userId ?? string.Empty;

        /// <inheritdoc />
        public void SetCustomKey(string key, string value)
        {
            if (!string.IsNullOrEmpty(key)) CustomKeys[key] = value ?? string.Empty;
        }

        /// <inheritdoc />
        public void LeaveBreadcrumb(string message)
        {
            if (!string.IsNullOrEmpty(message)) Breadcrumbs.Add(message);
        }

        /// <inheritdoc />
        public void RecordManagedException(in ManagedExceptionInfo error) => ManagedExceptions.Add(error);

        /// <inheritdoc />
        public UniTask<bool> TryFlushPendingAsync()
        {
            FlushCallCount++;
            return UniTask.FromResult(FlushResult);
        }
    }
}
