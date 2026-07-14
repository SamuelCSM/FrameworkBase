using HotUpdate.Clicker;
using UnityEngine;

namespace HotUpdate.Entry
{
    /// <summary>
    /// 热更新入口契约的框架参考实现。
    /// <para>
    /// <see cref="Framework.HotUpdate.HotUpdateManager.StartHotfix"/> 通过反射查找入口类型并调用其
    /// <see cref="Start"/> 方法。入口类型全名与入口程序集名均<b>可配置</b>（<c>AppConfig.HotUpdateEntryTypeFullName</c>
    /// / <c>AppConfig.HotUpdateEntryAssembly</c>），留空时回退框架默认 <c>HotUpdate.Entry.HotfixEntry</c>
    /// / <c>HotUpdate</c>（见 <see cref="Framework.HotUpdate.VersionManager.DefaultHotUpdateEntryTypeFullName"/>）。
    /// 业务项目可沿用该默认约定、或改配置指向自有入口类型；本实现保证框架仓库自身的发布 → 客户端消费链路
    /// （release-rehearsal）可在无业务代码时闭环演练。
    /// </para>
    /// <para>
    /// 该程序集经 HybridCLR 以 <c>HotUpdate.dll.bytes</c> 形式热更下发，属于远程代码执行链路，
    /// 任何改动都必须经由签名清单与完整性校验发布，禁止绕过发布管线手工分发。
    /// </para>
    /// </summary>
    public class HotfixEntry
    {
        /// <summary>
        /// 热更新逻辑启动点。框架在 AOT 元数据与全部热更程序集加载完成后调用；
        /// 正常返回即视为本次启动到达确认点（事务槽随后提升为 Last-Known-Good）。
        /// </summary>
        public void Start()
        {
            Debug.Log("[HotfixEntry] 框架参考热更入口已启动。");

            // 热更模式下 HotUpdate 程序集经 HybridCLR 运行时加载，
            // ClickerBootstrap 的 [RuntimeInitializeOnLoadMethod] 不会触发；
            // 故在此显式装配业务会话钩子（切片 D 接线）。Install 幂等，
            // 与离线整包路径不冲突：整包先由 RuntimeInitializeOnLoad 装配，
            // 此处再调只是重挂当前 GameEntry 钩子，无副作用。
            ClickerBootstrap.Install();
        }
    }
}
