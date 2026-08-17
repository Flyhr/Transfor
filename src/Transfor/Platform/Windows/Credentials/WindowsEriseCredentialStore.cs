using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Transfor;

// Windows Credential Manager 凭据存储（Phase 6.7）：
// 原生 advapi32 P/Invoke（不依赖 NuGet 包），仅持久化 Refresh Token；
// 目标凭据名 = "Transfor.Erise." + SHA256(规范化 Origin) 前 32 位（稳定派生，不暴露明文 Origin/Token）；
// 缺失/读取失败/删除不存在安全降级；日志与异常不含任何凭据内容。
internal sealed class WindowsEriseCredentialStore : IEriseCredentialStore
{
    private const string CredentialPrefix = "Transfor.Erise.";
    private const int CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;

    // 目标凭据名稳定派生（纯函数，可离线测试）
    public static string DeriveCredentialName(string origin)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(origin));
        return CredentialPrefix + Convert.ToHexString(hash)[..32];
    }

    public Task<bool> HasCredentialAsync(string origin) =>
        Task.FromResult(TryReadBlob(RequireNormalizedName(origin), out _));

    public Task<string?> ReadRefreshTokenAsync(string origin)
    {
        var name = RequireNormalizedName(origin);
        if (!TryReadBlob(name, out var blob))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            // 严格 UTF-8 解码：非法字节（损坏 Blob）抛异常 → 安全回退为无凭据
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return Task.FromResult<string?>(strictUtf8.GetString(blob));
        }
        catch (Exception)
        {
            // 损坏 Blob（非 UTF-8）：安全回退为无凭据
            return Task.FromResult<string?>(null);
        }
    }

    public Task SaveRefreshTokenAsync(string origin, string refreshToken)
    {
        var name = RequireNormalizedName(origin);
        var blob = Encoding.UTF8.GetBytes(refreshToken);
        if (!WriteBlob(name, blob))
        {
            // 异常不携带 Token/凭据内容
            throw new InvalidOperationException("无法保存凭据");
        }
        return Task.CompletedTask;
    }

    public Task DeleteCredentialAsync(string origin)
    {
        var name = RequireNormalizedName(origin);
        try
        {
            CredDelete(name, CredentialTypeGeneric, 0);
        }
        catch (Exception)
        {
            // 删除失败（含不存在）静默降级
        }
        return Task.CompletedTask;
    }

    // 调用方传入值必须是规范化 Origin（防御性再校验）
    private static string RequireNormalizedName(string origin)
    {
        if (!EriseServerSettings.TryNormalizeOrigin(origin, out var normalized, out _))
        {
            throw new ArgumentException("服务器地址未规范化");
        }
        return DeriveCredentialName(normalized!);
    }

    private static bool TryReadBlob(string targetName, out byte[] blob)
    {
        blob = [];
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out var pointer))
        {
            return false;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return false;
            }

            blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, (int)credential.CredentialBlobSize);
            return true;
        }
        catch (Exception)
        {
            // 读取失败安全降级
            return false;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static bool WriteBlob(string targetName, byte[] blob)
    {
        var blobPointer = Marshal.AllocHGlobal(blob.Length);
        var targetPointer = Marshal.StringToHGlobalUni(targetName);
        var userPointer = Marshal.StringToHGlobalUni("Transfor");
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new Credential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPointer,
                CredentialBlob = blobPointer,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CredentialPersistLocalMachine,
                UserName = userPointer,
            };
            return CredWrite(ref credential, 0);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(blobPointer);
            Marshal.FreeHGlobal(targetPointer);
            Marshal.FreeHGlobal(userPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, int type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite([In] ref Credential credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, int type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
