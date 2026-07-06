using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace Framework.Analytics
{
    /// <summary>
    /// 埋点传输后端抽象。
    ///
    /// 分工：<see cref="AnalyticsManager"/> 负责事件封装（公共维度）、缓冲、批量、
    /// 重试退避与断电落盘；后端只负责"把一批已序列化的事件送出去"。
    /// 对接三方平台（ThinkingData / Firebase 等）时在扩展包实现本接口并
    /// 经 <see cref="AnalyticsManager.SetBackend"/> 注入，框架主干不含厂商 SDK。
    /// </summary>
    public interface IAnalyticsBackend
    {
        /// <summary>后端标识（日志用）。</summary>
        string Name { get; }

        /// <summary>
        /// 发送一批事件（每项为单条事件的 JSON 文本）。
        /// 返回 false 表示本批发送失败，管理器会保留事件并退避重试；
        /// 实现内部不要抛异常（网络错误一律折算为 false）。
        /// </summary>
        UniTask<bool> SendAsync(IReadOnlyList<string> eventJsonBatch);
    }
}
