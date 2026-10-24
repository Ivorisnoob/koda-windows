using System;
using System.Runtime.InteropServices;
using System.Text;
using Koda.Core.LastFm;

namespace Koda.Interop;

/// <summary>
/// A secret kept in the Windows Credential Manager, as a generic credential for this user on
/// this machine (CRED_PERSIST_LOCAL_MACHINE): never roamed, never in Koda's data folder, so a
/// copied data folder or library backup never carries an account with it.
/// </summary>
internal sealed class CredentialStore(string target) : ISecretStore
{
    private const int GenericType = 1;
    private const int PersistLocalMachine = 2;
    private const int NotFound = 1168;

    public string? Read()
    {
        if (!CredRead(target, GenericType, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string? value)
    {
        if (value is null)
        {
            if (!CredDelete(target, GenericType, 0) && Marshal.GetLastWin32Error() != NotFound)
            {
                throw new InvalidOperationException("The saved Last.fm sign-in couldn't be removed.");
            }

            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException("Windows refused to keep the Last.fm sign-in.");
            }
        }
        finally
        {
            // The blob held the session key: it is wiped before it is freed.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
