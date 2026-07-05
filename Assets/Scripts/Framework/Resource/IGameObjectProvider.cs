using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// GameObject 实例提供者接口。
    /// <para>
    /// 该接口只描述“如何获得实例、如何归还实例”，不关心实例被 UI、特效、场景表现还是其他系统使用。
    /// 上层系统应自行解释实例的业务生命周期。
    /// </para>
    /// </summary>
    public interface IGameObjectProvider
    {
        /// <summary>
        /// 获取指定 key 对应的 GameObject 实例。
        /// </summary>
        /// <param name="key">资源地址、池 key 或其他实例来源标识。</param>
        /// <param name="parent">实例挂载父节点，可为空。</param>
        /// <returns>实例获取成功时返回 GameObject，否则返回 null。</returns>
        UniTask<GameObject> GetAsync(string key, Transform parent = null);

        /// <summary>
        /// 归还或释放实例。
        /// </summary>
        /// <param name="instance">需要归还的实例。</param>
        void Release(GameObject instance);
    }
}
