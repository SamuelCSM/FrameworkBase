using System.Runtime.CompilerServices;

// 让 EditMode 测试程序集能直接单测 internal 的平台细节（如 UnixStatvfs 的结构体偏移与溢出换算）：
// 这类代码的 P/Invoke 只在真机成立，但算术部分不该跟着一起失去覆盖。
// 注意本文件属于 Framework.Foundation——Storage/ 等目录经 .asmref 编入该程序集，不在 Framework 里。
// 仅对本仓库测试程序集开放，不影响对外 API 表面。
[assembly: InternalsVisibleTo("Framework.Tests.EditMode")]
[assembly: InternalsVisibleTo("Framework.Tests.PlayMode")]
