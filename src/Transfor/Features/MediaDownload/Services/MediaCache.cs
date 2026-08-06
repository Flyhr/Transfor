using System.Security.Cryptography;
using System.Text;

namespace Transfor;

// 媒体本地缓存：解析阶段把页面已成功加载的图片响应写入缓存目录
// （%LOCALAPPDATA%\Transfor\MediaCache，按 URL 哈希命名）；
// 下载/预览时命中缓存则直接复制，避免再次访问可能失效的 CDN 链接；
// 缓存文件有效性由下载终化（魔数/哈希）校验，无效条目自动清理
internal sealed class MediaCache
{
    private readonly string directory;

    public MediaCache(string directory)
    {
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => directory;

    // 命中返回缓存文件路径，未命中返回 null
    public string? GetCachedPath(Uri uri)
    {
        var path = PathFor(uri);
        return File.Exists(path) ? path : null;
    }

    // 写入缓存；失败返回 null（不抛异常，缓存是尽力而为）
    public async Task<string?> SaveAsync(Uri uri, Stream content, CancellationToken cancellationToken)
    {
        var path = PathFor(uri);
        try
        {
            await using var destination = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, 32 * 1024, useAsync: true);
            await content.CopyToAsync(destination, cancellationToken);
            return path;
        }
        catch
        {
            TryDelete(path);
            return null;
        }
    }

    // 删除无效缓存条目（终化失败时调用）
    public void Invalidate(Uri uri) => TryDelete(PathFor(uri));

    private string PathFor(Uri uri)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri.ToString()));
        return Path.Combine(directory, Convert.ToHexString(hash)[..32] + ".cache");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败忽略
        }
    }
}
