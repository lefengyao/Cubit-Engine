namespace Cubit.Core.Project;

/// <summary>项目在编辑器显式运行预览前配置脱离场景根的入口。</summary>
public interface IProjectPreviewBootstrap
{
    /// <summary>在场景进入 SceneTree 前注册 ECS、实体和通用表现节点。</summary>
    void ConfigurePreview(ProjectPreviewContext context);
}
