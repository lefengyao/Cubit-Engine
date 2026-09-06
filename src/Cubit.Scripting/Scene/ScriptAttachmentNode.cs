using Cubit.Core.Scene;

namespace Cubit.Scripting;

/// <summary>
/// 场景中的脚本附件声明节点。节点只保存数据；脚本生命周期由预览宿主显式驱动。
/// </summary>
public sealed class ScriptAttachmentNode : Node
{
    private ScriptAttachment? _attachment;
    private int _ownerThreadId;

    [Export]
    public ScriptAttachment? Attachment
    {
        get
        {
            EnsureMainThread();
            return _attachment;
        }
        set
        {
            EnsureMainThread();
            if (Tree is not null)
            {
                value?.BindOwnerThread(_ownerThreadId);
            }

            _attachment = value;
        }
    }

    protected override void EnterTree()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _attachment?.BindOwnerThread(_ownerThreadId);
    }

    private void EnsureMainThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0)
        {
            _ownerThreadId = current;
        }
        else if (_ownerThreadId != current)
        {
            throw new InvalidOperationException("ScriptAttachmentNode 只能在拥有它的主线程访问");
        }
    }
}
