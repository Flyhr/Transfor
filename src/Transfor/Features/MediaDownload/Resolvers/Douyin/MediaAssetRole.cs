namespace Transfor;

// 媒体资产角色：区分普通媒体与实况图的静态照片/动态视频配对；
// AlbumPreview 预留（图集顶层预览视频，本期不产出）
internal enum MediaAssetRole
{
    Normal,
    LivePhotoStill,
    LivePhotoMotion,
    AlbumPreview,
}
