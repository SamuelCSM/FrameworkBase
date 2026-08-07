using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Framework.Core;
using Framework.Serialization;
using Framework.Storage;

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
    public class SaveManager : Singleton<SaveManager>, ISaveService
    {
        // ── 当前账号 ─────────────────────────────────────────────────────────
        private string _currentUserId = "guest";

        // ── 按档案文件路径的串行化锁 ─────────────────────────────────────────
        // Key = 完整存档路径（已含账号/类型/slot），保证同一档案的读/写互斥，
        // 避免并发 SaveAsync 的 .tmp→备份→Move 交错损坏，以及读撞上「删主档到 Move」的空窗。
        // 不同档案各自独立锁，互不阻塞。
        // 带引用计数：条目在最后一个使用者释放后移出字典。slot 由业务任意传值，
        // 只增不删的字典会随游玩时长单向增长。
        private readonly Dictionary<string, FileLockEntry> _fileLocks = new Dictionary<string, FileLockEntry>();

        /// <summary>_fileLocks 自身的短临界区锁，只保护字典结构与引用计数，不覆盖任何文件 IO。</summary>
        private readonly object _fileLocksGate = new object();

        /// <summary>
        /// 存档删除世代号。任何删除（单档 / 单账号 / 全设备）都递增它，
        /// 使在途写入能发现"我要写的数据早于一次删除"，从而不把已删存档复活。
        /// </summary>
        private long _deleteGeneration;

        /// <summary>带引用计数的档案锁条目。计数为 0 表示无人持有也无人等待，可以安全移出字典。</summary>
        private sealed class FileLockEntry
        {
            /// <summary>该档案的串行化信号量。</summary>
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);

            /// <summary>当前持有或等待该锁的操作数，只在 <see cref="_fileLocksGate"/> 内读写。</summary>
            public int RefCount;
        }

        /// <summary>
        /// 取得档案锁条目并登记引用。必须在 <c>WaitAsync</c> <b>之前</b>调用：
        /// 先登记引用，等待中的操作才不会被并发的释放路径把条目回收掉。
        /// </summary>
        /// <param name="path">完整存档路径。</param>
        /// <returns>已登记引用的锁条目，用完须交给 <see cref="ReleaseFileLock"/>。</returns>
        private FileLockEntry AcquireFileLock(string path)
        {
            lock (_fileLocksGate)
            {
                if (!_fileLocks.TryGetValue(path, out FileLockEntry entry))
                {
                    entry = new FileLockEntry();
                    _fileLocks[path] = entry;
                }
                entry.RefCount++;
                return entry;
            }
        }

        /// <summary>
        /// 释放档案锁并回收无人使用的条目。
        /// </summary>
        /// <param name="path">完整存档路径。</param>
        /// <param name="entry">与 <paramref name="path"/> 对应的锁条目。</param>
        private void ReleaseFileLock(string path, FileLockEntry entry)
        {
            entry.Semaphore.Release();
            lock (_fileLocksGate)
            {
                entry.RefCount--;
                // 只回收字典里仍然是同一实例的条目：并发路径可能已经换过新实例。
                if (entry.RefCount == 0 &&
                    _fileLocks.TryGetValue(path, out FileLockEntry current) &&
                    ReferenceEquals(current, entry))
                {
                    _fileLocks.Remove(path);
                    entry.Semaphore.Dispose();
                }
            }
        }

        /// <summary>
        /// 当前账号 ID（只读）。未登录时为 "guest"。
        /// </summary>
        public string CurrentUserId => _currentUserId;

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// 仅供本仓库测试断言档案锁条目已被回收（读写结束后应为 0）。正式 Player 不编译该入口。
        /// </summary>
        internal int TrackedFileLockCount
        {
            get { lock (_fileLocksGate) return _fileLocks.Count; }
        }
#endif

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
        // 完整性方案：只接受 hmac256h —— HMAC-SHA256 覆盖「认证头(方案 m + 版本 v) + 密文」。
        // 历史上曾有两条更弱的读取路径，均已移除（见提交说明），因为它们各自留有认证缺口：
        //   · m 为空 → 裸 SHA-256：无密钥，攻击者可篡改密文后自行重算合法码，属认证绕过（降级攻击）；
        //   · m=hmac256（旧）：MAC 只覆盖密文，未覆盖 v/m，元数据（版本号、方案标识）可被篡改。
        // 现在 m/v 一并纳入 MAC，且非 hmac256h 一律拒绝加载（回退备份 → 默认值）。
        private const string MacSchemeHmacHeader = "hmac256h";

        [Serializable]
        private class SaveEnvelope
        {
            public int    v;   // 存档时的 dataVersion；已纳入 MAC 认证，篡改会导致校验失败
            public string h;   // 完整性码：HMAC-SHA256(认证头 + 密文) 的十六进制
            public string d;   // Base64(IV + AES ciphertext)
            public string m;   // 完整性方案标识；已纳入 MAC 认证，只接受 hmac256h
        }

        /// <summary>
        /// 注入存档主密钥来源（默认绑定本设备，存档不可跨设备）。
        /// 需在任何读写存档之前调用——例如登录拿到账号/服务端下发密钥后。
        /// 注意：更换密钥来源会使此前用旧来源加密的存档无法解密，需配合迁移策略。
        /// </summary>
        public void SetSaveKeyProvider(ISaveKeyProvider provider)
            => AesHelper.SetKeyProvider(provider);

        /// <summary>
        /// 设置本项目的域分隔 Salt（建议用包名，如 com.yourcompany.yourgame）。
        /// 需在任何读写存档之前调用——通常放在启动流程里，早于第一次读档。
        ///
        /// 为什么必须设：默认主密钥种子是 <c>deviceUniqueIdentifier</c>，同一台设备上
        /// 两个 FrameworkBase 产品拿到的种子完全相同，全靠此 Salt 把派生密钥分开。
        /// 不设则沿用框架兜底值，兄弟产品会派生出同一把存档密钥。
        /// 注意：上线后再改 Salt 会使既有存档全部无法解密，须配合迁移策略。
        /// </summary>
        /// <param name="salt">项目级唯一串，不得为空白。</param>
        /// <exception cref="ArgumentException">salt 为 null 或全空白时抛出。</exception>
        public void SetSaveSalt(string salt)
            => AesHelper.SetAppSalt(salt);

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
            // 记下发起时的删除世代：本次要写的是这一刻的数据，之后若发生删除，这份数据就已经过期。
            long generation = Volatile.Read(ref _deleteGeneration);

            var json      = JsonSerializers.Shared.ToJson(data, false);
            var encrypted = AesHelper.Encrypt(json);

            // 完整性用 HMAC-SHA256（encrypt-then-MAC）：篡改后无 MAC Key 无法重算合法完整性码。
            // MAC 覆盖「认证头(方案 m + 版本 v) + 密文」，把元数据一并绑进签名——防止篡改 v/m 或降级方案。
            var envelope = new SaveEnvelope
            {
                v = data.dataVersion,
                d = Convert.ToBase64String(encrypted),
                m = MacSchemeHmacHeader,
            };
            envelope.h = AesHelper.HmacSha256Hex(BuildMacInput(envelope.m, envelope.v, encrypted));
            var envelopeJson = JsonSerializers.Shared.ToJson(envelope, false);

            var savePath   = SlotPath<T>(slot);
            var backupPath = BackupPath<T>(slot);
            var userDir    = UserDir; // 捕获当前账号目录，避免线程池上重算依赖 _currentUserId

            // 同一档案串行化：上方 JSON 序列化/加密已在调用线程（主线程）完成，
            // 此处仅串行化文件 IO，不影响主线程序列化语义。
            FileLockEntry fileLock = AcquireFileLock(savePath);
            await fileLock.Semaphore.WaitAsync();
            try
            {
                // 等锁期间该档案被删除：写下去等于把玩家（或 RTBF 流程）刚删掉的存档复活。
                if (Volatile.Read(ref _deleteGeneration) != generation)
                {
                    GameLog.Warning($"[SaveManager] 写档期间存档已被删除，放弃写入 type={typeof(T).Name} slot={slot}");
                    return;
                }

                await UniTask.RunOnThreadPool(() =>
                {
                    FileStorages.Shared.EnsureDirectory(userDir);
                    FileStorages.Shared.AtomicWriteText(savePath, envelopeJson, backupPath);
                });

                // 写盘途中发生删除：删除是同步的、不等在途写入，因此由写入方负责把刚落盘的文件补删掉。
                // 这样"删除后不留残档"这一最终状态成立，代价是残档短暂存在过——对账号注销与 RTBF 足够。
                if (Volatile.Read(ref _deleteGeneration) != generation)
                {
                    await UniTask.RunOnThreadPool(() =>
                    {
                        FileStorages.Shared.TryDeleteFile(savePath);
                        FileStorages.Shared.TryDeleteFile(backupPath);
                    });
                    GameLog.Warning($"[SaveManager] 写档落盘后发现存档已被删除，已回收残档 type={typeof(T).Name} slot={slot}");
                    return;
                }
            }
            finally
            {
                ReleaseFileLock(savePath, fileLock);
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
            FileLockEntry fileLock = AcquireFileLock(savePath);
            await fileLock.Semaphore.WaitAsync();
            try
            {
                return await UniTask.RunOnThreadPool(() => LoadInternal<T>(slot, savePath, backupPath, userId));
            }
            finally
            {
                ReleaseFileLock(savePath, fileLock);
            }
        }

        private T LoadInternal<T>(int slot, string savePath, string backupPath, string userId)
            where T : SaveData, new()
        {
            var paths = new[] { savePath, backupPath };

            foreach (var path in paths)
            {
                if (!FileStorages.Shared.FileExists(path)) continue;

                try
                {
                    var raw      = FileStorages.Shared.ReadText(path);
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

        // 完整性校验：只接受 hmac256h —— HMAC-SHA256 覆盖「认证头(方案+版本)+密文」，常数时间比较。
        // 空方案(裸 SHA-256)与旧 hmac256(仅覆盖密文)一律拒绝：前者无密钥可伪造，后者元数据未认证。
        // 认证头用文件里读到的 m/v 重算——攻击者改动任一字段都会使 MAC 不匹配。
        private static bool VerifyIntegrity(byte[] encrypted, SaveEnvelope envelope)
        {
            if (envelope.m != MacSchemeHmacHeader)
            {
                GameLog.Warning($"[SaveManager] 不受支持的完整性方案 m={envelope.m ?? "(空)"}，拒绝加载");
                return false;
            }
            return AesHelper.VerifyHmac(BuildMacInput(envelope.m, envelope.v, encrypted), envelope.h);
        }

        // 构造 MAC 覆盖的字节：认证头(方案 + 版本，换行域分隔) 前置于密文。
        // 换行分隔避免 "1"+"23" 与 "12"+"3" 之类的拼接歧义；把 m/v 纳入认证，
        // 使元数据篡改与方案降级都会导致 MAC 不匹配。写入与校验两侧共用，保证一致。
        private static byte[] BuildMacInput(string scheme, int version, byte[] encrypted)
        {
            byte[] header = System.Text.Encoding.UTF8.GetBytes($"{scheme}\n{version}\n");
            var buffer = new byte[header.Length + encrypted.Length];
            Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
            Buffer.BlockCopy(encrypted, 0, buffer, header.Length, encrypted.Length);
            return buffer;
        }

        // ── 工具方法 ─────────────────────────────────────────────────────────

        /// <summary>当前账号是否存在指定类型 + 槽位的存档</summary>
        public bool HasSave<T>(int slot = 0) where T : SaveData
            => FileStorages.Shared.FileExists(SlotPath<T>(slot));

        // 删除一律同步、且不获取档案锁：档案锁会被 SaveAsync 持有跨越 await，
        // 主线程若在此阻塞等待它，写入的续体就永远回不到主线程，直接死锁。
        // 因此改由删除递增世代号、在途写入自行发现并回收残档（见 SaveAsync），
        // 换取删除接口保持同步语义，不改 ISaveService 契约。

        /// <summary>
        /// 删除当前账号指定类型 + 槽位的存档（包括备份）。
        /// 递增删除世代号，使发起于本次删除之前的在途写入不会把该存档重新写出来。
        /// </summary>
        public void DeleteSave<T>(int slot = 0) where T : SaveData
        {
            Interlocked.Increment(ref _deleteGeneration);
            TryDeleteFile(SlotPath<T>(slot));
            TryDeleteFile(BackupPath<T>(slot));
            GameLog.Log($"[SaveManager] 已删除存档 user={_currentUserId} type={typeof(T).Name} slot={slot}");
        }

        /// <summary>
        /// 删除当前账号的全部存档（保留其他账号数据）。同样递增删除世代号拦住在途写入。
        /// </summary>
        public void DeleteCurrentUserSaves()
        {
            Interlocked.Increment(ref _deleteGeneration);
            FileStorages.Shared.DeleteDirectory(UserDir, recursive: true);
            GameLog.Log($"[SaveManager] 已删除账号 {_currentUserId} 的全部存档");
        }

        /// <summary>
        /// 删除本设备所有账号的全部存档（慎用）。同样递增删除世代号拦住在途写入。
        /// </summary>
        public void DeleteAllSaves()
        {
            Interlocked.Increment(ref _deleteGeneration);
            var root = Path.Combine(Application.persistentDataPath, "saves");
            FileStorages.Shared.DeleteDirectory(root, recursive: true);
            GameLog.Log("[SaveManager] 已删除全设备所有存档");
        }

        private static void TryDeleteFile(string path)
        {
            FileStorages.Shared.TryDeleteFile(path);
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
