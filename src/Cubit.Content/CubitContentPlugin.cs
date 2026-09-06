using Cubit.Core.Diagnostics;
using Cubit.Core.Plugins;

namespace Cubit.Content;

/// <summary>提供显式内容导入能力的可选官方插件。</summary>
public sealed class CubitContentPlugin : IEnginePlugin
{
    private readonly DiagnosticBag _diagnostics;

    public CubitContentPlugin(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public string Id => "cubit.content";

    public Version Version => new(1, 0, 0);

    public void Register(PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
    }

    public void Start(EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryGetService<ContentService>(out _))
        {
            _diagnostics.Add(new EngineDiagnostic(
                "content.service.missing",
                DiagnosticSeverity.Error,
                "内容插件启动时找不到 ContentService"));
        }
    }

    public void Stop()
    {
    }
}
