namespace Transfor;

// 媒体解析请求：来源链接、解析模式与请求上下文
internal sealed record MediaResolveRequest(
    Uri SourceUri,
    MediaResolveMode Mode,
    MediaRequestContext RequestContext);
