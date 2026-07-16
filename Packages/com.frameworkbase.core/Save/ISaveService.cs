using Cysharp.Threading.Tasks;

namespace Framework.Save
{
    /// <summary>
    /// 存档服务瘦接口：业务消费端的读 / 写 / 删子集，用作测试替身 / 解耦注入的缝。
    /// <para>
    /// 刻意不求全：账号目录切换（SetCurrentUser / ClearCurrentUser）是组合根在登录 /
    /// 登出编排里的职责、加密密钥注入是装配期配置，均只在具体类
    /// <see cref="SaveManager"/> 上；PlayerPrefs 包装属轻量偏好、不进存档抽象。
    /// </para>
    /// </summary>
    public interface ISaveService
    {
        /// <summary>保存到当前账号目录的指定槽位。</summary>
        UniTask SaveAsync<T>(T data, int slot = 0) where T : SaveData;

        /// <summary>从当前账号目录读取；不存在返回新实例。</summary>
        UniTask<T> LoadAsync<T>(int slot = 0) where T : SaveData, new();

        /// <summary>删除指定类型/槽位存档。</summary>
        void DeleteSave<T>(int slot = 0) where T : SaveData;

        /// <summary>清空当前账号的全部存档。</summary>
        void DeleteCurrentUserSaves();
    }
}
