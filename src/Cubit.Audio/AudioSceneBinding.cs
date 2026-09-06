using Cubit.Core.Scene;

namespace Cubit.Audio;

/// <summary>宿主在场景装载或替换后显式调用的音频节点服务注入器。</summary>
public static class AudioSceneBinding
{
    /// <summary>递归将一个已启动的 <see cref="AudioServer"/> 附加到场景中的音频播放器。</summary>
    public static void Attach(Node root, AudioServer server)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(server);

        AttachRecursive(root, server);
    }

    private static void AttachRecursive(Node node, AudioServer server)
    {
        switch (node)
        {
            case AudioPlayer player:
                player.AttachServer(server);
                break;
            case AudioPlayer3D player3D:
                player3D.AttachServer(server);
                break;
        }

        foreach (var child in node.Children)
        {
            AttachRecursive(child, server);
        }
    }
}
