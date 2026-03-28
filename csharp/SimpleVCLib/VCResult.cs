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
