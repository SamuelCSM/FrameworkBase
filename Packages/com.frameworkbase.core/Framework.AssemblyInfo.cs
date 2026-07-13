using System.Runtime.CompilerServices;

// 让 EditMode 测试程序集能直接单测安全关键的 internal 类型（AesHelper 加密/HMAC 核心）。
// 仅对本仓库测试程序集开放，不影响对外 API 表面。
[assembly: InternalsVisibleTo("Framework.Tests.EditMode")]

// 让同包编辑器工具复用 internal 类型（如 DevAuthTools 清会话直调 AuthSessionStore，
// 避免在编辑器侧硬编码存储键名造成双源漂移）。仍不对业务程序集开放。
[assembly: InternalsVisibleTo("Framework.Editor")]
