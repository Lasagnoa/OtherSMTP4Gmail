using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Generic credentials are scoped to the current Windows user and this computer.
internal static class CredentialStore {
    const string Prefix = "LocalSMTPGmail/v1/";
    const int Generic = 1, PersistLocalMachine = 2, ErrorNotFound = 1168;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CredWriteW")]
    static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CredReadW")]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CredDeleteW")]
    static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", EntryPoint="CredFree")]
    static extern void CredFree(IntPtr credential);

    static string Target(string id) {
        Guid parsed;
        if (id == null || id.Length != 32 || !Guid.TryParseExact(id,"N",out parsed)) throw new InvalidOperationException("資格情報の識別子が不正です。");
        return Prefix + parsed.ToString("N");
    }
    public static string Create(string password) {
        string id = Guid.NewGuid().ToString("N");
        byte[] bytes = Encoding.Unicode.GetBytes(password);
        if (bytes.Length == 0 || bytes.Length > 2560) { Array.Clear(bytes,0,bytes.Length); throw new InvalidOperationException("パスワードの長さが不正です。"); }
        IntPtr memory = Marshal.AllocCoTaskMem(bytes.Length);
        try {
            Marshal.Copy(bytes,0,memory,bytes.Length);
            var entry = new Credential {Type=Generic,TargetName=Target(id),CredentialBlobSize=(uint)bytes.Length,CredentialBlob=memory,Persist=PersistLocalMachine};
            if (!CredWrite(ref entry,0)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Windowsの資格情報を保存できません。");
        } finally {
            Array.Clear(bytes,0,bytes.Length);
            for (int i=0;i<bytes.Length;i++) Marshal.WriteByte(memory,i,0);
            Marshal.FreeCoTaskMem(memory);
        }
        return id;
    }
    public static bool Exists(string id) {
        IntPtr ptr;
        if (CredRead(Target(id),Generic,0,out ptr)) { CredFree(ptr); return true; }
        int error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new Win32Exception(error,"Windowsの資格情報を確認できません。");
    }
    public static string Read(string id) {
        IntPtr ptr;
        if (!CredRead(Target(id),Generic,0,out ptr)) {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) throw new InvalidOperationException("Windowsの資格情報が見つかりません。設定画面でパスワードを再入力してください。");
            throw new Win32Exception(error,"Windowsの資格情報を読み取れません。");
        }
        try {
            var entry = (Credential)Marshal.PtrToStructure(ptr,typeof(Credential));
            if (entry.CredentialBlobSize == 0 || entry.CredentialBlobSize > 2560 || (entry.CredentialBlobSize % 2) != 0) throw new InvalidOperationException("Windowsの資格情報が不正です。");
            byte[] bytes = new byte[entry.CredentialBlobSize];
            try { Marshal.Copy(entry.CredentialBlob,bytes,0,bytes.Length); return Encoding.Unicode.GetString(bytes); }
            finally { Array.Clear(bytes,0,bytes.Length); }
        } finally { CredFree(ptr); }
    }
    public static void Delete(string id) {
        if (CredDelete(Target(id),Generic,0)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound) throw new Win32Exception(error,"Windowsの資格情報を削除できません。");
    }
}
