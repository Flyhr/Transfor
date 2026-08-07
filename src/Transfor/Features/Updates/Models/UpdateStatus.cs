namespace Transfor;

// 更新检查结果状态：UpToDate 无需更新；OptionalUpdate 可选更新；
// RequiredUpdate 强制更新；CheckFailed 检查失败（应用必须仍可运行）；Disabled 远程禁用更新
internal enum UpdateStatus
{
    UpToDate,
    OptionalUpdate,
    RequiredUpdate,
    CheckFailed,
    Disabled,
}
