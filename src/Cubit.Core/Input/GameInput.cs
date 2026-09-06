namespace Cubit.Core.Input;

/// <summary>跨平台通用输入快照：平台后端负责填充，领域插件维护自己的输入状态。</summary>
public struct GameInput
{
    public bool Forward;
    public bool Back;
    public bool Left;
    public bool Right;
    public bool Up;
    public bool Down;
    public bool Cancel;

    public float MouseDeltaX;
    public float MouseDeltaY;
}
