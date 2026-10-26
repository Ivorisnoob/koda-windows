namespace Koda.Core;

/// <summary>
/// Where a secret is kept: somewhere only this user on this machine can read (the Windows
/// Credential Manager in the app; memory in tests). Null clears it.
/// </summary>
public interface ISecretStore
{
    string? Read();

    void Write(string? value);
}
