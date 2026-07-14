using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace QHR.Services;

public sealed record StoredCredential(string Username, string Secret);

public sealed class WindowsCredentialService
{
    private const string DefaultTargetName = "QHR.Overtime.SSO";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private readonly string _targetName;

    public WindowsCredentialService(string targetName = DefaultTargetName)
    {
        _targetName = string.IsNullOrWhiteSpace(targetName) ? DefaultTargetName : targetName;
    }

    public void Save(string username, string secret)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("保存自动登录信息时账号或密码为空");
        }

        var secretBytes = Encoding.Unicode.GetBytes(secret);
        IntPtr targetPointer = IntPtr.Zero;
        IntPtr usernamePointer = IntPtr.Zero;
        IntPtr secretPointer = IntPtr.Zero;
        try
        {
            targetPointer = Marshal.StringToCoTaskMemUni(_targetName);
            usernamePointer = Marshal.StringToCoTaskMemUni(username.Trim());
            secretPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);

            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = PersistLocalMachine,
                UserName = usernamePointer
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入 Windows 凭据管理器");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            if (secretPointer != IntPtr.Zero)
            {
                Marshal.Copy(new byte[secretBytes.Length], 0, secretPointer, secretBytes.Length);
                Marshal.FreeCoTaskMem(secretPointer);
            }
            if (usernamePointer != IntPtr.Zero) Marshal.FreeCoTaskMem(usernamePointer);
            if (targetPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPointer);
        }
    }

    public StoredCredential? Read()
    {
        if (!CredRead(_targetName, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "无法读取 Windows 凭据管理器");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            var username = Marshal.PtrToStringUni(credential.UserName) ?? string.Empty;
            var secret = credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2)) ?? string.Empty;
            return string.IsNullOrWhiteSpace(username) || secret.Length == 0
                ? null
                : new StoredCredential(username, secret);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Delete()
    {
        if (CredDelete(_targetName, GenericCredential, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "无法删除 Windows 凭据");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
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

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credentialPointer);
}
