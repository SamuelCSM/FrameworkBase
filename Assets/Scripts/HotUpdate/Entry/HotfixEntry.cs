using UnityEngine;

namespace HotUpdate.Entry
{
    /// <summary>
    /// 热更新入口契约的框架参考实现。
    /// <para>
    /// <see cref="Framework.HotUpdate.HotUpdateManager.StartHotfix"/> 通过反射查找
    /// <c>HotUpdate.Entry.HotfixEntry</c> 类型并调用其 <see cref="Start"/> 方法——这是框架
    /// 硬编码的启动契约。业务项目应在自己的 HotUpdate 程序集中以同名类型承接真实游戏逻辑；
    /// 本实现保证框架仓库自身的发布 → 客户端消费链路（release-rehearsal）可在无业务代码时闭环演练。
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
            Debug.Log("[HotfixEntry] 框架参考热更入口已启动（纯框架模式，无业务逻辑）。");
        }
    }
}
