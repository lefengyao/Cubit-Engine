using Cubit.Core.Scene;
using Cubit.Scripting;

namespace Cubit.Editor.Scenes;

/// <summary>Editor 私有脚本帧驱动器；不注册到 ClassDB，也不写入作者场景。</summary>
internal sealed class ScriptRuntimeDriver : Node
{
    private readonly ScriptRuntime _runtime;

    public ScriptRuntimeDriver(ScriptRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Name = "_ScriptRuntimeDriver";
        ProcessPriority = int.MinValue;
    }

    protected override void PhysicsProcess(double delta) => _runtime.PhysicsProcess(delta);

    protected override void Process(double delta) => _runtime.Process(delta);
}
