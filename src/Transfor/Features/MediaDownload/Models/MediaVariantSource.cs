namespace Transfor;

// 媒体候选的来源，用于质量排序与缩略图过滤
internal enum MediaVariantSource
{
    // 页面结构化作品数据（可信度最高）
    StructuredData,
    // 内嵌状态 JSON
    InlineState,
    // JSON-LD 结构化数据
    JsonLd,
    // DOM 直接提取的地址
    Dom,
    // 浏览器网络捕获
    NetworkCapture,
    // 封面/缩略图（可信度最低，仅在无其他候选时参与）
    Thumbnail,
}
