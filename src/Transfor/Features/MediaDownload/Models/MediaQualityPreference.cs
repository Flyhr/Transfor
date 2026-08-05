namespace Transfor;

// 质量偏好策略
internal enum MediaQualityPreference
{
    // 尽可能选择最高质量的直接可下载版本
    Highest,
    // 平衡策略：优先至少 720p 的可访问变体，其次按像素面积、码率/内容长度
    Balanced,
}
