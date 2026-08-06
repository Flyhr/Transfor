namespace Transfor;

// 媒体网络模式：抖音为 CN 服务，默认强制直连最稳；
// System 使用 Windows 系统代理；CustomProxy 使用设置中指定的代理地址
internal enum MediaNetworkMode
{
    Direct,
    System,
    CustomProxy,
}
