using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProtoGen;

/// <summary>
/// 一键双端协议生成器：单一 .proto 源 → protoc 生成 Google.Protobuf 消息类（客户端+服务端各输出一份，
/// 同命名空间、字节一致）+ 依命名约定生成路由伴生 partial（GetMainId/GetSubId + IRequest/IResponse）。
/// 无需手装 protoc（借 Grpc.Tools 内置）。以仓库根为工作目录运行。
/// </summary>
internal static class Program
{
    /// <summary>消息名约定：方向_主号3位_子号3位_名称。</summary>
    private static readonly Regex MessageRegex = new(
        @"message\s+((GC2GS|GS2GC)_(\d{3})_(\d{3})_\w+)\s*\{([^}]*)\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>命名空间约定：option csharp_namespace = "X";。</summary>
    private static readonly Regex NamespaceRegex = new(
        "option\\s+csharp_namespace\\s*=\\s*\"([^\"]+)\"\\s*;",
        RegexOptions.Compiled);

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8; // 保证中文日志在 cmd/bat 双击时不乱码
            // 仓库根由工具自身位置向上找 .git 定位，不依赖工作目录（双击 bat 时 cwd 可能不对）。
            string repoRoot = FindRepoRoot() ?? Directory.GetCurrentDirectory();
            string configPath = args.Length > 0 ? args[0] : Path.Combine("Tools", "ProtoGen", "protogen.json");
            configPath = Path.GetFullPath(configPath, repoRoot);

            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"[ProtoGen] 找不到配置文件: {configPath}");
                return 1;
            }

            GenConfig config = JsonSerializer.Deserialize<GenConfig>(
                File.ReadAllText(configPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("配置解析为空");

            (string protoc, string stdImports) = ResolveProtoc();

            string protoDir = Path.GetFullPath(config.ProtoDir, repoRoot);
            string[] protoFiles = Directory.GetFiles(protoDir, "*.proto", SearchOption.AllDirectories);
            if (protoFiles.Length == 0)
            {
                Console.Error.WriteLine($"[ProtoGen] {protoDir} 下无 .proto 文件");
                return 1;
            }

            Console.WriteLine($"[ProtoGen] protoc = {protoc}");
            Console.WriteLine($"[ProtoGen] proto 源 {protoFiles.Length} 个于 {protoDir}");

            // 先统一解析所有消息（供路由生成的请求↔响应配对）。
            List<ProtoMessage> messages = ParseMessages(protoFiles);

            foreach (GenTarget target in config.Targets)
            {
                string outDir = Path.GetFullPath(target.OutDir, repoRoot);
                Directory.CreateDirectory(outDir);

                RunProtoc(protoc, protoDir, stdImports, protoFiles, outDir);
                GenerateRouting(messages, outDir, target.RoutingNamespace);

                Console.WriteLine($"[ProtoGen] ✅ 目标 [{target.Name}] → {outDir}（消息 {messages.Count} + 路由 partial）");
            }

            Console.WriteLine("[ProtoGen] 全部完成。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProtoGen] 失败: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
    }

    /// <summary>从工具所在位置向上查找含 .git 的仓库根，使双击运行时不依赖工作目录。</summary>
    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "Tools", "ProtoGen", "protogen.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// 从 NuGet 全局包目录定位 Grpc.Tools 内置的 protoc 与 well-known 导入目录。
    /// 不依赖 MSBuild 属性（那需存在 &lt;Protobuf&gt; 项才解析），只要求已 dotnet build 过本工具（已还原 grpc.tools）。
    /// </summary>
    private static (string protoc, string stdImports) ResolveProtoc()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string grpcTools = Path.Combine(packages, "grpc.tools");
        if (!Directory.Exists(grpcTools))
            throw new DirectoryNotFoundException($"未找到 grpc.tools 包目录: {grpcTools}（请先 dotnet build Tools/ProtoGen）");

        string versionDir = Directory.GetDirectories(grpcTools).OrderByDescending(d => d).First();
        string rid = GetToolRid();
        string exe = OperatingSystem.IsWindows() ? "protoc.exe" : "protoc";
        string protoc = Path.Combine(versionDir, "tools", rid, exe);
        if (!File.Exists(protoc))
            throw new FileNotFoundException($"protoc 不存在: {protoc}（rid={rid}）");

        // well-known 类型（Timestamp/Any 等）导入目录；本示例未用到，存在则一并传给 protoc。
        string include = Path.Combine(versionDir, "build", "native", "include");
        string stdImports = Directory.Exists(include) ? include : string.Empty;
        return (protoc, stdImports);
    }

    /// <summary>Grpc.Tools 内置 protoc 的平台目录名（如 windows_x64）。</summary>
    private static string GetToolRid()
    {
        string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macosx" : "linux";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };
        return $"{os}_{arch}";
    }

    /// <summary>调用 protoc 生成 C# 消息类到 outDir。</summary>
    private static void RunProtoc(string protoc, string protoDir, string stdImports, string[] protoFiles, string outDir)
    {
        var psi = new ProcessStartInfo(protoc)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"--proto_path={protoDir}");
        if (!string.IsNullOrEmpty(stdImports))
            psi.ArgumentList.Add($"--proto_path={stdImports}");
        psi.ArgumentList.Add($"--csharp_out={outDir}");
        foreach (string f in protoFiles)
            psi.ArgumentList.Add(f);

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 protoc");
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"protoc 退出码 {proc.ExitCode}: {stderr}");
    }

    /// <summary>解析全部 proto 文件中的消息定义。</summary>
    private static List<ProtoMessage> ParseMessages(string[] protoFiles)
    {
        var list = new List<ProtoMessage>();
        foreach (string file in protoFiles)
        {
            string text = File.ReadAllText(file);
            Match nsMatch = NamespaceRegex.Match(text);
            string ns = nsMatch.Success ? nsMatch.Groups[1].Value : "Game.Protocol";
            string sourceName = ToPascalCase(Path.GetFileNameWithoutExtension(file)); // 与 protoc 输出同名，路由文件按此拆分

            foreach (Match m in MessageRegex.Matches(text))
            {
                list.Add(new ProtoMessage(
                    Namespace: ns,
                    SourceName: sourceName,
                    ClassName: m.Groups[1].Value,
                    Direction: m.Groups[2].Value,
                    MainId: byte.Parse(m.Groups[3].Value),
                    SubId: byte.Parse(m.Groups[4].Value),
                    HasResultCode: Regex.IsMatch(m.Groups[5].Value, @"\bResultCode\b")));
            }
        }
        return list;
    }

    /// <summary>依命名约定生成路由伴生 partial（GetMainId/GetSubId + IRequest/IResponse）。每个源 .proto 输出一个路由文件，避免单文件膨胀与团队合并冲突。</summary>
    private static void GenerateRouting(List<ProtoMessage> messages, string outDir, string routingNs)
    {
        // 清理旧路由产物（含上一版单文件 ProtoRouting.g.cs），避免改名/删协议后残留。
        foreach (string old in Directory.GetFiles(outDir, "*.Routing.g.cs"))
            File.Delete(old);
        string legacy = Path.Combine(outDir, "ProtoRouting.g.cs");
        if (File.Exists(legacy))
            File.Delete(legacy);

        // 建索引，供 GC2GS 请求配对同主/子号的 GS2GC 响应（跨全部源文件，故用全量 messages）。
        var byKey = new Dictionary<string, ProtoMessage>();
        foreach (ProtoMessage msg in messages)
            byKey[$"{msg.Direction}_{msg.MainId:D3}_{msg.SubId:D3}"] = msg;

        // 按 源文件 + 命名空间 分组，一个源 .proto 出一个 <源名>.Routing.g.cs。
        foreach (IGrouping<(string Source, string Ns), ProtoMessage> group in
                 messages.GroupBy(m => (m.SourceName, m.Namespace)))
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated> 由 Tools/ProtoGen 生成，请勿手改。 </auto-generated>");
            sb.AppendLine("#pragma warning disable");
            sb.AppendLine($"namespace {group.Key.Ns}");
            sb.AppendLine("{");

            foreach (ProtoMessage msg in group)
            {
                string interfaces;
                // 请求（GC2GS，子号 1-99）配对同号响应 → IRequest<Resp>；含 ResultCode 的响应 → IResponse；其余 → INetMessage。
                if (msg.Direction == "GC2GS" && msg.SubId is >= 1 and <= 99
                    && byKey.TryGetValue($"GS2GC_{msg.MainId:D3}_{msg.SubId:D3}", out ProtoMessage? resp))
                    interfaces = $"{routingNs}.IRequest<{resp.ClassName}>";
                else if (msg.HasResultCode)
                    interfaces = $"{routingNs}.IResponse";
                else
                    interfaces = $"{routingNs}.INetMessage";

                sb.AppendLine($"    public sealed partial class {msg.ClassName} : {interfaces}");
                sb.AppendLine("    {");
                sb.AppendLine($"        public byte GetMainId() => {msg.MainId};");
                sb.AppendLine($"        public byte GetSubId() => {msg.SubId};");
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(outDir, $"{group.Key.Source}.Routing.g.cs"), sb.ToString(), new UTF8Encoding(false));
        }
    }

    /// <summary>把 proto 文件名转 PascalCase（与 protoc 的 C# 输出文件名一致，如 match_room → MatchRoom）。</summary>
    private static string ToPascalCase(string name)
    {
        string[] parts = name.Split(new[] { '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (string p in parts)
            sb.Append(char.ToUpperInvariant(p[0])).Append(p[1..]);
        return sb.Length == 0 ? name : sb.ToString();
    }
}

/// <summary>生成配置根。</summary>
internal sealed record GenConfig(string ProtoDir, List<GenTarget> Targets);

/// <summary>单个输出目标（客户端/服务端各一）。命名空间取自 .proto 的 csharp_namespace；此处仅配路由接口所在命名空间。</summary>
internal sealed record GenTarget(string Name, string OutDir, string RoutingNamespace);

/// <summary>解析出的一条协议消息。SourceName=来源 .proto 的 PascalCase 名（路由文件据此拆分）。</summary>
internal sealed record ProtoMessage(string Namespace, string SourceName, string ClassName, string Direction, byte MainId, byte SubId, bool HasResultCode);
