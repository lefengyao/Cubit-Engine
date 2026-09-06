namespace Cubit.Content;

/// <summary>传递给导入器的已验证输入。</summary>
public sealed class ContentImportContext
{
    internal ContentImportContext(string sourcePath, byte[] sourceData, string settingsJson, string cacheKey)
    {
        SourcePath = sourcePath;
        SourceData = sourceData.AsMemory();
        SettingsJson = settingsJson;
        CacheKey = cacheKey;
    }

    public string SourcePath { get; }

    public ReadOnlyMemory<byte> SourceData { get; }

    public string SettingsJson { get; }

    public string CacheKey { get; }
}
