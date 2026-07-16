using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace Framework
{
    /// <summary>
    /// 资源服务瘦接口：业务消费端最常用的加载 / 实例化 / 释放 / 作用域子集，
    /// 用作测试替身 / 解耦注入的缝。基础的按址加载与归还继承自
    /// <see cref="IResourceScopeHost"/>（ResourceScope 已用它做替身的先例）。
    /// <para>
    /// 刻意不求全：Catalog 热更、下载量预估、缓存清理等启动链路运维面只在具体类上
    /// （LaunchFlow / 组合根经 <c>GameEntry.Resource</c> 访问）。
    /// </para>
    /// </summary>
    public interface IResourceService : IResourceScopeHost
    {
        /// <summary>加载资源（带下载/加载进度回调）。</summary>
        UniTask<T> LoadAssetAsync<T>(string address, Action<float> onProgress) where T : UnityEngine.Object;

        /// <summary>批量预加载到缓存（进度按完成个数回调）。</summary>
        UniTask PreloadAssetsAsync(List<string> addresses, Action<float> onProgress = null);

        /// <summary>按标签整组释放（与按标签预热配对使用）。</summary>
        void ReleaseAssetsByLabel(string label);

        /// <summary>创建资源作用域：作用域内借出的资源随 Dispose 一次性归还。</summary>
        ResourceScope CreateScope(string name);
    }
}
