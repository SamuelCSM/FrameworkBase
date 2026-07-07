using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Framework.Core;
using Framework.Serialization;

namespace Framework.Save
{
    /// <summary>
    /// 本地存档管理器（单例）
    ///
    /// ── 账号隔离 ──────────────────────────────────────────────────────────
    /// 登录成功后调用一次 SetCurrentUser，后续所有存档自动隔离到该账号目录：
    ///   SaveManager.Instance.SetCurrentUser("10001");
    ///   → 存档路径：saves/u_10001/PlayerData_0.sav
    ///
    /// 未调用 SetCurrentUser 时走 guest 目录：saves/guest/
    /// 账号切换时再次调用 SetCurrentUser，自动切换到新账号目录。
    ///
    /// ── 每种类型独立文件，互不干扰 ───────────────────────────────────────
    ///   PlayerData   slot 0 → saves/u_10001/PlayerData_0.sav
    ///   ActivityData slot 0 → saves/u_10001/ActivityData_0.sav
    ///
    /// ── 存档 ──────────────────────────────────────────────────────────────
    ///   await SaveManager.Instance.SaveAsync(myData);
    ///   SaveManager.Instance.Save(myData);              // 不阻塞（Forget）
    ///
    /// ── 读档 ──────────────────────────────────────────────────────────────
    ///   var data = await SaveManager.Instance.LoadAsync<PlayerData>();
    ///   // 无存档时返回 new PlayerData()，不抛异常
    ///
    /// ── PlayerPrefs 轻量设置（全局，不区分账号）──────────────────────────
    ///   SaveManager.Instance.SetPref(PlayerSettings.MusicOn, true);
    ///   bool on = SaveManager.Instance.GetPref(PlayerSettings.MusicOn, defaultValue: true);
    /// </summary>
    public class SaveManager : Singleton<SaveManager>
    {
        // ── 当前账号 ─────────────────────────────────────────────────────────
        private string _currentUserId = "guest";

        // ── 按档案文件路径的串行化锁 ─────────────────────────────────────────
        // Key = 完整存档路径（已含账号/类型/slot），保证同一档案的读/写互斥，
        // 避免并发 SaveAsync 的 .tmp→备份→Move 交错损坏，以及读撞上「删主档到 Move」的空窗。
        // 不同档案各自独立锁，互不阻塞。
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>获取指定档案路径的串行化锁（按需创建，单一实例）。</summary>
        private SemaphoreSlim GetFileLock(string path)
            => _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        /// <summary>
        /// 当前账号 ID（只读）。未登录时为 "guest"。
        /// </summary>
        public string CurrentUserId => _currentUserId;

        /// <summary>
        /// 登录成功后调用，切换存档目录到该账号。
        /// userId 会被净化（只保留字母、数字、下划线），防止路径注入。
        /// 账号切换时再次调用即可，无需手动清理旧状态。
        /// </summary>
        public void SetCurrentUser(string userId)
        {
            var sanitized = SanitizeUserId(userId);
            if (string.IsNullOrEmpty(sanitized))
            {
                GameLog.Warning("[SaveManager] SetCurrentUser: userId 无效，保持 guest");
                return;
            }
            _currentUserId = sanitized;
            GameLog.Log($"[SaveManager] 当前账号已切换 → {_currentUserId}");
        }

        /// <summary>
        /// 退出登录，切回 guest 目录。
        /// </summary>
        public void ClearCurrentUser()
        {
            _currentUserId = "guest";
            GameLog.Log("[SaveManager] 已退出账号，切回 guest 目录");
        }

        // ── 磁盘封包格式 ─────────────────────────────────────────────────────
        // 完整性方案标识：旧档无此字段（m 为空）→ 裸 SHA-256；新档 = HMAC-SHA256
        private const string MacSchemeHmac = "hmac256";

        [Serializable]
        private class SaveEnvelope
        {
            public int    v;   // dataVersion at time of save
            public string h;   // 完整性码（按 m 区分：空=裸 SHA-256 旧档，hmac256=HMAC-SHA256）
            public string d;   // Base64(IV + AES ciphertext)
            public string m;   // 完整性方案标识；旧档无此字段反序列化为 null，按裸 SHA-256 兼容读取
        }

        /// <summary>
        /// 注入存档主密钥来源（默认绑定本设备，存档不可跨设备）。
        /// 需在任何读写存档之前调用——例如登录拿到账号/服务端下发密钥后。
        /// 注意：更换密钥来源会使此前用旧来源加密的存档无法解密，需配合迁移策略。
        /// </summary>
        public void SetSaveKeyProvider(ISaveKeyProvider provider)
            => AesHelper.SetKeyProvider(provider);

        // ── 路径 ─────────────────────────────────────────────────────────────
        // 目录结构：{persistentDataPath}/saves/{userId}/{TypeName}_{slot}.sav
        private string UserDir => Path.Combine(Application.persistentDataPath, "saves", $"u_{_currentUserId}");

        private string SlotPath<T>(int slot)   => Path.Combine(UserDir, $"{typeof(T).Name}_{slot}.sav");
        private string BackupPath<T>(int slot) => Path.Combine(UserDir, $"{typeof(T).Name}_{slot}.sav.bak");

        // ── 写档 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 异步写档（推荐）。
        /// 流程：JSON 序列化 → AES-128 加密 → HMAC-SHA256 完整性签名 → 原子写入（.tmp → rename）→ 备份旧档。
        /// </summary>
        public async UniTask SaveAsync<T>(T data, int slot = 0) where T : SaveData
        {
            var json      = JsonSerializers.Shared.ToJson(data, false);
            var encrypted = AesHelper.Encrypt(json);

            // 完整性一律用 HMAC-SHA256（encrypt-then-MAC）：篡改后无 MAC Key 无法重算合法完整性码
            var envelope = new SaveEnvelope
            {
                v = data.dataVersion,
                h = AesHelper.HmacSha256Hex(encrypted),
                d = Convert.ToBase64String(encrypted),
                m = MacSchemeHmac,
            };
            var envelopeJson = JsonSerializers.Shared.ToJson(envelope, false);

            var savePath   = SlotPath<T>(slot);
            var backupPath = BackupPath<T>(slot);
            var userDir    = UserDir; // 捕获当前账号目录，避免线程池上重算依赖 _currentUserId

            // 同一档案串行化：上方 JSON 序列化/加密已在调用线程（主线程）完成，
            // 此处仅串行化文件 IO，不影响主线程序列化语义。
            var fileLock = GetFileLock(savePath);
            await fileLock.WaitAsync();
            try
            {
                await UniTask.RunOnThreadPool(() =>
                {
                    Directory.CreateDirectory(userDir);

                    var tmp = savePath + ".tmp";

                    // 先写临时文件，防止写到一半崩溃损坏主档
                    File.WriteAllText(tmp, envelopeJson);

                    // 备份旧档
                    if (File.Exists(savePath))
                        File.Copy(savePath, backupPath, overwrite: true);

                    if (File.Exists(savePath)) File.Delete(savePath);
                    File.Move(tmp, savePath);
                });
            }
            finally
            {
                fileLock.Release();
            }

            GameLog.Log($"[SaveManager] 写档成功 user={_currentUserId} type={typeof(T).Name} slot={slot}");
        }

        /// <summary>
        /// 同步触发写档（内部 Forget，不阻塞调用方）。
        /// 关键档口（关卡结束、充值等）推荐 await SaveAsync 确保写入完成。
        /// </summary>
        public void Save<T>(T data, int slot = 0) where T : SaveData
            => SaveAsync(data, slot).Forget();

        // ── 读档 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 异步读档。主档损坏自动 fallback 备份；两者都失败返回 new T()。
        /// 读取后自动执行版本迁移（SaveData.OnMigrate）。
        /// </summary>
        public async UniTask<T> LoadAsync<T>(int slot = 0) where T : SaveData, new()
        {
            var savePath   = SlotPath<T>(slot);
            var backupPath = BackupPath<T>(slot);
            var userId     = _currentUserId;

            // 与同档案的写入互斥，避免读到写入过程中的中间态（删主档到 Move 之间的空窗）
            var fileLock = GetFileLock(savePath);
            await fileLock.WaitAsync();
            try
            {
                return await UniTask.RunOnThreadPool(() => LoadInternal<T>(slot, savePath, backupPath, userId));
            }
            finally
            {
                fileLock.Release();
            }
        }

        private T LoadInternal<T>(int slot, string savePath, string backupPath, string userId)
            where T : SaveData, new()
        {
            var paths = new[] { savePath, backupPath };

            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;

                try
                {
                    var raw      = File.ReadAllText(path);
                    var envelope = JsonSerializers.Shared.FromJson<SaveEnvelope>(raw);

                    var encrypted = Convert.FromBase64String(envelope.d);

                    if (!VerifyIntegrity(encrypted, envelope))
                        throw new InvalidDataException("完整性校验失败 — 文件可能被篡改或损坏");

                    var json   = AesHelper.Decrypt(encrypted);
                    var result = JsonSerializers.Shared.FromJson<T>(json);

                    // 代码当前版本取自全新实例的字段初始值（不会被上面的反序列化覆盖），
                    // 与磁盘封包版本 envelope.v 比较决定是否迁移；迁移后 result.dataVersion 归位到当前版本。
                    int currentVersion = new T().dataVersion;
                    result.RunMigrationFrom(envelope.v, currentVersion);

                    GameLog.Log($"[SaveManager] 读档成功 user={userId} type={typeof(T).Name} slot={slot} v{envelope.v}→{result.dataVersion}");
                    return result;
                }
                catch (CryptographicException e)
                {
                    GameLog.Warning($"[SaveManager] 解密失败 ({path}): {e.Message}");
                }
                catch (Exception e)
                {
                    GameLog.Warning($"[SaveManager] 读档失败 ({path}): {e.Message}，尝试备份...");
                }
            }

            GameLog.Log($"[SaveManager] 无有效存档 user={userId} type={typeof(T).Name} slot={slot}，使用默认值");
            return new T();
        }

        // 按封包标识的完整性方案校验密文：
        //   · m == hmac256：HMAC-SHA256 常数时间校验（防篡改）；
        //   · m 为空：旧档兼容，退回裸 SHA-256（仅防意外损坏，下次写档自动升级为 HMAC）；
        //   · 其它取值：未知方案，视为校验失败。
        private static bool VerifyIntegrity(byte[] encrypted, SaveEnvelope envelope)
        {
            if (envelope.m == MacSchemeHmac)
                return AesHelper.VerifyHmac(encrypted, envelope.h);

            if (string.IsNullOrEmpty(envelope.m))
                return AesHelper.Sha256Hex(encrypted) == envelope.h;

            GameLog.Warning($"[SaveManager] 未知完整性方案 m={envelope.m}，拒绝加载");
            return false;
        }

        // ── 工具方法 ─────────────────────────────────────────────────────────

        /// <summary>当前账号是否存在指定类型 + 槽位的存档</summary>
        public bool HasSave<T>(int slot = 0) where T : SaveData
            => File.Exists(SlotPath<T>(slot));

        /// <summary>删除当前账号指定类型 + 槽位的存档（包括备份）</summary>
        public void DeleteSave<T>(int slot = 0) where T : SaveData
        {
            TryDeleteFile(SlotPath<T>(slot));
            TryDeleteFile(BackupPath<T>(slot));
            GameLog.Log($"[SaveManager] 已删除存档 user={_currentUserId} type={typeof(T).Name} slot={slot}");
        }

        /// <summary>删除当前账号的全部存档（保留其他账号数据）</summary>
        public void DeleteCurrentUserSaves()
        {
            if (Directory.Exists(UserDir))
                Directory.Delete(UserDir, recursive: true);
            GameLog.Log($"[SaveManager] 已删除账号 {_currentUserId} 的全部存档");
        }

        /// <summary>删除本设备所有账号的全部存档（慎用）</summary>
        public void DeleteAllSaves()
        {
            var root = Path.Combine(Application.persistentDataPath, "saves");
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            GameLog.Log("[SaveManager] 已删除全设备所有存档");
        }

        private static void TryDeleteFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        // userId 净化：只保留字母、数字、下划线，防止路径注入（如 "../"）
        private static string SanitizeUserId(string raw)
            => string.IsNullOrEmpty(raw) ? "" : Regex.Replace(raw, @"[^\w]", "_");

        // ── PlayerPrefs 封装（全局，不区分账号）─────────────────────────────
        // Key 字符串请统一定义在 PlayerSettings 中，避免魔法字符串散落各处

        public void  SetPref(string key, int    value) { PlayerPrefs.SetInt(key, value);         PlayerPrefs.Save(); }
        public void  SetPref(string key, float  value) { PlayerPrefs.SetFloat(key, value);       PlayerPrefs.Save(); }
        public void  SetPref(string key, string value) { PlayerPrefs.SetString(key, value);      PlayerPrefs.Save(); }
        public void  SetPref(string key, bool   value) { PlayerPrefs.SetInt(key, value ? 1 : 0); PlayerPrefs.Save(); }

        public int    GetPref(string key, int    defaultValue = 0)     => PlayerPrefs.GetInt(key, defaultValue);
        public float  GetPref(string key, float  defaultValue = 0f)    => PlayerPrefs.GetFloat(key, defaultValue);
        public string GetPref(string key, string defaultValue = "")    => PlayerPrefs.GetString(key, defaultValue);
        public bool   GetPref(string key, bool   defaultValue = false) => PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) == 1;

        public bool HasPref(string key)    => PlayerPrefs.HasKey(key);
        public void DeletePref(string key) { PlayerPrefs.DeleteKey(key); PlayerPrefs.Save(); }
        public void DeleteAllPrefs()       { PlayerPrefs.DeleteAll();    PlayerPrefs.Save(); }
    }
}
