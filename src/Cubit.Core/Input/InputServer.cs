namespace Cubit.Core.Input;

/// <summary>引擎通用输入动作；平台按键和触摸语义统一映射到这里。</summary>
public enum InputAction
{
    Forward,
    Back,
    Left,
    Right,
    Up,
    Down,
    Cancel,
}

/// <summary>
/// 引擎输入服务：平台后端喂入通用动作，每帧产生一次快照。
/// 玩法专用操作由项目或插件自己的输入状态承载。
/// </summary>
public sealed class InputServer
{
    private readonly HashSet<InputAction> _held = [];
    private float _mouseDeltaX;
    private float _mouseDeltaY;
    private float _moveAxisX;
    private float _moveAxisY;
    private float _lookDeltaX;
    private float _lookDeltaY;

    /// <summary>设置按键或触摸动作的按下状态。</summary>
    public void SetAction(InputAction action, bool pressed)
    {
        if (pressed)
        {
            _held.Add(action);
        }
        else
        {
            _held.Remove(action);
        }
    }

    /// <summary>平台输入模式切换时清理残留按住动作，避免菜单/聊天打开后继续移动。</summary>
    public void ClearActions() => _held.Clear();

    /// <summary>累计鼠标视角增量；构建快照后清零。</summary>
    public void AddMouseDelta(float dx, float dy)
    {
        _mouseDeltaX += dx;
        _mouseDeltaY += dy;
    }

    /// <summary>设置通用移动轴，输入范围限制为 -1..1。</summary>
    public void SetMoveAxis(float x, float y)
    {
        _moveAxisX = Math.Clamp(x, -1f, 1f);
        _moveAxisY = Math.Clamp(y, -1f, 1f);
    }

    /// <summary>累计触摸视角增量；构建快照后与鼠标增量合并。</summary>
    public void AddLookDelta(float dx, float dy)
    {
        _lookDeltaX += dx;
        _lookDeltaY += dy;
    }

    /// <summary>
    /// 为固定模拟步生成动作快照。持续动作和移动轴可在同一渲染帧的多个物理步复用；
    /// 视角增量属于渲染帧，不能在这里读取或清零。
    /// </summary>
    public GameInput BuildSimulationSnapshot()
    {
        return new GameInput
        {
            Forward = _held.Contains(InputAction.Forward) || _moveAxisY < -0.5f,
            Back = _held.Contains(InputAction.Back) || _moveAxisY > 0.5f,
            Left = _held.Contains(InputAction.Left) || _moveAxisX < -0.5f,
            Right = _held.Contains(InputAction.Right) || _moveAxisX > 0.5f,
            Up = _held.Contains(InputAction.Up),
            Down = _held.Contains(InputAction.Down),
            Cancel = _held.Contains(InputAction.Cancel),
        };
    }

    /// <summary>产生本渲染帧输入快照，并清零累计视角增量。</summary>
    public GameInput BuildSnapshot()
    {
        var input = BuildSimulationSnapshot();
        input.MouseDeltaX = _mouseDeltaX + _lookDeltaX;
        input.MouseDeltaY = _mouseDeltaY + _lookDeltaY;

        _mouseDeltaX = 0f;
        _mouseDeltaY = 0f;
        _lookDeltaX = 0f;
        _lookDeltaY = 0f;
        return input;
    }
}
