using System.Collections;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Http;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    /// <summary>
    /// 默认 HTTP 传输的防御性上限测试：响应体字节上限与取消令牌。
    /// <para>
    /// 用 <c>file://</c> 让 UnityWebRequest 真正跑一次本地文件传输，从而验证的是传输层的实际行为，
    /// 而不是只断言请求对象上的字段被赋了值。
    /// </para>
    /// </summary>
    public class UnityHttpClientLimitTests
    {
        private string _filePath;

        [SetUp]
        public void SetUp()
        {
            _filePath = Path.Combine(Application.temporaryCachePath, "http-limit-" + Path.GetRandomFileName());
            File.WriteAllBytes(_filePath, new byte[4096]);
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }

        /// <summary>本地文件的 file:// URL。UnityWebRequest 在编辑器下按普通传输处理。</summary>
        private string FileUrl => "file://" + _filePath.Replace('\\', '/');

        [UnityTest]
        public IEnumerator 响应体超过上限_按失败返回且不交出内容() => UniTask.ToCoroutine(async () =>
        {
            var client = new UnityHttpClient();
            HttpResponse response = await client.SendAsync(
                HttpRequest.Get(FileUrl).WithMaxResponseBytes(1024));

            // 4096 字节的响应撞上 1024 上限：调用方不该拿到任何响应体。
            Assert.IsFalse(response.Succeeded, "超限必须按失败返回");
            StringAssert.Contains("limit", response.Error);
        });

        [UnityTest]
        public IEnumerator 响应体在上限内_正常返回() => UniTask.ToCoroutine(async () =>
        {
            var client = new UnityHttpClient();
            HttpResponse response = await client.SendAsync(
                HttpRequest.Get(FileUrl).WithMaxResponseBytes(8192));

            Assert.IsTrue(response.Succeeded, response.Error);
            Assert.AreEqual(4096, response.Data.Length);
        });

        [UnityTest]
        public IEnumerator 不设上限_保持原有不限行为() => UniTask.ToCoroutine(async () =>
        {
            var client = new UnityHttpClient();
            HttpResponse response = await client.SendAsync(HttpRequest.Get(FileUrl));

            Assert.IsTrue(response.Succeeded, response.Error);
            Assert.AreEqual(4096, response.Data.Length);
        });

        [UnityTest]
        public IEnumerator 令牌已取消_请求不产生结果() => UniTask.ToCoroutine(async () =>
        {
            var client = new UnityHttpClient();
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                HttpResponse response = await client.SendAsync(
                    HttpRequest.Get(FileUrl).WithCancellation(cts.Token));

                Assert.IsFalse(response.Succeeded, "已取消的请求不得返回成功结果");
                StringAssert.Contains("canceled", response.Error);
            }
        });
    }
}
