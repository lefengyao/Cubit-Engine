namespace Cubit.Core.Diagnostics;

/// <summary>跨模块收集诊断的线程安全容器。</summary>
public sealed class DiagnosticBag
{
    private readonly object _sync = new();
    private readonly List<EngineDiagnostic> _items = [];

    public bool HasErrors
    {
        get
        {
            lock (_sync)
            {
                return _items.Any(item => item.Severity == DiagnosticSeverity.Error);
            }
        }
    }

    public void Add(EngineDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        lock (_sync)
        {
            _items.Add(diagnostic);
        }
    }

    public void AddRange(IEnumerable<EngineDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        lock (_sync)
        {
            foreach (var diagnostic in diagnostics)
            {
                ArgumentNullException.ThrowIfNull(diagnostic);
                _items.Add(diagnostic);
            }
        }
    }

    public EngineDiagnostic[] Snapshot()
    {
        lock (_sync)
        {
            return _items.ToArray();
        }
    }
}
