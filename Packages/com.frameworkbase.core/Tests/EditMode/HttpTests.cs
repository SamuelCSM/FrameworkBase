using System.Text;
using Cysharp.Threading.Tasks;
using Framework.Http;
using NUnit.Framework;

namespace Framework.Tests
{
    public class HttpTests
    {
        [Test]
        public void HttpResponse_Succeeded_AllowsLocalTransportWithoutStatusCode()
        {
            var response = new HttpResponse(0, null, Encoding.UTF8.GetBytes("ok"));

            Assert.IsTrue(response.Succeeded);
            Assert.AreEqual("ok", response.Text);
        }

        [Test]
        public void PostTextAsync_BuildsPostRequest()
        {
            var client = new FakeHttpClient();

            HttpResponse response = client.PostTextAsync(
                "https://example.test/events",
                "{\"ok\":true}",
                "application/json",
                7).GetAwaiter().GetResult();

            Assert.IsTrue(response.Succeeded);
            Assert.AreEqual(HttpMethod.Post, client.LastRequest.Method);
            Assert.AreEqual("https://example.test/events", client.LastRequest.Url);
            Assert.AreEqual("application/json", client.LastRequest.ContentType);
            Assert.AreEqual(7, client.LastRequest.TimeoutSeconds);
            Assert.AreEqual("{\"ok\":true}", Encoding.UTF8.GetString(client.LastRequest.Body));
        }

        private sealed class FakeHttpClient : IHttpClient
        {
            public HttpRequest LastRequest;

            public UniTask<HttpResponse> SendAsync(HttpRequest request)
            {
                LastRequest = request;
                return UniTask.FromResult(new HttpResponse(200, null, Encoding.UTF8.GetBytes("ok")));
            }
        }
    }
}
