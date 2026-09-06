using System.Text.Json;
using Cubit.Content;
using Cubit.Core.Diagnostics;
using Cubit.Mcp;

namespace Cubit.Mcp.Content;

/// <summary>宿主向 Content MCP adapter 显式声明的导入器能力。</summary>
public sealed record McpContentImporterInfo
{
    public McpContentImporterInfo(string id, string version, IReadOnlyList<string> extensions)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("导入器 ID 不能为空", nameof(id)) : id.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? throw new ArgumentException("导入器版本不能为空", nameof(version)) : version.Trim();
        Extensions = extensions?.Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(extension => extension, StringComparer.Ordinal)
            .ToArray()
            ?? throw new ArgumentNullException(nameof(extensions));
        if (Extensions.Count == 0)
        {
            throw new ArgumentException("导入器至少需要一个扩展名", nameof(extensions));
        }
    }

    public string Id { get; }

    public string Version { get; }

    public IReadOnlyList<string> Extensions { get; }
}

/// <summary>按项目清单装配 Content MCP adapter。</summary>
public sealed class ContentMcpProjectExtensionModule : IMcpProjectExtensionModule
{
    private readonly IReadOnlyList<McpContentImporterInfo> _importers;

    public ContentMcpProjectExtensionModule()
        : this(null)
    {
    }

    public ContentMcpProjectExtensionModule(IReadOnlyList<McpContentImporterInfo>? importers)
    {
        _importers = importers?.Select(importer => importer ?? throw new ArgumentException("内容导入器描述不能为 null", nameof(importers)))
            .OrderBy(importer => importer.Id, StringComparer.Ordinal)
            .ToArray()
            ?? [];
    }

    public string PluginId => "cubit.content";

    public string ExpectedAssemblyName => "Cubit.Mcp.Content";

    public bool AlwaysAvailable => false;

    public IMcpExtensionProvider Create(McpProjectExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ContentMcpExtension(
            context.Project.RootDirectory,
            _importers,
            () => context.TryGetRuntimeService<ContentService>(out var service) ? service : null);
    }
}

/// <summary>只桥接宿主已注入的 ContentService，不自动创建缓存或导入资源。</summary>
public sealed class ContentMcpExtension : IMcpExtensionProvider
{
    private readonly string _projectRoot;
    private readonly IReadOnlyList<McpContentImporterInfo> _importers;
    private readonly Func<ContentService?> _serviceResolver;

    public ContentMcpExtension(
        string projectRoot,
        IReadOnlyList<McpContentImporterInfo> importers,
        Func<ContentService?> serviceResolver)
    {
        _projectRoot = Path.GetFullPath(projectRoot ?? throw new ArgumentNullException(nameof(projectRoot)));
        _importers = importers ?? throw new ArgumentNullException(nameof(importers));
        _serviceResolver = serviceResolver ?? throw new ArgumentNullException(nameof(serviceResolver));
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        if (CurrentService is null)
        {
            yield break;
        }

        yield return new McpToolDescriptor(
            "content.list_importers",
            "列出宿主已注入 ContentService 的导入器",
            EmptySchema(),
            _ => JsonSerializer.Serialize(_importers.Select(importer => new { id = importer.Id, version = importer.Version, extensions = importer.Extensions })));
        yield return new McpToolDescriptor(
            "content.import",
            "导入项目内相对路径内容到 Cubit 内容缓存",
            new { type = "object", properties = new { sourcePath = Property("string"), settingsJson = Property("string") }, required = new[] { "sourcePath" } },
            ImportContent);
    }

    public IEnumerable<McpResourceDescriptor> GetResources() => [];

    public IEnumerable<McpPromptDescriptor> GetPrompts() => [];

    private ContentService? CurrentService => _serviceResolver();

    private string ImportContent(JsonElement arguments)
    {
        var result = (CurrentService ?? throw new InvalidOperationException("宿主未注入 ContentService")).Import(new ContentImportRequest(
            _projectRoot,
            Argument(arguments, "sourcePath"),
            Argument(arguments, "settingsJson", "{}")));
        return JsonSerializer.Serialize(new
        {
            succeeded = result.Succeeded,
            cacheHit = result.CacheHit,
            cacheKey = result.CacheKey,
            artifactPath = result.ArtifactPath,
            diagnostics = result.Diagnostics.Select(Diagnostic),
        });
    }

    private static object EmptySchema() => new { type = "object", properties = new { } };

    private static object Property(string type) => new { type };

    private static string Argument(JsonElement arguments, string name, string fallback = "") =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
            ? value.GetString() ?? fallback
            : fallback;

    private static object Diagnostic(EngineDiagnostic diagnostic) => new
    {
        code = diagnostic.Code,
        severity = diagnostic.Severity.ToString(),
        message = diagnostic.Message,
        sourcePath = diagnostic.SourcePath,
        nodePath = diagnostic.NodePath,
    };
}
