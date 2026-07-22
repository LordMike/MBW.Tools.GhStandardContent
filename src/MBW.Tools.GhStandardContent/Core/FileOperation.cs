namespace MBW.Tools.GhStandardContent.Core;

internal sealed record FileOperation(string Path, FileOperationKind Kind, byte[]? Content = null);