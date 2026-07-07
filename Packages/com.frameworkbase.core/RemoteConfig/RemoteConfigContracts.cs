using Cysharp.Threading.Tasks;

namespace Framework.RemoteConfig
{
    /// <summary>
    /// 单次拉取携带的客户端属性，供服务端做条件定向（按渠道/版本/环境下发不同配置）。
    /// 静态 CDN 文件后端会忽略这些参数（此时定向逻辑在客户端：功能开关的 rollout / min_version 字段）。
    /// </summary>
    public struct RemoteConfigRequest
    {
        public string DeviceId;
        public string UserId;
        public string AppVersion;
        public string Channel;
        public string Env;
    }

    /// <summary>
    /// 远程配置拉取后端抽象。
    ///
    /// 分工：<see cref="RemoteConfigManager"/> 负责默认值合并、磁盘缓存（last-known-good）、
    /// 类型化取值与功能开关判定；后端只负责"取回一份配置 JSON 文本"。
    /// 对接三方平台（Firebase Remote Config 等）时在扩展包实现本接口并
    /// 经 <see cref="RemoteConfigManager.SetBackend"/> 注入，框架主干不含厂商 SDK。
    /// </summary>
    public interface IRemoteConfigBackend
    {
        /// <summary>后端标识（日志用）。</summary>
        string Name { get; }

        /// <summary>
        /// 拉取配置，返回顶层为对象的 JSON 文本；失败返回 null / 空串
        /// （管理器保留现值，不抛异常——网络错误一律折算为 null）。
        /// </summary>
        UniTask<string> FetchAsync(RemoteConfigRequest request);
    }
}
