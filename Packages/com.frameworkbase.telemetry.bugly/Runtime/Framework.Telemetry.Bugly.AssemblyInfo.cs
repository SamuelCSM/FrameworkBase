using System.Runtime.CompilerServices;

// 让本包 EditMode 测试程序集单测 internal 的接管判定（BuglyBootstrap.ShouldTakeOver / TryRegisterBackend）。
// 判定不放进公开 API：它是装配内部决策，业务只需要"装了包就自动生效"这一层语义。
[assembly: InternalsVisibleTo("Framework.Telemetry.Bugly.Tests.EditMode")]
