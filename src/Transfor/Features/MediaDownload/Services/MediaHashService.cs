using System.Security.Cryptography;

namespace Transfor;

// 媒体哈希：流式计算 SHA-256，不将大文件读入内存
internal static class MediaHashService
{
    public static async Task<string> ComputeSha256Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
