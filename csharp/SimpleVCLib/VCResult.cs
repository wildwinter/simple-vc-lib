namespace SimpleVCLib;

public enum VCStatus
{
    Ok,
    /// <summary>The file is exclusively locked by another user.</summary>
    Locked,
    /// <summary>The local file is behind the depot; a sync/update is required first.</summary>
    OutOfDate,
    Error,
}

/// <summary>
/// Result returned by all VCLib operations.
/// </summary>
public record VCResult(bool Success, VCStatus Status, string Message)
{
    public static VCResult Ok(string message = "") =>
        new(true, VCStatus.Ok, message);

    public static VCResult Failure(VCStatus status, string message) =>
        new(false, status, message);

    public static VCResult Error(string message) =>
        new(false, VCStatus.Error, message);
}

/// <summary>One file in a <see cref="VCLib.WriteTextFiles"/> batch.</summary>
public sealed record VCFileWrite(string FilePath, string Content);

/// <summary>The outcome of one file in a <see cref="VCLib.WriteTextFiles"/> batch.</summary>
public sealed record VCWriteOutcome(string FilePath, bool Success, VCStatus Status, string Message);

/// <summary>The outcome of a <see cref="VCLib.WriteTextFiles"/> batch.</summary>
public sealed record VCWriteBatchResult(bool Success, IReadOnlyList<VCWriteOutcome> Results);
