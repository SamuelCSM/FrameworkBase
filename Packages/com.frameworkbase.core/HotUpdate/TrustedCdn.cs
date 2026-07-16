using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Framework.Core;
using Framework.Storage;

namespace Framework.HotUpdate
{
    /// <summary>
    /// CDN 下载/校验的失败类别。它决定回退与熔断策略：传输类可短暂隔离后重试，完整性类隔离更久，
    /// 安全/配置类与 Host 无关、继续回退也不可能恢复，必须失败关闭。
    /// </summary>
    internal enum CdnFailureKind
    {
        /// <summary>无失败。</summary>
        None,
        /// <summary>传输层失败（连接、超时、非 2xx 等）；累计到阈值短暂隔离该 Host。</summary>
        Transport,
        /// <summary>内容完整性失败（哈希/长度/身份不符）；更可能是投毒或错配，隔离时间更长。</summary>
        Integrity,
        /// <summary>安全配置错误（如本地信任根缺失）；与 Host 无关，立即失败关闭，不再回退。</summary>
        Security,
        /// <summary>目标 Host 处于熔断隔离期，本轮不参与候选。</summary>
        CircuitOpen,
        /// <summary>路由/端点配置非法（不合规拓扑）；构建门禁应已拦截，运行时兜底。</summary>
        Configuration,
    }

    /// <summary>单个端点一次下载后的校验结论；失败时携带类别与可读原因，供回退决策与日志使用。</summary>
    internal readonly struct CdnValidationResult
    {
        private CdnValidationResult(bool isValid, CdnFailureKind failureKind, string reason)
        {
            IsValid = isValid;
            FailureKind = failureKind;
            Reason = reason ?? string.Empty;
        }

        public bool IsValid { get; }
        public CdnFailureKind FailureKind { get; }
        public string Reason { get; }

        public static CdnValidationResult Valid() =>
            new CdnValidationResult(true, CdnFailureKind.None, string.Empty);

        public static CdnValidationResult Failed(CdnFailureKind kind, string reason)
        {
            if (kind == CdnFailureKind.None)
                throw new ArgumentOutOfRangeException(nameof(kind));
            return new CdnValidationResult(false, kind, reason);
        }
    }

    /// <summary>
    /// 已验签清单中的不可变内容身份。Host 不属于身份；不同 CDN 只有在四个字段完全一致时
    /// 才允许承载同一个对象。
    /// </summary>
    internal readonly struct TrustedContentIdentity
    {
        private TrustedContentIdentity(string releaseId, string relativePath, long size, string sha256)
        {
            ReleaseId = releaseId;
            RelativePath = relativePath;
            Size = size;
            Sha256 = sha256;
        }

        public string ReleaseId { get; }
        public string RelativePath { get; }
        public long Size { get; }
        public string Sha256 { get; }

        public static bool TryCreate(
            string releaseId,
            string relativePath,
            long size,
            string sha256,
            out TrustedContentIdentity identity,
            out string reason)
        {
            identity = default;
            reason = null;
            if (!Guid.TryParse(releaseId, out _))
            {
                reason = "内容身份缺少有效 ManifestId。";
                return false;
            }
            if (!TrustedCdnRouteSet.IsSafeRelativePath(relativePath))
            {
                reason = $"内容相对路径不安全：{relativePath}";
                return false;
            }
            if (size <= 0 || string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 ||
                sha256.Any(c => !Uri.IsHexDigit(c)))
            {
                reason = "内容身份缺少有效 Size 或 SHA-256。";
                return false;
            }

            identity = new TrustedContentIdentity(releaseId, relativePath, size, sha256.ToLowerInvariant());
            return true;
        }
    }

    /// <summary>
    /// 一个已通过安全准入的可信 CDN 渠道根。<see cref="OriginKey"/> 是「传输故障域」标识
    /// （scheme + 主机 + 端口），熔断状态按它聚合；不同端点必须是不同 Origin 才构成独立故障域。
    /// </summary>
    internal sealed class TrustedCdnEndpoint
    {
        public TrustedCdnEndpoint(string name, Uri baseUri)
        {
            Name = name;
            BaseUri = baseUri;
            OriginKey = $"{baseUri.Scheme.ToLowerInvariant()}://{baseUri.IdnHost.ToLowerInvariant()}:{baseUri.Port}";
        }

        public string Name { get; }
        public Uri BaseUri { get; }
        public string OriginKey { get; }

        public string BuildUrl(string relativePath) => new Uri(BaseUri, relativePath).AbsoluteUri;
    }

    /// <summary>某个端点 + 安全相对路径解析出的一次下载路由（绝对 URL）。相对路径是唯一输入，调用方不能注入新 Host。</summary>
    internal readonly struct TrustedCdnRoute
    {
        public TrustedCdnRoute(TrustedCdnEndpoint endpoint, string relativePath)
        {
            Endpoint = endpoint;
            RelativePath = relativePath;
            Url = endpoint.BuildUrl(relativePath);
        }

        public TrustedCdnEndpoint Endpoint { get; }
        public string RelativePath { get; }
        public string Url { get; }
    }

    /// <summary>按传输 Host 维护进程内熔断状态；完整性失败比普通网络失败隔离更久。</summary>
    internal sealed class CdnHealthTracker
    {
        private sealed class HostState
        {
            public int ConsecutiveTransportFailures;
            public long OpenUntilMilliseconds;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, HostState> _states =
            new Dictionary<string, HostState>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<long> _nowMilliseconds;

        public CdnHealthTracker(
            int transportFailureThreshold = 2,
            long transportCooldownMilliseconds = 30_000,
            long integrityCooldownMilliseconds = 5 * 60_000,
            Func<long> nowMilliseconds = null)
        {
            TransportFailureThreshold = Math.Max(1, transportFailureThreshold);
            TransportCooldownMilliseconds = Math.Max(1, transportCooldownMilliseconds);
            IntegrityCooldownMilliseconds = Math.Max(TransportCooldownMilliseconds, integrityCooldownMilliseconds);
            _nowMilliseconds = nowMilliseconds ?? MonotonicMilliseconds;
        }

        public int TransportFailureThreshold { get; }
        public long TransportCooldownMilliseconds { get; }
        public long IntegrityCooldownMilliseconds { get; }

        public bool IsAvailable(string origin)
        {
            lock (_sync)
            {
                return !_states.TryGetValue(origin, out HostState state) ||
                       state.OpenUntilMilliseconds <= _nowMilliseconds();
            }
        }

        public void ReportSuccess(string origin)
        {
            lock (_sync) _states.Remove(origin);
        }

        public void ReportFailure(string origin, CdnFailureKind kind)
        {
            if (kind != CdnFailureKind.Transport && kind != CdnFailureKind.Integrity)
                return;

            lock (_sync)
            {
                if (!_states.TryGetValue(origin, out HostState state))
                {
                    state = new HostState();
                    _states.Add(origin, state);
                }

                long now = _nowMilliseconds();
                if (kind == CdnFailureKind.Integrity)
                {
                    state.ConsecutiveTransportFailures = 0;
                    state.OpenUntilMilliseconds = SaturatingAdd(now, IntegrityCooldownMilliseconds);
                    return;
                }

                state.ConsecutiveTransportFailures++;
                if (state.ConsecutiveTransportFailures >= TransportFailureThreshold)
                    state.OpenUntilMilliseconds = SaturatingAdd(now, TransportCooldownMilliseconds);
            }
        }

        private static long MonotonicMilliseconds() =>
            (long)(Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    /// <summary>
    /// 从包内可信端点定义构造路由。相对路径是唯一输入，调用方不能在运行时传入新 Host。
    /// 同一解析器可供清单、签名、DLL，以及后续 Addressables InternalId 转换层复用。
    /// </summary>
    internal sealed class TrustedCdnRouteSet
    {
        private const int MaxEndpointCount = 8;
        private const int MaxRelativePathLength = 1024;
        private readonly List<TrustedCdnEndpoint> _endpoints;
        private readonly CdnHealthTracker _health;

        private TrustedCdnRouteSet(List<TrustedCdnEndpoint> endpoints, CdnHealthTracker health)
        {
            _endpoints = endpoints;
            _health = health;
        }

        public IReadOnlyList<TrustedCdnEndpoint> Endpoints => _endpoints;

        public static bool TryCreate(
            string primaryBaseUrl,
            IReadOnlyList<UpdateCdnEndpointDefinition> alternates,
            string expectedEnvironment,
            CdnHealthTracker health,
            out TrustedCdnRouteSet routes,
            out string reason)
        {
            routes = null;
            reason = null;
            if (!TryCreateEndpoint("primary", primaryBaseUrl, expectedEnvironment, out TrustedCdnEndpoint primary, out reason))
                return false;

            var endpoints = new List<TrustedCdnEndpoint> { primary };
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary.Name };
            var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary.OriginKey };
            if (alternates != null)
            {
                if (alternates.Count > MaxEndpointCount - 1)
                {
                    reason = $"可信 CDN 端点总数不得超过 {MaxEndpointCount}。";
                    return false;
                }
                foreach (UpdateCdnEndpointDefinition definition in alternates)
                {
                    if (definition == null)
                    {
                        reason = "可信 CDN 列表包含空条目。";
                        return false;
                    }
                    if (!UpdateSecurity.IsSafeManifestIdentifier(definition.Name, 64) || !names.Add(definition.Name))
                    {
                        reason = $"可信 CDN 名称为空、非法或重复：{definition.Name}";
                        return false;
                    }
                    if (!string.Equals(
                            definition.AppEnv?.Trim(),
                            expectedEnvironment?.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        reason = $"CDN {definition.Name} 环境不匹配：expected={expectedEnvironment}, actual={definition.AppEnv}";
                        return false;
                    }
                    if (!TryCreateEndpoint(
                            definition.Name,
                            definition.BaseUrl,
                            expectedEnvironment,
                            out TrustedCdnEndpoint endpoint,
                            out reason))
                        return false;
                    if (!origins.Add(endpoint.OriginKey))
                    {
                        reason = $"CDN {definition.Name} 与已有端点共享同一传输 Origin，不能提供独立故障域：{endpoint.OriginKey}";
                        return false;
                    }
                    endpoints.Add(endpoint);
                }
            }

            routes = new TrustedCdnRouteSet(endpoints, health ?? new CdnHealthTracker());
            return true;
        }

        public IReadOnlyList<TrustedCdnRoute> Resolve(string relativePath)
        {
            if (!IsSafeRelativePath(relativePath))
                throw new InvalidDataException($"CDN 相对路径不安全：{relativePath}");

            var result = new List<TrustedCdnRoute>(_endpoints.Count);
            foreach (TrustedCdnEndpoint endpoint in _endpoints)
            {
                if (_health.IsAvailable(endpoint.OriginKey))
                    result.Add(new TrustedCdnRoute(endpoint, relativePath));
            }
            return result;
        }

        public TrustedCdnRoute ResolveForEndpoint(TrustedCdnEndpoint endpoint, string relativePath)
        {
            if (endpoint == null || !_endpoints.Contains(endpoint))
                throw new InvalidOperationException("端点不属于当前包内可信 CDN 集合。");
            if (!IsSafeRelativePath(relativePath))
                throw new InvalidDataException($"CDN 相对路径不安全：{relativePath}");
            return new TrustedCdnRoute(endpoint, relativePath);
        }

        public bool TryGetPrimaryRelativePath(string absoluteUrl, out string relativePath)
        {
            relativePath = null;
            if (!TryParseTransportUri(absoluteUrl, out Uri contentUri, out _))
                return false;

            Uri root = _endpoints[0].BaseUri;
            bool sameOrigin = string.Equals(root.Scheme, contentUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(root.IdnHost, contentUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                              root.Port == contentUri.Port;
            if (!sameOrigin) return false;

            string rootPath = Uri.UnescapeDataString(root.AbsolutePath).TrimEnd('/') + "/";
            string contentPath = Uri.UnescapeDataString(contentUri.AbsolutePath);
            if (!contentPath.StartsWith(rootPath, StringComparison.Ordinal)) return false;

            string candidate = contentPath.Substring(rootPath.Length);
            if (!IsSafeRelativePath(candidate)) return false;
            relativePath = candidate;
            return true;
        }

        public void ReportSuccess(TrustedCdnEndpoint endpoint) => _health.ReportSuccess(endpoint.OriginKey);
        public void ReportFailure(TrustedCdnEndpoint endpoint, CdnFailureKind kind) =>
            _health.ReportFailure(endpoint.OriginKey, kind);

        internal static bool IsSafeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                relativePath.Length > MaxRelativePathLength ||
                !string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal) ||
                relativePath[0] == '/' || relativePath[relativePath.Length - 1] == '/' ||
                relativePath.IndexOf('\\') >= 0 || relativePath.IndexOf('?') >= 0 ||
                relativePath.IndexOf('#') >= 0 || relativePath.IndexOf('%') >= 0 ||
                Uri.TryCreate(relativePath, UriKind.Absolute, out _))
                return false;

            string[] segments = relativePath.Split('/');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment == "." || segment == "..") return false;
                if (segment.Length > 128) return false;
                foreach (char c in segment)
                {
                    bool safe = char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_';
                    if (!safe) return false;
                }
            }
            return true;
        }

        private static bool TryCreateEndpoint(
            string name,
            string baseUrl,
            string environment,
            out TrustedCdnEndpoint endpoint,
            out string reason)
        {
            endpoint = null;
            if (!UpdateSecurity.ValidateUpdateServerUrl(baseUrl, environment, out reason))
                return false;
            if (!TryParseTransportUri(baseUrl, out Uri parsed, out reason))
                return false;
            if (!string.IsNullOrWhiteSpace(environment))
            {
                string decodedPath = Uri.UnescapeDataString(parsed.AbsolutePath);
                bool containsEnvironmentSegment = decodedPath
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => string.Equals(
                        segment,
                        environment.Trim(),
                        StringComparison.OrdinalIgnoreCase));
                if (!containsEnvironmentSegment)
                {
                    reason = $"CDN {name} 渠道根未包含独立环境路径段 {environment}：{baseUrl}";
                    return false;
                }
            }
            if (parsed.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
                parsed = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
            else
                parsed = new Uri(parsed.AbsoluteUri + "/", UriKind.Absolute);
            endpoint = new TrustedCdnEndpoint(name, parsed);
            return true;
        }

        private static bool TryParseTransportUri(string value, out Uri uri, out string reason)
        {
            uri = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > 2048 ||
                !Uri.TryCreate(value, UriKind.Absolute, out Uri parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                reason = $"CDN 渠道根不是有效 HTTP(S) 绝对 URL：{value}";
                return false;
            }
            if (!string.IsNullOrEmpty(parsed.UserInfo) ||
                !string.IsNullOrEmpty(parsed.Query) ||
                !string.IsNullOrEmpty(parsed.Fragment) ||
                parsed.AbsolutePath.IndexOf('%') >= 0)
            {
                reason = $"CDN URL 禁止包含凭据、Query、Fragment 或非规范化转义路径：{value}";
                return false;
            }
            string decodedPath;
            try { decodedPath = Uri.UnescapeDataString(parsed.AbsolutePath); }
            catch
            {
                reason = $"CDN 渠道根包含无效转义：{value}";
                return false;
            }
            if (decodedPath.IndexOf('\\') >= 0 || decodedPath.Split('/').Any(segment => segment == "." || segment == ".."))
            {
                reason = $"CDN 渠道根路径不安全：{value}";
                return false;
            }
            uri = parsed;
            return true;
        }
    }

    /// <summary>一次（可能跨多个端点回退的）下载的最终结果：成败、末次失败类别、尝试端点数、成功端点名与原因。</summary>
    internal readonly struct CdnDownloadResult
    {
        public CdnDownloadResult(bool success, CdnFailureKind failureKind, int attempts, string endpointName, string reason)
        {
            Success = success;
            FailureKind = failureKind;
            Attempts = attempts;
            EndpointName = endpointName ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        public bool Success { get; }
        public CdnFailureKind FailureKind { get; }
        public int Attempts { get; }
        public string EndpointName { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// 下载完成后对落盘文件做内容校验的回调。由调用方注入签名/哈希/身份校验逻辑，
    /// 让下载器与「什么算可信」解耦；返回非 <see cref="CdnFailureKind.None"/> 即触发回退或失败关闭。
    /// </summary>
    internal delegate UniTask<CdnValidationResult> CdnDownloadedFileValidator(
        TrustedCdnRoute route,
        string downloadedPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// 受信 CDN 执行器：每个 Host 内部由传输层有限重试，Host 之间按包内顺序回退。
    /// 跨 Host 不保留部分文件；在没有 ETag/长度不可变性证明时一律全量重下，避免拼接不同对象。
    /// </summary>
    internal sealed class TrustedCdnDownloadClient
    {
        private readonly TrustedCdnRouteSet _routes;
        private readonly IFileDownloadTransport _transport;

        public TrustedCdnDownloadClient(TrustedCdnRouteSet routes, IFileDownloadTransport transport)
        {
            _routes = routes ?? throw new ArgumentNullException(nameof(routes));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        public async UniTask<CdnDownloadResult> DownloadAsync(
            string relativePath,
            string savePath,
            CdnDownloadedFileValidator validator,
            Action<float> onProgress = null,
            bool quarantineTransportFailures = true,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<TrustedCdnRoute> candidates = _routes.Resolve(relativePath);
            if (candidates.Count == 0)
                return new CdnDownloadResult(false, CdnFailureKind.CircuitOpen, 0, null, "所有可信 CDN Host 均处于隔离期。");

            int attempts = 0;
            CdnFailureKind lastKind = CdnFailureKind.Transport;
            string lastReason = "所有可信 CDN 下载失败。";
            foreach (TrustedCdnRoute route in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;

                // 安全优先的跨 Host 续传策略：未证明 ETag/Size/不可变对象一致时，绝不复用部分文件。
                FileStorages.Shared.TryDeleteFile(savePath);
                FileStorages.Shared.TryDeleteFile(savePath + ".download");

                bool downloaded = await _transport.DownloadFileAsync(
                    route.Url,
                    savePath,
                    onProgress,
                    forceRefresh: true,
                    cancellationToken: cancellationToken);
                if (!downloaded)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lastKind = CdnFailureKind.Transport;
                    lastReason = $"端点 {route.Endpoint.Name} 传输失败。";
                    if (quarantineTransportFailures)
                        _routes.ReportFailure(route.Endpoint, lastKind);
                    GameLog.Warning($"[TrustedCDN] {lastReason} path={relativePath}");
                    continue;
                }

                CdnValidationResult validation = validator == null
                    ? CdnValidationResult.Valid()
                    : await validator(route, savePath, cancellationToken);
                if (validation.IsValid)
                {
                    _routes.ReportSuccess(route.Endpoint);
                    GameLog.Log($"[TrustedCDN] 下载完成 endpoint={route.Endpoint.Name}, attempts={attempts}, path={relativePath}");
                    return new CdnDownloadResult(true, CdnFailureKind.None, attempts, route.Endpoint.Name, string.Empty);
                }

                FileStorages.Shared.TryDeleteFile(savePath);
                FileStorages.Shared.TryDeleteFile(savePath + ".download");
                lastKind = validation.FailureKind;
                lastReason = validation.Reason;
                if (lastKind != CdnFailureKind.Transport || quarantineTransportFailures)
                    _routes.ReportFailure(route.Endpoint, lastKind);
                GameLog.Warning($"[TrustedCDN] 端点校验失败 endpoint={route.Endpoint.Name}, kind={lastKind}, path={relativePath}, reason={lastReason}");

                // 本地信任根缺失等安全配置错误与 Host 无关，继续回退不能恢复，必须立即失败关闭。
                if (lastKind == CdnFailureKind.Security)
                    break;
            }

            FileStorages.Shared.TryDeleteFile(savePath);
            FileStorages.Shared.TryDeleteFile(savePath + ".download");
            return new CdnDownloadResult(false, lastKind, attempts, null, lastReason);
        }
    }
}
