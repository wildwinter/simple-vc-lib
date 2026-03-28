namespace SimpleVCLib;

/// <summary>
/// Main entry point for the simple-vc-lib API.
/// All methods auto-detect the version control system from the file path,
/// unless an explicit provider has been set via <see cref="SetProvider"/>.
/// </summary>
public static class VCLib
{
    private static IVCProvider? _overrideProvider;

    /// <summary>
    /// Override the provider used for all operations.
    /// Useful for testing or in environments where auto-detection is unreliable.
    /// </summary>
    public static void SetProvider(IVCProvider provider) =>
        _overrideProvider = provider;

    /// <summary>
    /// Clear any provider override, restoring auto-detection.
    /// </summary>
    public static void ClearProvider() =>
        _overrideProvider = null;

    /// <summary>
    /// Return the provider that would be used for <paramref name="path"/>.
    /// </summary>
    public static IVCProvider GetProvider(string path) =>
        _overrideProvider ?? Detector.Detect(path);

    /// <inheritdoc cref="IVCProvider.PrepareToWrite"/>
    public static VCResult PrepareToWrite(string filePath) =>
        GetProvider(filePath).PrepareToWrite(filePath);

    /// <inheritdoc cref="IVCProvider.FinishedWrite"/>
    public static VCResult FinishedWrite(string filePath) =>
        GetProvider(filePath).FinishedWrite(filePath);

    /// <inheritdoc cref="IVCProvider.DeleteFile"/>
    public static VCResult DeleteFile(string filePath) =>
        GetProvider(filePath).DeleteFile(filePath);

    /// <inheritdoc cref="IVCProvider.DeleteFolder"/>
    public static VCResult DeleteFolder(string folderPath) =>
        GetProvider(folderPath).DeleteFolder(folderPath);
}
