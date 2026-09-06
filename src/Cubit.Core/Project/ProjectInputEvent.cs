namespace Cubit.Core.Project;

/// <summary>平台可转发给项目的原始物理控制；不表达任何玩法语义。</summary>
public enum ProjectInputControl
{
    Cancel,
    PrimaryPointer,
    SecondaryPointer,
    Digit1,
    Digit2,
    Digit3,
    Digit4,
    Digit5,
    Digit6,
    Digit7,
    Digit8,
    Digit9,
    ToggleDebugOverlay,
    ToggleView,
    OpenChat,
    ToggleInventory,
}

/// <summary>原始项目控制的按住状态，由项目自己的路由器解释。</summary>
public readonly record struct ProjectInputEvent(ProjectInputControl Control, bool Pressed);
