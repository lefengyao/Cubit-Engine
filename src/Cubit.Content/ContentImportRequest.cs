namespace Cubit.Content;

/// <summary>一次由调用方明确发起的内容导入请求。</summary>
public sealed record ContentImportRequest(
    string ProjectDirectory,
    string SourceRelativePath,
    string SettingsJson = "{}");
