namespace DumpTriage.Common.Detection;

/// <summary>
/// Size and last-write time of a dump file: what tells "the file already in
/// the queue" from "a new dump written at the same path" (website#1486) without
/// re-hashing it, and what shows a queued file changed before upload (website#1493).
/// </summary>
public readonly record struct FileIdentity(long SizeBytes, DateTimeOffset LastWriteUtc)
{
    public static FileIdentity Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("dump file not found", path);
        return new FileIdentity(info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    /// <summary>Null when the file is missing or can't be read right now.</summary>
    public static FileIdentity? TryRead(string path)
    {
        try
        {
            return Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
