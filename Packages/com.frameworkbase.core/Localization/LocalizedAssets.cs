using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Core;

namespace Framework
{
    /// <summary>
    /// 本地化资源加载编排：按 <see cref="LocalizedAssetResolver"/> 的候选链逐个探测存在性，
    /// 取首个命中的实际地址加载。解析结果按（语言 + 地址）缓存，同一资源只探测一次。
    /// <para>
    /// 释放契约：资源经 ResourceManager 引用计数管理，释放必须用<b>解析后的实际地址</b>
    /// （<see cref="LoadAsync{T}"/> 随资源一并返回）而非基础地址。
    /// Catalog 热更后存在性可能变化：<see cref="ResourceManager.CheckAndUpdateCatalogsAsync"/> 在 Catalog
    /// 实际更新后已自动调 <see cref="ClearCache"/> 失效缓存，业务无需手动介入（走非标准热更路径时才需自调）。
    /// 线程约定：仅主线程访问。
    /// </para>
    /// </summary>
    public static class LocalizedAssets
    {
        /// <summary>解析缓存：「语言\n基础地址」→ 实际地址。语言进 key，切语言天然失效。</summary>
        private static readonly Dictionary<string, string> ResolvedCache =
            new Dictionary<string, string>(System.StringComparer.Ordinal);

        /// <summary>
        /// 解析基础地址在指定语言下的实际加载地址（候选链首个存在者；全部不存在返回原始地址，
        /// 由后续加载报标准错误，不在此处吞掉）。
        /// </summary>
        /// <param name="baseAddress">不带语言后缀的原始地址。</param>
        /// <param name="language">目标语言；null 用当前语言。</param>
        public static async UniTask<string> ResolveAsync(string baseAddress, string language = null)
        {
            string lang = Language.NormalizeLanguage(language ?? Language.CurrentLanguage);
            string cacheKey = lang + "\n" + baseAddress;
            if (ResolvedCache.TryGetValue(cacheKey, out string cached))
                return cached;

            IReadOnlyList<string> candidates = LocalizedAssetResolver.GetCandidates(baseAddress, lang);
            string resolved = candidates[candidates.Count - 1]; // 末位恒为原始地址兜底
            for (int i = 0; i < candidates.Count - 1; i++)
            {
                if (await GameEntry.Resource.ExistsAsync(candidates[i]))
                {
                    resolved = candidates[i];
                    break;
                }
            }

            ResolvedCache[cacheKey] = resolved;
            return resolved;
        }

        /// <summary>
        /// 按当前语言加载本地化资源。返回资源与<b>实际地址</b>——释放时必须用它调
        /// <c>GameEntry.Resource.ReleaseAsset(address)</c>，用基础地址释放会漏计数。
        /// </summary>
        public static async UniTask<(T asset, string address)> LoadAsync<T>(string baseAddress)
            where T : UnityEngine.Object
        {
            string resolved = await ResolveAsync(baseAddress);
            T asset = await GameEntry.Resource.LoadAssetAsync<T>(resolved);
            return (asset, resolved);
        }

        /// <summary>清空解析缓存。Catalog 热更完成后调用（资源存在性可能已变化）。</summary>
        public static void ClearCache()
        {
            ResolvedCache.Clear();
        }
    }
}
