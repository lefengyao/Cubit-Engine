using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Cubit.Core.Diagnostics;
using Cubit.Core.Project;
using Cubit.Core.Scene;

namespace Cubit.Scripting;

/// <summary>
/// 显式启动的 C# 脚本运行时。程序集加载、脚本实例和生命周期均绑定到宿主主线程。
/// </summary>
public sealed class ScriptRuntime : IDisposable
{
    private readonly SceneTree _tree;
    private readonly string _projectRoot;
    private readonly DiagnosticBag _diagnostics;
    private readonly Action<ScriptRuntime>? _onDisposed;
    private readonly int _ownerThreadId;
    private readonly List<ScriptBinding> _bindings = [];
    private ScriptLoadContext? _loadContext;
    private WeakReference? _loadContextReference;
    private Assembly? _loadedAssembly;
    private string? _assemblyPath;
    private bool _running;
    private bool _disposed;

    public ScriptRuntime(
        SceneTree tree,
        string projectRoot,
        DiagnosticBag diagnostics,
        Action<ScriptRuntime>? onDisposed = null)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _projectRoot = Path.GetFullPath(projectRoot ?? throw new ArgumentNullException(nameof(projectRoot)));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _onDisposed = onDisposed;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    public Assembly? LoadedAssembly
    {
        get
        {
            EnsureOwnerThread();
            return _loadedAssembly;
        }
    }

    internal WeakReference? LoadContextReference => _loadContextReference;

    public bool IsRunning
    {
        get
        {
            EnsureOwnerThread();
            return _running;
        }
    }

    public IReadOnlyList<CubitScript> ActiveScripts
    {
        get
        {
            EnsureOwnerThread();
            return _bindings.Select(binding => binding.Script).ToArray();
        }
    }

    /// <summary>从项目根目录下的安全相对路径加载程序集；不会扫描目录或自动执行代码。</summary>
    public void LoadAssembly(string relativeAssemblyPath)
    {
        EnsureOwnerThread();
        EnsureNotDisposed();
        if (_running)
        {
            throw new InvalidOperationException("脚本运行时运行期间不能替换程序集");
        }

        if (string.IsNullOrWhiteSpace(relativeAssemblyPath) ||
            !relativeAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new ScriptRuntimeException(
                "脚本程序集必须是项目内的 .dll 文件",
                relativeAssemblyPath ?? "",
                "",
                "load");
        }

        string path;
        byte[] assemblyBytes;
        try
        {
            path = ProjectIO.ResolveProjectPath(_projectRoot, relativeAssemblyPath);
            assemblyBytes = ProjectIO.ReadProjectFileBytes(_projectRoot, relativeAssemblyPath);
            path = ProjectIO.ResolveProjectPath(_projectRoot, relativeAssemblyPath);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new ScriptRuntimeException(
                "脚本程序集路径不在项目根目录内",
                relativeAssemblyPath,
                "",
                "load",
                exception);
        }

        UnloadAssembly();
        try
        {
            _loadContext = new ScriptLoadContext(path, _projectRoot);
            _loadContextReference = new WeakReference(_loadContext);
            // 主程序集从快照流加载，避免 Windows 文件映射阻止编辑器替换/删除项目 DLL。
            using var assemblyStream = new MemoryStream(assemblyBytes, writable: false);
            _loadedAssembly = _loadContext.LoadFromStream(assemblyStream);
            _assemblyPath = path;
        }
        catch (Exception exception)
        {
            UnloadAssembly();
            throw new ScriptRuntimeException(
                "加载脚本程序集失败",
                relativeAssemblyPath,
                "",
                "load",
                exception);
        }
    }

    /// <summary>校验附件、创建脚本并依次调用 EnterTree/Ready。</summary>
    public void Start(IReadOnlyList<ScriptAttachment> attachments)
    {
        EnsureOwnerThread();
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(attachments);
        if (_running)
        {
            throw new InvalidOperationException("脚本运行时已经启动");
        }

        var assembly = _loadedAssembly ?? throw new InvalidOperationException("脚本运行时尚未加载程序集");
        var snapshots = new List<ScriptAttachment>(attachments.Count);
        foreach (var attachment in attachments)
        {
            ArgumentNullException.ThrowIfNull(attachment);
            snapshots.Add(attachment.Snapshot());
        }

        ValidateAttachments(snapshots);
        var pending = new List<ScriptBinding>(snapshots.Count);
        try
        {
            foreach (var attachment in snapshots)
            {
                var node = _tree.GetNode(attachment.TargetPath)
                    ?? throw CreateFailure(attachment.TypeName, attachment.TargetPath, "bind", "找不到脚本目标节点");
                var type = assembly.GetType(attachment.TypeName, throwOnError: false, ignoreCase: false)
                    ?? throw CreateFailure(attachment.TypeName, attachment.TargetPath, "bind", "找不到脚本类型");
                if (!type.IsPublic || type.IsAbstract || !typeof(CubitScript).IsAssignableFrom(type))
                {
                    throw CreateFailure(attachment.TypeName, attachment.TargetPath, "bind", "脚本类型必须是公开的非抽象 CubitScript 派生类");
                }

                var constructor = type.GetConstructor(Type.EmptyTypes)
                    ?? throw CreateFailure(attachment.TypeName, attachment.TargetPath, "bind", "脚本类型必须提供公开无参构造函数");
                CubitScript script;
                try
                {
                    script = (CubitScript)constructor.Invoke(null);
                }
                catch (Exception exception)
                {
                    throw CreateFailure(attachment.TypeName, attachment.TargetPath, "bind", "脚本构造函数创建失败", exception);
                }
                ApplyOverrides(script, attachment);
                script.Attach(node);
                pending.Add(new ScriptBinding(attachment, script));
            }

            _bindings.AddRange(pending);
            _running = true;
            foreach (var binding in _bindings)
            {
                Invoke(binding, "enter", binding.Script.InvokeEnterTree);
            }

            foreach (var binding in _bindings)
            {
                Invoke(binding, "ready", binding.Script.InvokeReady);
            }
        }
        catch
        {
            AbortBindings();
            throw;
        }
    }

    /// <summary>在创建任何脚本前检查声明，避免重复附件或无效字段留下半初始化运行态。</summary>
    private void ValidateAttachments(IReadOnlyList<ScriptAttachment> attachments)
    {
        var declaredBindings = new HashSet<(string TargetPath, string TypeName)>();
        foreach (var attachment in attachments)
        {
            ArgumentNullException.ThrowIfNull(attachment);
            var targetPath = attachment.TargetPath.Value;
            var typeName = attachment.TypeName;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw CreateFailure(typeName, attachment.TargetPath, "bind", "脚本目标路径不能为空");
            }

            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw CreateFailure(typeName, attachment.TargetPath, "bind", "脚本类型名不能为空");
            }

            if (!declaredBindings.Add((targetPath, typeName)))
            {
                throw CreateFailure(typeName, attachment.TargetPath, "bind", "脚本附件目标路径与类型名重复");
            }
        }
    }

    public void PhysicsProcess(double delta)
    {
        EnsureOwnerThread();
        EnsureNotDisposed();
        EnsureValidDelta(delta);
        if (!_running)
        {
            return;
        }

        foreach (var binding in _bindings)
        {
            Invoke(binding, "physics", () => binding.Script.InvokePhysicsProcess(delta));
        }
    }

    public void Process(double delta)
    {
        EnsureOwnerThread();
        EnsureNotDisposed();
        EnsureValidDelta(delta);
        if (!_running)
        {
            return;
        }

        foreach (var binding in _bindings)
        {
            Invoke(binding, "process", () => binding.Script.InvokeProcess(delta));
        }
    }

    public void Stop()
    {
        EnsureOwnerThread();
        EnsureNotDisposed();
        if (!_running && _bindings.Count == 0)
        {
            return;
        }

        Exception? firstError = null;
        for (var index = _bindings.Count - 1; index >= 0; index--)
        {
            try
            {
                Invoke(_bindings[index], "exit", _bindings[index].Script.InvokeExitTree);
            }
            catch (Exception exception)
            {
                firstError ??= exception;
            }
        }

        _running = false;
        _bindings.Clear();
        if (firstError is not null)
        {
            throw firstError;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EnsureOwnerThread();
        try
        {
            Stop();
        }
        finally
        {
            UnloadAssembly();
            _disposed = true;
            _onDisposed?.Invoke(this);
        }
    }

    private void ApplyOverrides(CubitScript script, ScriptAttachment attachment)
    {
        var type = script.GetType();
        foreach (var pair in attachment.PropertyOverrides)
        {
            var property = type.GetProperty(pair.Key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property is not null)
            {
                if (!property.CanWrite || property.GetCustomAttributes(typeof(ExportAttribute), inherit: true).Length == 0)
                {
                    throw CreateFailure(attachment.TypeName, attachment.TargetPath, "bind", $"脚本属性未导出或不可写: {pair.Key}");
                }

                try
                {
                    property.SetValue(script, ConvertOverride(pair.Value, property.PropertyType));
                }
                catch (Exception exception)
                {
                    throw CreateFailure(attachment.TypeName, attachment.TargetPath, "bind", $"脚本属性赋值失败: {pair.Key}", exception);
                }

                continue;
            }

            var field = type.GetField(pair.Key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field is null || field.IsStatic || field.IsInitOnly ||
                field.GetCustomAttributes(typeof(ExportAttribute), inherit: true).Length == 0)
            {
                throw CreateFailure(attachment.TypeName, attachment.TargetPath, "bind", $"脚本成员未导出或不可写: {pair.Key}");
            }

            try
            {
                field.SetValue(script, ConvertOverride(pair.Value, field.FieldType));
            }
            catch (Exception exception)
            {
                throw CreateFailure(attachment.TypeName, attachment.TargetPath, "bind", $"脚本字段赋值失败: {pair.Key}", exception);
            }
        }
    }

    private static object? ConvertOverride(object? value, Type targetType)
    {
        if (value is null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
            {
                throw new InvalidCastException();
            }

            return null;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (value is JsonElement element)
        {
            return element.Deserialize(targetType);
        }

        if (targetType.IsEnum && value is string text)
        {
            return Enum.Parse(targetType, text, ignoreCase: false);
        }

        return Convert.ChangeType(value, Nullable.GetUnderlyingType(targetType) ?? targetType);
    }

    private void Invoke(ScriptBinding binding, string phase, Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            _running = false;
            var scriptType = binding.Script.GetType().FullName ?? binding.Script.GetType().Name;
            var nodePath = binding.Script.TargetNode?.GetPath().Value ?? binding.Attachment.TargetPath.Value;
            var failure = new ScriptRuntimeException(
                $"脚本 {scriptType} 在 {phase} 阶段失败",
                scriptType,
                nodePath,
                phase,
                exception);
            _diagnostics.Add(new EngineDiagnostic(
                $"scripting.{phase}-failed",
                DiagnosticSeverity.Error,
                failure.Message,
                _assemblyPath,
                nodePath));
            throw failure;
        }
    }

    private ScriptRuntimeException CreateFailure(string scriptType, NodePath nodePath, string phase, string message, Exception? inner = null) =>
        new(message, scriptType, nodePath.Value, phase, inner);

    private void AbortBindings()
    {
        for (var index = _bindings.Count - 1; index >= 0; index--)
        {
            try
            {
                _bindings[index].Script.InvokeExitTree();
            }
            catch
            {
                // 预览已中止；保留第一个绑定错误作为主异常。
            }
        }

        _bindings.Clear();
        _running = false;
    }

    private void UnloadAssembly()
    {
        _loadedAssembly = null;
        _assemblyPath = null;
        _loadContext?.Unload();
        _loadContext = null;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ScriptRuntime));
        }
    }

    private static void EnsureValidDelta(double delta)
    {
        if (!double.IsFinite(delta) || delta < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "脚本生命周期 delta 必须是有限非负数");
        }
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("ScriptRuntime 只能在创建线程运行");
        }
    }

    private sealed record ScriptBinding(ScriptAttachment Attachment, CubitScript Script);

    private sealed class ScriptLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _projectRoot;

        public ScriptLoadContext(string assemblyPath, string projectRoot)
            : base($"CubitScript:{Path.GetFileNameWithoutExtension(assemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
            _projectRoot = projectRoot;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "Cubit.Core" or "Cubit.Scripting")
            {
                return Assembly.Load(assemblyName);
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null)
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(_projectRoot, fullPath);
            if (relative == "." || relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                throw new InvalidDataException("脚本依赖必须位于项目根目录内");
            }

            return LoadFromAssemblyPath(fullPath);
        }
    }
}
