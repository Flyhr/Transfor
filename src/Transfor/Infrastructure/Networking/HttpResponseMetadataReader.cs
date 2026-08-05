namespace Transfor;

// 响应头元数据读取：Content-Type 与声明长度
internal static class HttpResponseMetadataReader
{
    public static (string? ContentType, long? ContentLength) Read(HttpResponseMessage response)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var contentLength = response.Content.Headers.ContentLength;
        return (contentType, contentLength);
    }
}
