using Framework.HotUpdate;
using NUnit.Framework;

namespace Framework.Tests
{
    /// <summary>
    /// current.json 指针字段级准入测试：指针是渠道根唯一可变对象，被投毒时
    /// ManifestPath 不得把客户端引出 releases/ 不可变目录，平台/渠道误投必须拒绝。
    /// </summary>
    public class CurrentPointerSecurityTests
    {
        private static CurrentPointer NewValidPointer() => new CurrentPointer
        {
            SchemaVersion = 1,
            KeyId = "dev_hotupdate_manifest",
            Env = "dev",
            Platform = UpdateSecurity.GetRuntimePlatformId(),
            Channel = "default",
            AppVersion = "1.0.0",
            ReleaseId = "abc123",
            ManifestPath = "releases/1.0.0/abc123/version.json",
        };

        [Test]
        public void 合法指针_通过准入()
        {
            Assert.IsTrue(UpdateSecurity.ValidateCurrentPointer(NewValidPointer(), "default", out string reason), reason);
        }

        [Test]
        public void 平台或渠道不匹配_拒绝()
        {
            CurrentPointer wrongPlatform = NewValidPointer();
            wrongPlatform.Platform = "definitely-not-this-platform";
            Assert.IsFalse(UpdateSecurity.ValidateCurrentPointer(wrongPlatform, "default", out _));

            CurrentPointer wrongChannel = NewValidPointer();
            wrongChannel.Channel = "other-channel";
            Assert.IsFalse(UpdateSecurity.ValidateCurrentPointer(wrongChannel, "default", out _));
        }

        [Test]
        public void 清单路径逃逸_一律拒绝()
        {
            string[] hostile =
            {
                null,
                "",
                "version.json",                                  // 不在 releases/ 下
                "releases/../version.json",                      // 目录穿越
                "releases/1.0.0/abc/../../../etc/version.json",  // 深层穿越
                "/releases/1.0.0/abc/version.json",              // 绝对路径
                "releases\\1.0.0\\abc\\version.json",            // 反斜杠
                "releases/1.0.0/abc/current.json",               // 指向可变对象
                "releases//abc/version.json",                    // 空段
                "c:/releases/1.0.0/abc/version.json",            // 盘符
            };
            foreach (string path in hostile)
            {
                CurrentPointer pointer = NewValidPointer();
                pointer.ManifestPath = path;
                Assert.IsFalse(
                    UpdateSecurity.ValidateCurrentPointer(pointer, "default", out _),
                    $"恶意 ManifestPath 未被拒绝：{path}");
            }
        }

        [Test]
        public void 协议版本或标识非法_拒绝()
        {
            CurrentPointer futureSchema = NewValidPointer();
            futureSchema.SchemaVersion = 2;
            Assert.IsFalse(UpdateSecurity.ValidateCurrentPointer(futureSchema, "default", out _));

            CurrentPointer badKeyId = NewValidPointer();
            badKeyId.KeyId = "key/../id";
            Assert.IsFalse(UpdateSecurity.ValidateCurrentPointer(badKeyId, "default", out _));

            CurrentPointer emptyRelease = NewValidPointer();
            emptyRelease.ReleaseId = "";
            Assert.IsFalse(UpdateSecurity.ValidateCurrentPointer(emptyRelease, "default", out _));
        }
    }
}
