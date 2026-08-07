using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Framework.Save;
using Framework.Serialization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 存档管理器端到端测试（SaveManager）：加密往返、落盘密文不含明文、防篡改回退、
    /// 备份兜底、账号隔离。异步 IO 用 [UnityTest] + UniTask.ToCoroutine 驱动。
    /// 用固定主密钥摆脱设备依赖；每个用例独立账号，跑完清理磁盘。
    /// </summary>
    public class SaveManagerTests
    {
        [Serializable]
        private class ProfileSave : SaveData
        {
            public string nickname = "";
            public int coins;
        }

        /// <summary>当前代码版本=2 的存档：旧档（v1）读入时应触发 OnMigrate(1)。</summary>
        [Serializable]
        private class MigratableSave : SaveData
        {
            public int payload;
            public int migratedFrom = -1;
            public MigratableSave() { dataVersion = 2; }
            protected override void OnMigrate(int fromVersion) { migratedFrom = fromVersion; }
        }

        private sealed class FixedKeyProvider : ISaveKeyProvider
        {
            public string GetMasterSecret() => "save-manager-test-secret";
        }

        /// <summary>与 SaveManager 磁盘信封同字段的测试 DTO，用于按字段篡改后回写。</summary>
        [Serializable]
        private class RawEnvelope
        {
            public int    v;
            public string h;
            public string d;
            public string m;
        }

        private string _user;

        [SetUp]
        public void SetUp()
        {
            SaveManager.Instance.SetSaveKeyProvider(new FixedKeyProvider());
            _user = "test_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            SaveManager.Instance.SetCurrentUser(_user);
        }

        [TearDown]
        public void TearDown()
        {
            try { SaveManager.Instance.DeleteCurrentUserSaves(); } catch { }
            SaveManager.Instance.ClearCurrentUser();
            SaveManager.Instance.SetSaveKeyProvider(new DeviceSaveKeyProvider());
        }

        /// <summary>
        /// 当前账号下某存档的主档路径。刻意走 SaveManager 自己的入口而不在测试里重拼命名规则——
        /// 重拼会与实现漂移，改命名时用例要么假通过、要么在无关处报错。
        /// </summary>
        private static string SavePath<T>(int slot = 0) where T : SaveData
            => SaveManager.Instance.TestSlotPath<T>(slot);

        [UnityTest]
        public IEnumerator 存读往返_还原字段() => UniTask.ToCoroutine(async () =>
        {
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "勇者", coins = 999 });
            var loaded = await SaveManager.Instance.LoadAsync<ProfileSave>();

            Assert.AreEqual("勇者", loaded.nickname);
            Assert.AreEqual(999, loaded.coins);
        });

        [UnityTest]
        public IEnumerator 无存档_返回默认实例不抛异常() => UniTask.ToCoroutine(async () =>
        {
            var loaded = await SaveManager.Instance.LoadAsync<ProfileSave>();
            Assert.IsNotNull(loaded);
            Assert.AreEqual("", loaded.nickname);
            Assert.AreEqual(0, loaded.coins);
        });

        [UnityTest]
        public IEnumerator 落盘为密文_不含明文字段() => UniTask.ToCoroutine(async () =>
        {
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "PlainSecretName", coins = 12345 });

            string raw = File.ReadAllText(SavePath<ProfileSave>());
            StringAssert.DoesNotContain("PlainSecretName", raw, "昵称明文不得出现在存档文件中");
            StringAssert.DoesNotContain("12345", raw, "金币明文不得出现在存档文件中");
            StringAssert.Contains("\"m\":\"hmac256c\"", raw, "封包应标记上下文绑定的 HMAC 完整性方案");
        });

        [UnityTest]
        public IEnumerator 降级到裸SHA256被拒_旧洞已堵() => UniTask.ToCoroutine(async () =>
        {
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "orig", coins = 50 });

            // 模拟降级攻击：篡改密文，把方案抹成空（据此走无密钥的裸 SHA-256），再自行重算一个
            // "合法"完整性码。只认 hmac256c 这一种方案，任何其它标识都必须被拒绝。
            string path = SavePath<ProfileSave>();
            var envelope = JsonSerializers.Shared.FromJson<RawEnvelope>(File.ReadAllText(path));
            byte[] encrypted = Convert.FromBase64String(envelope.d);
            encrypted[encrypted.Length - 1] ^= 0xFF;   // 翻转一字节，模拟数据被改
            envelope.d = Convert.ToBase64String(encrypted);
            envelope.m = "";                           // 降级到"空方案"
            envelope.h = AesHelper.Sha256Hex(encrypted); // 无密钥摘要，模拟攻击者自行重算
            File.WriteAllText(path, JsonSerializers.Shared.ToJson(envelope, false));
            File.Delete(path + ".bak");

            var loaded = await SaveManager.Instance.LoadAsync<ProfileSave>();
            Assert.AreEqual("", loaded.nickname, "降级到裸 SHA-256 的伪造档必须被拒绝，回退默认");
            Assert.AreEqual(0, loaded.coins);
        });

        [UnityTest]
        public IEnumerator 篡改版本号被拒_元数据已纳入认证() => UniTask.ToCoroutine(async () =>
        {
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "orig", coins = 50 });

            // 只改信封里的版本号 v（不动密文与 h）。旧方案 MAC 只覆盖密文，改 v 不影响校验，
            // 会以被篡改的版本触发错误迁移；现在 v 已纳入 MAC，改 v 必然使校验失败。
            string path = SavePath<ProfileSave>();
            string raw = File.ReadAllText(path);
            string tampered = Regex.Replace(raw, "\"v\":\\s*-?\\d+", "\"v\":999");
            Assert.AreNotEqual(raw, tampered, "测试前提：信封应含可篡改的 v 字段");
            File.WriteAllText(path, tampered);
            File.Delete(path + ".bak");

            var loaded = await SaveManager.Instance.LoadAsync<ProfileSave>();
            Assert.AreEqual("", loaded.nickname, "篡改版本号的档必须被拒绝，回退默认");
            Assert.AreEqual(0, loaded.coins);
        });

        [UnityTest]
        public IEnumerator 篡改完整性码_拒绝加载回退默认() => UniTask.ToCoroutine(async () =>
        {
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "orig", coins = 50 });

            // 篡改：把完整性码 h 改成全 0（封包仍是合法 JSON，但 HMAC 校验必失败），并删除备份断掉兜底
            string path = SavePath<ProfileSave>();
            string raw = File.ReadAllText(path);
            string tampered = Regex.Replace(raw, "\"h\":\"[0-9a-f]+\"", "\"h\":\"" + new string('0', 64) + "\"");
            File.WriteAllText(path, tampered);
            File.Delete(path + ".bak");

            var loaded = await SaveManager.Instance.LoadAsync<ProfileSave>();
            Assert.AreEqual("", loaded.nickname, "篡改档必须被拒绝，回退默认值");
            Assert.AreEqual(0, loaded.coins);
        });

        [UnityTest]
        public IEnumerator 主档损坏_回退备份() => UniTask.ToCoroutine(async () =>
        {
            // 第一次写 → 主档=v1；第二次写 → 备份=v1、主档=v2
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "v1", coins = 1 });
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "v2", coins = 2 });

            // 损坏主档（写入乱码），保留备份
            string path = SavePath<ProfileSave>();
            File.WriteAllText(path, "corrupted-not-json");

            var loaded = await SaveManager.Instance.LoadAsync<ProfileSave>();
            Assert.AreEqual("v1", loaded.nickname, "主档损坏应回退到备份档");
            Assert.AreEqual(1, loaded.coins);
        });

        [UnityTest]
        public IEnumerator 旧档读入_触发版本迁移() => UniTask.ToCoroutine(async () =>
        {
            // 造一个 v1 旧档：把 dataVersion 显式降到 1 再写盘（模拟老版本存的档）
            var old = new MigratableSave { payload = 7 };
            old.dataVersion = 1;
            await SaveManager.Instance.SaveAsync(old);

            // 以当前代码（dataVersion=2）读入：应回调 OnMigrate(1) 并把版本归位到 2
            var loaded = await SaveManager.Instance.LoadAsync<MigratableSave>();

            Assert.AreEqual(7, loaded.payload, "业务字段应原样还原");
            Assert.AreEqual(1, loaded.migratedFrom, "OnMigrate 应以磁盘旧版本号触发");
            Assert.AreEqual(2, loaded.dataVersion, "迁移后版本号应归位到代码当前版本");
        });

        [UnityTest]
        public IEnumerator 同版本读入_不触发迁移() => UniTask.ToCoroutine(async () =>
        {
            // dataVersion 保持当前=2 写盘，读入时不应触发 OnMigrate
            await SaveManager.Instance.SaveAsync(new MigratableSave { payload = 3 });

            var loaded = await SaveManager.Instance.LoadAsync<MigratableSave>();

            Assert.AreEqual(3, loaded.payload);
            Assert.AreEqual(-1, loaded.migratedFrom, "版本一致不应触发迁移");
            Assert.AreEqual(2, loaded.dataVersion);
        });

        [UnityTest]
        public IEnumerator 账号隔离_各账号互不可见() => UniTask.ToCoroutine(async () =>
        {
            // 当前账号（_user）写入
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "alpha", coins = 111 });

            string other = "test_other_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            SaveManager.Instance.SetCurrentUser(other);
            try
            {
                var otherLoaded = await SaveManager.Instance.LoadAsync<ProfileSave>();
                Assert.AreEqual("", otherLoaded.nickname, "切换账号后不应看到别的账号存档");

                await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "beta", coins = 222 });
            }
            finally
            {
                SaveManager.Instance.DeleteCurrentUserSaves();
                SaveManager.Instance.SetCurrentUser(_user);
            }

            var back = await SaveManager.Instance.LoadAsync<ProfileSave>();
            Assert.AreEqual("alpha", back.nickname, "切回原账号应读到原账号数据");
            Assert.AreEqual(111, back.coins);
        });

        // ── 文件身份（账号目录与存档类型的唯一性）────────────────────────────

        [UnityTest]
        public IEnumerator 净化后同名但原值不同的账号_存档互不覆盖() => UniTask.ToCoroutine(async () =>
        {
            // "dash-user" 与 "dash_user" 净化后都是 dash_user：只做字符净化会让两个账号共用一个目录。
            try
            {
                SaveManager.Instance.SetCurrentUser("dash-user");
                await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "dash", coins = 1 });

                SaveManager.Instance.SetCurrentUser("dash_user");
                var other = await SaveManager.Instance.LoadAsync<ProfileSave>();
                Assert.AreEqual("", other.nickname, "另一个账号不得读到前者的存档");

                await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "underscore", coins = 2 });

                SaveManager.Instance.SetCurrentUser("dash-user");
                var back = await SaveManager.Instance.LoadAsync<ProfileSave>();
                Assert.AreEqual("dash", back.nickname, "切回后应仍是自己的数据，未被同名账号覆盖");
            }
            finally
            {
                foreach (string id in new[] { "dash-user", "dash_user" })
                {
                    SaveManager.Instance.SetCurrentUser(id);
                    try { SaveManager.Instance.DeleteCurrentUserSaves(); } catch { }
                }
                SaveManager.Instance.SetCurrentUser(_user);
            }
        });

        [UnityTest]
        public IEnumerator 同名不同命名空间的存档类型_落到不同文件() => UniTask.ToCoroutine(async () =>
        {
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "inner", coins = 1 });
            await SaveManager.Instance.SaveAsync(new Duplicate.ProfileSave { marker = 42 });

            // 只用短类型名时两者共用一个文件，后写的会覆盖先写的。
            Assert.AreNotEqual(SavePath<ProfileSave>(), SavePath<Duplicate.ProfileSave>());

            var inner = await SaveManager.Instance.LoadAsync<ProfileSave>();
            var outer = await SaveManager.Instance.LoadAsync<Duplicate.ProfileSave>();
            Assert.AreEqual("inner", inner.nickname);
            Assert.AreEqual(42, outer.marker);
        });

        [Test]
        public void 超长账号ID_目录段长度有界()
        {
            try
            {
                SaveManager.Instance.SetCurrentUser(new string('x', 500));
                string segment = Path.GetFileName(Path.GetDirectoryName(SavePath<ProfileSave>()));

                // u_ + 32 位可读前缀 + _ + 8 位哈希。不设上限时超长账号 ID 会把整条路径顶爆。
                Assert.LessOrEqual(segment.Length, 2 + MaxUserIdPrefix + 1 + 8);
            }
            finally
            {
                SaveManager.Instance.SetCurrentUser(_user);
            }
        }

        /// <summary>与 SaveManager.MaxUserIdPrefixLength 对应的期望值，用于断言路径长度上界。</summary>
        private const int MaxUserIdPrefix = 32;

        // ── 归属绑定（认证头覆盖账号 / 类型 / 槽位）──────────────────────────

        [UnityTest]
        public IEnumerator 跨账号复制存档_拒绝加载() => UniTask.ToCoroutine(async () =>
        {
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "alpha", coins = 111 });
            string source = SavePath<ProfileSave>();

            string other = "test_other_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            SaveManager.Instance.SetCurrentUser(other);
            try
            {
                string target = SavePath<ProfileSave>();
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, true);

                // 同设备密钥相同，密文本身解得开；拦住它的是认证头里的账号。
                var loaded = await SaveManager.Instance.LoadAsync<ProfileSave>();
                Assert.AreEqual("", loaded.nickname, "跨账号搬运的存档必须失效，回退默认");
                Assert.AreEqual(0, loaded.coins);
            }
            finally
            {
                SaveManager.Instance.DeleteCurrentUserSaves();
                SaveManager.Instance.SetCurrentUser(_user);
            }
        });

        [UnityTest]
        public IEnumerator 跨槽位复制存档_拒绝加载() => UniTask.ToCoroutine(async () =>
        {
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "slot0", coins = 7 }, 0);
            File.Copy(
                SavePath<ProfileSave>(0),
                SavePath<ProfileSave>(1),
                overwrite: true);

            // 槽位已纳入认证头，否则可用低价值档覆盖高价值档，或回滚到旧槽内容。
            var loaded = await SaveManager.Instance.LoadAsync<ProfileSave>(1);
            Assert.AreEqual("", loaded.nickname, "跨槽位复制的存档必须失效，回退默认");
        });

        // ── 删除与在途写入 ───────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator 写档途中删除_最终不留残档() => UniTask.ToCoroutine(async () =>
        {
            // 不 await：SaveAsync 会同步执行到线程池落盘那一步才让出，此刻删除正好落在写入途中。
            UniTask writing = SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "ghost", coins = 9 });
            SaveManager.Instance.DeleteSave<ProfileSave>();
            await writing;

            // 在途写入若不理会删除，账号注销 / RTBF 之后存档会被它重新写出来。
            Assert.IsFalse(SaveManager.Instance.HasSave<ProfileSave>(), "删除之后不得留下被在途写入复活的存档");
            Assert.IsFalse(File.Exists(SavePath<ProfileSave>()), "备份与主档都不应残留");
        });

        [UnityTest]
        public IEnumerator 账号整体删除后_在途写入不复活存档() => UniTask.ToCoroutine(async () =>
        {
            UniTask writing = SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "ghost", coins = 9 });
            SaveManager.Instance.DeleteCurrentUserSaves();
            await writing;

            Assert.IsFalse(SaveManager.Instance.HasSave<ProfileSave>());
        });

        [UnityTest]
        public IEnumerator 读写完成后_档案锁字典不残留条目() => UniTask.ToCoroutine(async () =>
        {
            for (int slot = 0; slot < 5; slot++)
            {
                await SaveManager.Instance.SaveAsync(new ProfileSave { coins = slot }, slot);
                await SaveManager.Instance.LoadAsync<ProfileSave>(slot);
            }

            // slot 由业务任意传值，锁条目只增不删会随游玩时长单向增长。
            Assert.AreEqual(0, SaveManager.Instance.TrackedFileLockCount, "读写结束后档案锁条目应被回收");
        });
    }
}

namespace Framework.Tests.Duplicate
{
    /// <summary>
    /// 与 <c>SaveManagerTests.ProfileSave</c> 短类型名相同、命名空间不同的存档类型，
    /// 用于验证文件命名按类型全名区分而不是按短名。
    /// </summary>
    [System.Serializable]
    public class ProfileSave : Framework.Save.SaveData
    {
        public int marker;
    }
}
