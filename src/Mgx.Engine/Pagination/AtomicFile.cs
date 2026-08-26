namespace Mgx.Engine.Pagination;

/// <summary>Shared file primitives for the state and checkpoint writers.</summary>
internal static class AtomicFile
{
    private const int MoveAttempts = 5;
    private const int MoveRetryDelayMs = 20;

    /// <summary>
    /// Replace <paramref name="destination"/> with <paramref name="source"/>, retrying briefly on
    /// a sharing violation. On Windows the replace fails while anything else holds the
    /// destination open, including an indexer or a scanner that opened the file this process
    /// just wrote, so a first failure says nothing about whether the move can succeed.
    /// </summary>
    internal static void Replace(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       && attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryDelayMs);
            }
        }
    }
}
