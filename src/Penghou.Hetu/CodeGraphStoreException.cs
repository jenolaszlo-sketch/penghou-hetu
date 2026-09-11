namespace Penghou.Hetu;

/// <summary>
/// A durable graph store operation failed for provider reasons. Hosts catch
/// this instead of provider-native exception types; the original failure is
/// preserved as <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class CodeGraphStoreException : Exception
{
    public CodeGraphStoreException(
        string message,
        string storeName,
        string? nativeErrorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StoreName = storeName ?? throw new ArgumentNullException(nameof(storeName));
        NativeErrorCode = nativeErrorCode;
    }

    public string StoreName { get; }
    public string? NativeErrorCode { get; }
}
