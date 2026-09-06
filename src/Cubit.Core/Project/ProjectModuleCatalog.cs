namespace Cubit.Core.Project;

/// <summary>项目模块依赖验证与确定性拓扑顺序；不负责程序集扫描或模块实例化。</summary>
public static class ProjectModuleCatalog
{
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "feature",
        "content",
        "presentation",
        "tool",
    };

    public static IReadOnlyList<CubitProjectModuleReference> ResolveLoadOrder(
        IReadOnlyList<CubitProjectModuleReference> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var byId = new Dictionary<string, CubitProjectModuleReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in declarations)
        {
            ArgumentNullException.ThrowIfNull(module);
            var id = NormalizeId(module.Id, "项目模块 ID 不能为空");
            if (!string.Equals(id, module.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"项目模块 ID 必须使用规范值: {module.Id}");
            }

            if (!byId.TryAdd(id, module))
            {
                throw new InvalidDataException($"项目模块 ID 重复: {id}");
            }

            if (!Version.TryParse(module.Version, out _))
            {
                throw new InvalidDataException($"项目模块 {id} 版本无效: {module.Version}");
            }

            if (!Kinds.Contains(module.Kind))
            {
                throw new InvalidDataException($"项目模块 {id} 类型无效: {module.Kind}");
            }

        }

        foreach (var module in declarations)
        {
            var id = module.Id;
            ValidateReferences(id, module.Dependencies, byId, "依赖");
            ValidateReferences(id, module.Conflicts, byId, "冲突");
            if (module.Dependencies.Any(dependency => module.Conflicts.Contains(dependency, StringComparer.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"项目模块 {id} 不能同时依赖并冲突于同一模块");
            }
        }

        foreach (var module in declarations)
        {
            foreach (var conflict in module.Conflicts)
            {
                var other = byId[conflict];
                throw new InvalidDataException($"项目模块冲突: {module.Id} 与 {other.Id}");
            }
        }

        var result = new List<CubitProjectModuleReference>(declarations.Count);
        var states = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in declarations)
        {
            Visit(module.Id);
        }

        return result;

        void Visit(string id)
        {
            if (!byId.ContainsKey(id))
            {
                // 预留的 cubit.* 依赖由官方插件清单解析，不属于项目模块拓扑。
                return;
            }

            if (states.TryGetValue(id, out var state) && state == VisitState.Done)
            {
                return;
            }

            if (states.TryGetValue(id, out state) && state == VisitState.Active)
            {
                throw new InvalidDataException($"项目模块存在循环依赖: {id}");
            }

            states[id] = VisitState.Active;
            var module = byId[id];
            foreach (var dependency in module.Dependencies)
            {
                Visit(dependency);
            }

            states[id] = VisitState.Done;
            result.Add(module);
        }
    }

    private static void ValidateReferences(
        string moduleId,
        IEnumerable<string> references,
        IReadOnlyDictionary<string, CubitProjectModuleReference> declarations,
        string relation)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            var id = NormalizeId(reference, $"项目模块 {moduleId} 的{relation} ID 不能为空");
            if (!seen.Add(id))
            {
                throw new InvalidDataException($"项目模块 {moduleId} 的{relation}重复: {id}");
            }

            if (string.Equals(moduleId, id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"项目模块 {moduleId} 不能引用自身");
            }

            if (!declarations.ContainsKey(id))
            {
                // 允许未来由官方宿主提供的保留依赖命名空间；当前不会尝试解析或加载。
                if (!id.StartsWith("cubit.", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"项目模块 {moduleId} 的{relation}不存在: {id}");
                }
            }
        }
    }

    private static string NormalizeId(string? id, string message)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidDataException(message);
        }

        return id.Trim();
    }

    private enum VisitState
    {
        Active,
        Done,
    }
}
