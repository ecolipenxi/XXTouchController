using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace XXTouchController.Services;

public sealed class WindowsCredentialService
{
    private const string TargetName = "XXTouchController/OpenAI";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public void SaveApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key không được để trống.", nameof(apiKey));

        var normalized = apiKey.Trim();
        var byteCount = Encoding.Unicode.GetByteCount(normalized);
        if (byteCount > 5120)
            throw new ArgumentException("API key quá dài.", nameof(apiKey));

        var blob = Marshal.StringToCoTaskMemUni(normalized);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = (uint)byteCount,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "OpenAI API key"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Không lưu được API key vào Windows Credential Manager.");
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public string? ReadApiKey()
    {
        if (!CredRead(TargetName, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error,
                "Không đọc được API key từ Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;
            return Marshal.PtrToStringUni(
                credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void DeleteApiKey()
    {
        if (CredDelete(TargetName, CredTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != 1168)
            throw new Win32Exception(error,
                "Không xóa được API key khỏi Windows Credential Manager.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CredRead(
        string target, uint type, uint flags, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
