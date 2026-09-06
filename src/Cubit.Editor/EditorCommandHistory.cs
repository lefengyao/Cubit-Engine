namespace Cubit.Editor;

/// <summary>编辑器场景修改命令；命令本身不依赖 ImGui，可由菜单、快捷键或 MCP 调用。</summary>
public interface IEditorCommand
{
    string Description { get; }

    void Execute();

    void Undo();
}

/// <summary>线性撤销/重做命令栈；新命令执行后丢弃旧 redo 分支。</summary>
public sealed class EditorCommandHistory
{
    private readonly List<IEditorCommand> _undo = [];
    private readonly List<IEditorCommand> _redo = [];

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public string? UndoDescription => _undo.Count == 0 ? null : _undo[^1].Description;

    public string? RedoDescription => _redo.Count == 0 ? null : _redo[^1].Description;

    /// <summary>执行命令；只有执行成功的命令才会进入历史。</summary>
    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Execute();
        _undo.Add(command);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        var command = _undo[^1];
        command.Undo();
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(command);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        var command = _redo[^1];
        command.Execute();
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(command);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
