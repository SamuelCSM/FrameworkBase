using Cysharp.Threading.Tasks;
using Framework.Core;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 基于 <see cref="ResourceManager"/> 的 Addressables GameObject 实例提供者。
    /// </summary>
    public sealed class AddressableGameObjectProvider : IGameObjectProvider
    {
        /// <summary>
        /// 通过 Addressables 实例化指定地址的 GameObject。
        /// </summary>
        /// <param name="key">Addressables Prefab 地址。</param>
        /// <param name="parent">实例挂载父节点，可为空。</param>
        /// <returns>实例化成功时返回 GameObject，否则返回 null。</returns>
        public async UniTask<GameObject> GetAsync(string key, Transform parent = null)
        {
            if (string.IsNullOrEmpty(key))
            {
                GameLog.Error("[AddressableGameObjectProvider] GetAsync 失败，key 为空");
                return null;
            }

            if (GameEntry.Resource == null)
            {
                GameLog.Error($"[AddressableGameObjectProvider] GetAsync 失败，ResourceManager 未就绪: {key}");
                return null;
            }

            return await GameEntry.Resource.InstantiateAsync(key, parent);
        }

        /// <summary>
        /// 释放通过 <see cref="ResourceManager"/> 创建的实例。
        /// </summary>
        /// <param name="instance">需要释放的实例。</param>
        public void Release(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            if (GameEntry.Resource != null)
            {
                GameEntry.Resource.ReleaseInstance(instance);
                return;
            }

            Object.Destroy(instance);
        }
    }
}
