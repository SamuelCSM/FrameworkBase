using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Framework.Save;
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

        private sealed class FixedKeyProvider : ISaveKeyProvider
        {
            public string GetMasterSecret() => "save-manager-test-secret";
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

        private static string SavePath(string user, string typeName, int slot = 0)
            => Path.Combine(Application.persistentDataPath, "saves", $"u_{user}", $"{typeName}_{slot}.sav");

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

            string raw = File.ReadAllText(SavePath(_user, nameof(ProfileSave)));
            StringAssert.DoesNotContain("PlainSecretName", raw, "昵称明文不得出现在存档文件中");
            StringAssert.DoesNotContain("12345", raw, "金币明文不得出现在存档文件中");
            StringAssert.Contains("hmac256", raw, "封包应标记 HMAC 完整性方案");
        });

        [UnityTest]
        public IEnumerator 篡改完整性码_拒绝加载回退默认() => UniTask.ToCoroutine(async () =>
        {
            await SaveManager.Instance.SaveAsync(new ProfileSave { nickname = "orig", coins = 50 });

            // 篡改：把完整性码 h 改成全 0（封包仍是合法 JSON，但 HMAC 校验必失败），并删除备份断掉兜底
            string path = SavePath(_user, nameof(ProfileSave));
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
            string path = SavePath(_user, nameof(ProfileSave));
            File.WriteAllText(path, "corrupted-not-json");

            var loaded = await SaveManager.Instance.LoadAsync<ProfileSave>();
            Assert.AreEqual("v1", loaded.nickname, "主档损坏应回退到备份档");
            Assert.AreEqual(1, loaded.coins);
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
    }
}
