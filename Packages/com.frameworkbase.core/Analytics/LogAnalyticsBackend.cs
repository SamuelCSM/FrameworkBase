using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace Framework.Analytics
{
    /// <summary>
    /// 日志埋点后端：未配置采集端点时的默认实现（开发期直接看 Console 验证埋点）。
    /// 永远返回成功，事件不出设备。
    /// </summary>
    public class LogAnalyticsBackend : IAnalyticsBackend
    {
        public string Name => "log";

        public UniTask<bool> SendAsync(IReadOnlyList<string> eventJsonBatch)
        {
            if (eventJsonBatch != null && GameLog.IsDebugEnabled)
            {
                foreach (string json in eventJsonBatch)
                    GameLog.Debug($"[Analytics] {json}");
            }
            return UniTask.FromResult(true);
        }
    }
}
