using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Koda.Core;

namespace Koda.Interop;

/// <summary>
/// A secret kept in the Windows Credential Manager, as a generic credential for this user on
/// this machine (CRED_PERSIST_LOCAL_MACHINE): never roamed, never in Koda's data folder, so a
/// copied data folder or library backup never carries an account with it.
///
/// One credential holds at most 2.5 KB, and a YouTube Music sign-in's cookie jar can be larger.
/// A value that doesn't fit is split across numbered credentials, and the target keeps a short
/// manifest naming how many; small values (Last.fm's key) stay one credential, as before.
/// </summary>
internal sealed class CredentialStore(string target) : ISecretStore
{
    private const int GenericType = 1;
    private const int PersistLocalMachine = 2;
    private const int NotFound = 1168;

    /// <summary>What one credential may hold, with room under the API's 2560-byte blob limit.</summary>
    private const int ChunkBytes = 2000;

    private const string ChunkPrefix = "koda-chunks:1:";
    private const int MaxChunks = 64;

    /// <summary>Rotations from requests in flight together all write the same target; the
    /// manifest and its chunks must change as one.</summary>
    private readonly object _gate = new();

    public string? Read()
    {
        lock (_gate) return ReadLocked();
    }

    private string? ReadLocked()
    {
        var value = ReadRaw(target);
        if (value is null || !value.StartsWith(ChunkPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        if (!int.TryParse(value.AsSpan(ChunkPrefix.Length), out var count) || count is < 1 or > MaxChunks)
        {
            return null;
        }

        var parts = new string[count];
        for (var index = 0; index < count; index++)
        {
            if (ReadRaw(ChunkTarget(index)) is not { } part)
            {
                return null;
            }
            parts[index] = part;
        }
        return string.Concat(parts);
    }

    public void Write(string? value)
    {
        lock (_gate) WriteLocked(value);
    }

    private void WriteLocked(string? value)
    {
        if (value is null)
        {
            DeleteRaw(target);
            for (var index = 0; index < MaxChunks; index++)
            {
                if (!DeleteRaw(ChunkTarget(index))) break;
            }
            return;
        }

        var chunks = Split(value);
        if (chunks.Count > MaxChunks)
        {
            throw new InvalidOperationException("The sign-in is too large to keep.");
        }

        var chunked = chunks.Count > 1;
        if (chunked)
        {
            for (var index = 0; index < chunks.Count; index++)
            {
                WriteRaw(ChunkTarget(index), chunks[index]);
            }
        }

        // The manifest is written last: a crash mid-write never points at chunks that aren't there.
        WriteRaw(target, chunked ? ChunkPrefix + chunks.Count : value);

        // A value shorter than last time leaves no stale chunks behind; a single-chunk value
        // wrote none, so any chunk credentials from before are stale from index zero.
        for (var index = chunked ? chunks.Count : 0; index < MaxChunks; index++)
        {
            if (!DeleteRaw(ChunkTarget(index))) break;
        }
    }

    /// <summary>The value cut into pieces that each fit a credential, never splitting a
    /// character's UTF-8 bytes.</summary>
    private static List<string> Split(string value)
    {
        var chunks = new List<string>();
        if (value.Length == 0)
        {
            chunks.Add(value);
            return chunks;
        }

        var start = 0;
        while (start < value.Length)
        {
            var length = Math.Min(ChunkBytes, value.Length - start);
            while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(start, length)) > ChunkBytes)
            {
                length--;
            }
            if (length > 0 && char.IsHighSurrogate(value[start + length - 1]))
            {
                length--;
            }
            if (length == 0)
            {
                length = 1;
            }
            chunks.Add(value.Substring(start, length));
            start += length;
        }
        return chunks;
    }

    private string ChunkTarget(int index) => $"{target}#{index}";

    private static string? ReadRaw(string name)
    {
        if (!CredRead(name, GenericType, 0, out var pointer))
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

    private static void WriteRaw(string name, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = name,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException("Windows refused to keep the sign-in.");
            }
        }
        finally
        {
            // The blob held the session: it is wiped before it is freed.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    private static bool DeleteRaw(string name)
    {
        if (CredDelete(name, GenericType, 0))
        {
            return true;
        }
        if (Marshal.GetLastWin32Error() == NotFound)
        {
            return false;
        }
        throw new InvalidOperationException("The saved sign-in couldn't be removed.");
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
