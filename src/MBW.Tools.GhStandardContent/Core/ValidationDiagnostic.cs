namespace MBW.Tools.GhStandardContent.Core;

internal sealed record ValidationDiagnostic(string Code, string Message, string? Location = null);