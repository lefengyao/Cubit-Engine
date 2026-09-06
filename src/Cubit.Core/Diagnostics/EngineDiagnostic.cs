namespace Cubit.Core.Diagnostics;

/// <summary>不可变的引擎诊断记录。</summary>
public sealed record EngineDiagnostic
{
    public EngineDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        string? sourcePath = null,
        string? nodePath = null)
    {
        Code = NormalizeCode(code);
        Severity = severity;
        Message = string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("诊断消息不能为空", nameof(message))
            : message.Trim();
        SourcePath = NormalizeOptionalPath(sourcePath);
        NodePath = NormalizeOptionalPath(nodePath);
    }

    public string Code { get; }

    public DiagnosticSeverity Severity { get; }

    public string Message { get; }

    public string? SourcePath { get; }

    public string? NodePath { get; }

    private static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("诊断代码不能为空", nameof(code));
        }

        foreach (var character in code)
        {
            var isLowercaseLetter = character is >= 'a' and <= 'z';
            var isDigit = character is >= '0' and <= '9';
            if (!isLowercaseLetter && !isDigit && character is not '.' and not '-')
            {
                throw new ArgumentException("诊断代码只能包含小写 ASCII 字母、数字、点和连字符", nameof(code));
            }
        }

        return code;
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}
