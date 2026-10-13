using DumpTriage.Common.Models;

namespace DumpTriage.Common.Detection;

/// <summary>
/// Classifies a dump file from its first 4 bytes, mirroring the backend's
/// detect_dump_type (service/backend/app/services/dump_sniff.py) so rule-based
/// policy can match on dump type before upload without asking the server.
/// The server is still the source of truth — this is only used for local
/// policy decisions, never sent to the API.
/// </summary>
public static class DumpSniffer
{
    private static readonly byte[] MdmpSignature = "MDMP"u8.ToArray();
    private static readonly byte[] RawSignature = "PAGE"u8.ToArray();

    public static async Task<DumpType> DetectAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        if (read < header.Length)
        {
            return DumpType.Unknown;
        }

        if (header.AsSpan().SequenceEqual(MdmpSignature))
        {
            return DumpType.UserMode;
        }

        if (header.AsSpan().SequenceEqual(RawSignature))
        {
            return DumpType.KernelMode;
        }

        return DumpType.Unknown;
    }
}
