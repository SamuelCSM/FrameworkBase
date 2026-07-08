using System;

namespace Framework.Core
{
    /// <summary>
    /// 线程安全的单例基类（普通C#类）
    /// </summary>
    /// <typeparam name="T">单例类型</typeparam>
    public abstract class Singleton<T> where T : class, new()
    {
        private static T _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// 获取单例实例（线程安全）
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new T();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// 构造函数（受保护，防止外部实例化）
        /// </summary>
        protected Singleton()
        {
        }

        /// <summary>
        /// 销毁单例实例
        /// </summary>
        public static void DestroyInstance()
        {
            lock (_lock)
            {
                _instance = null;
            }
        }
    }
}
