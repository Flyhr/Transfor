namespace Transfor;

// 解析结果状态："需要浏览器"属于正常业务状态，不通过异常表达
internal enum MediaResolveStatus
{
    Succeeded,
    RequiresUserInteraction,
    Unsupported,
    Failed,
}
