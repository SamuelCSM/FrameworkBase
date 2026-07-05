using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// MonoBehaviour单例基类（支持DontDestroyOnLoad）
    /// </summary>
    /// <typeparam name="T">单例类型</typeparam>
    public abstract class MonoSingleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;
        private static bool _applicationIsQuitting = false;

        /// <summary>
        /// 获取单例实例。
        /// 实例由场景挂载对象（或显式实例化对象）在 <see cref="Awake"/> 中注册，
        /// 本属性不做任何隐式查找或自动创建（遵循运行时禁止隐式查找的硬约束）：
        /// 若在实例存在前访问，将返回 null 并记录错误，提示调整初始化顺序。
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_applicationIsQuitting)
                {
                    Debug.LogWarning($"[MonoSingleton] '{typeof(T)}' 已在应用退出时销毁，不再返回实例。");
                    return null;
                }

                if (_instance == null)
                {
                    Debug.LogError(
                        $"[MonoSingleton] '{typeof(T)}' 实例不存在。" +
                        "请确保其已挂载到启动场景并先于访问点执行 Awake，框架不做隐式查找/自动创建。");
                }

                return _instance;
            }
        }

        /// <summary>
        /// Awake生命周期（子类重写时必须调用base.Awake()）
        /// </summary>
        protected virtual void Awake()
        {
            if (_instance == null)
            {
                _instance = this as T;
                DontDestroyOnLoad(gameObject);
            }
            else if (_instance != this)
            {
                Debug.LogWarning($"[MonoSingleton] Instance of '{typeof(T)}' already exists. Destroying duplicate.");
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// 应用退出时标记
        /// </summary>
        protected virtual void OnApplicationQuit()
        {
            _applicationIsQuitting = true;
        }

        /// <summary>
        /// 销毁时清理实例
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>
        /// 销毁单例实例
        /// </summary>
        public static void DestroyInstance()
        {
            if (_instance != null)
            {
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }
    }
}
