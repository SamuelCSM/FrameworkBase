using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// 验收专用的进程内本地认证服务：在 127.0.0.1 上实现框架 <c>HttpAuthBackend</c> 的中性 JSON 契约，
    /// 让登录切片验收走「真实 HTTP 登录链路」而非 Mock（切片 E：账号登录 / 令牌重绑均可命中）。
    /// <para>
    /// 行为：account 模式带密码 → 发 <c>acc_{account}</c> 身份与新令牌；密码为空且携带已知令牌 →
    /// 令牌重绑（冷启动恢复 / 断线重连路径）返回原身份；guest → 固定 <c>guest_e2e</c>；其余拒绝。
    /// </para>
    /// <para>
    /// 线程模型：HttpListener 回调线程只入队，请求在 <see cref="Pump"/>（编辑器主循环）里处理——
    /// JsonUtility 与日志都留在主线程，验收器每帧调用 Pump 即可。
    /// </para>
    /// </summary>
    public sealed class LocalAuthServer : IDisposable
    {
        // 与 HttpAuthBackend 的请求/响应 DTO 字段一一对应（中性契约）。
        [Serializable]
        private sealed class LoginRequestDto
        {
            public string mode;
            public string account;
            public string password;
            public string sessionToken;
            public string deviceId;
        }

        [Serializable]
        private sealed class LoginResponseDto
        {
            public bool success;
            public string userId;
            public string sessionToken;
            public long expiresAt;
            public string errorCode;
            public string errorMessage;
        }

        /// <summary>签发令牌的有效期（毫秒）。每次成功响应（含令牌重绑）都滑动续期。</summary>
        private const long TokenTtlMs = 24L * 60 * 60 * 1000;

        private readonly HttpListener _listener;
        private readonly ConcurrentQueue<HttpListenerContext> _pending =
            new ConcurrentQueue<HttpListenerContext>();
        private readonly Dictionary<string, string> _tokenToUser = new Dictionary<string, string>();
        private int _tokenSeq;
        private volatile bool _disposed;

        /// <summary>登录端点完整地址，直接交给 HttpAuthBackend。</summary>
        public string LoginUrl { get; }

        /// <summary>已处理的登录请求数（验收器可断言确实走了 HTTP 而非 Mock）。</summary>
        public int HandledRequests { get; private set; }

        private LocalAuthServer(HttpListener listener, string loginUrl)
        {
            _listener = listener;
            LoginUrl = loginUrl;
        }

        /// <summary>在 127.0.0.1 上启动，端口冲突时向后顺延重试。</summary>
        public static LocalAuthServer Start(int basePort = 17890, int portRange = 20)
        {
            for (int port = basePort; port < basePort + portRange; port++)
            {
                var listener = new HttpListener();
                string prefix = $"http://127.0.0.1:{port}/login/";
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                }
                catch (Exception)
                {
                    // 端口被占：换下一个。
                    try { listener.Close(); } catch { }
                    continue;
                }

                var server = new LocalAuthServer(listener, prefix.TrimEnd('/'));
                server.BeginAccept();
                Debug.Log($"[LocalAuthServer] 本地认证服务已启动: {server.LoginUrl}");
                return server;
            }
            throw new InvalidOperationException($"[LocalAuthServer] {basePort}..{basePort + portRange} 均无法监听。");
        }

        private void BeginAccept()
        {
            if (_disposed) return;
            try
            {
                _listener.BeginGetContext(OnContext, null);
            }
            catch (Exception)
            {
                // 释放竞态：监听器已关闭。
            }
        }

        private void OnContext(IAsyncResult ar)
        {
            if (_disposed) return;
            try
            {
                HttpListenerContext context = _listener.EndGetContext(ar);
                _pending.Enqueue(context);
            }
            catch (Exception)
            {
                // 释放竞态：忽略。
            }
            BeginAccept();
        }

        /// <summary>主线程泵：处理排队中的登录请求。验收器每帧调用。</summary>
        public void Pump()
        {
            while (_pending.TryDequeue(out HttpListenerContext context))
            {
                try { Handle(context); }
                catch (Exception ex) { Debug.Log($"[LocalAuthServer] 请求处理异常（按拒绝返回）: {ex.Message}"); }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();

            LoginRequestDto request = null;
            try { request = JsonUtility.FromJson<LoginRequestDto>(body); }
            catch (Exception) { /* 非法 JSON 按拒绝处理 */ }

            LoginResponseDto response = BuildResponse(request);
            HandledRequests++;
            Debug.Log($"[LocalAuthServer] LOGIN_HTTP_HIT #{HandledRequests} mode={request?.mode} account={request?.account} " +
                      $"tokenRebind={(request != null && string.IsNullOrEmpty(request.password) && !string.IsNullOrEmpty(request.sessionToken))} " +
                      $"→ success={response.success} userId={response.userId}");

            byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(response));
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            context.Response.OutputStream.Write(payload, 0, payload.Length);
            context.Response.OutputStream.Close();
        }

        private LoginResponseDto BuildResponse(LoginRequestDto request)
        {
            if (request == null || string.IsNullOrEmpty(request.mode))
                return Fail("auth_invalid_credential", "malformed request");

            if (request.mode == "guest")
                return Ok("guest_e2e");

            if (request.mode == "account")
            {
                // 令牌重绑（冷启动恢复 / 断线重连）：密码为空、仅携带令牌。
                if (string.IsNullOrEmpty(request.password) && !string.IsNullOrEmpty(request.sessionToken))
                {
                    return _tokenToUser.TryGetValue(request.sessionToken, out string boundUser)
                        ? Ok(boundUser, request.sessionToken)
                        : Fail("auth_invalid_credential", "unknown session token");
                }

                if (!string.IsNullOrEmpty(request.account) && !string.IsNullOrEmpty(request.password))
                    return Ok($"acc_{request.account}");
            }

            return Fail("auth_invalid_credential", "unsupported mode or missing credential");
        }

        private LoginResponseDto Ok(string userId, string reuseToken = null)
        {
            string token = reuseToken;
            if (string.IsNullOrEmpty(token))
            {
                token = $"tok_{userId}_{++_tokenSeq}";
                _tokenToUser[token] = userId;
            }
            return new LoginResponseDto
            {
                success = true,
                userId = userId,
                sessionToken = token,
                expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + TokenTtlMs,
                errorCode = string.Empty,
                errorMessage = string.Empty,
            };
        }

        private static LoginResponseDto Fail(string code, string message)
        {
            return new LoginResponseDto
            {
                success = false,
                userId = string.Empty,
                sessionToken = string.Empty,
                errorCode = code,
                errorMessage = message,
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            Debug.Log("[LocalAuthServer] 本地认证服务已停止");
        }
    }
}
