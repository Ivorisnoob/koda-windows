using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core.Profiles;
using Koda.Interop;
using Koda.Pages;
using Koda.Playback;

namespace Koda.Services;

/// <summary>
/// The app's side of profiles: the roster, and moving the library and session between them.
/// Switching is local and instant; whatever an account needs follows behind it.
/// </summary>
public static class Profiles
{
    private static bool _switching;

    public static ProfileRoster Roster { get; } = new(App.DataDirectory);

    public static string ActiveFolder => Roster.Folder(Roster.Active);

    public static Koda.Core.ISecretStore ActiveCredentials => new CredentialStore(ProfileRoster.CredentialTarget(Roster.Active));

    /// <summary>The active profile keeps up with its account: who it is, or that it needs signing in.</summary>
    public static void FollowAccount()
    {
        if (_switching) return;
        var account = App.Account;
        var signedIn = account.IsSignedIn;
        var info = account.Info;
        var wasSignedIn = Roster.Active.IsSignedIn;
        Roster.Update(Roster.ActiveId, profile =>
        {
            profile.Kind = signedIn ? ProfileKind.YouTube : ProfileKind.Device;
            profile.IsExpired = signedIn && account.IsExpired;
            if (!signedIn)
            {
                profile.Handle = null;
                profile.AccountPhoto = null;
                profile.DatasyncId = null;
                return;
            }

            if (info is null) return;
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = info.Name;
            profile.Handle = info.Handle ?? profile.Handle;
            profile.AccountPhoto = info.Photo?.Url.ToString() ?? profile.AccountPhoto;
            profile.DatasyncId = info.DatasyncId ?? profile.DatasyncId;
        });

        // A Home saved for the other state is the wrong person's: signed in, it is the account's.
        if (wasSignedIn != signedIn) ForgetSavedHome();
    }

    private static void ForgetSavedHome()
    {
        try
        {
            File.Delete(Path.Combine(ActiveFolder, "home-snapshot.json"));
        }
        catch (Exception)
        {
            // The next load replaces it anyway.
        }
    }

    /// <summary>The same account: by YouTube's id when it gave one, else by its @handle.</summary>
    private static bool SameAccount(KodaProfile a, KodaProfile b) =>
        (a.DatasyncId is not null && a.DatasyncId == b.DatasyncId)
        || (a.Handle is not null && string.Equals(a.Handle, b.Handle, StringComparison.OrdinalIgnoreCase));

    /// <summary>Another profile takes over: its library everywhere at once, its session or none.
    /// Music keeps playing; a video belongs to the profile it was started in, so it closes.</summary>
    public static async Task SwitchAsync(Guid id)
    {
        if (id == Roster.ActiveId || Roster.Profile(id) is not { } profile || _switching) return;
        _switching = true;
        try
        {
            VideoPlaybackService.Instance.Stop();
            var folder = Roster.Folder(profile);
            await App.Account.SwitchAsync(
                folder,
                new CredentialStore(ProfileRoster.CredentialTarget(profile)),
                () => App.Library.Open(folder));
            Roster.SetActive(id);
        }
        finally
        {
            _switching = false;
        }

        FollowAccount();
        App.Shell?.NavigateRoot(typeof(HomePage), null, "home", fresh: true);
        App.Shell?.Note($"Switched to {profile.DisplayName}");
    }

    /// <summary>Back to the profile before this one.</summary>
    public static Task SwitchBackAsync() =>
        Roster.PreviousId is { } previous ? SwitchAsync(previous)
        : Roster.Profiles.FirstOrDefault(p => p.Id != Roster.ActiveId) is { } other ? SwitchAsync(other.Id)
        : Task.CompletedTask;

    public static async Task DeleteAsync(Guid id)
    {
        if (Roster.Profile(id) is not { } profile || !Roster.HasSeveral) return;
        if (id == Roster.ActiveId)
        {
            await SwitchBackAsync();
        }

        var credentials = new CredentialStore(ProfileRoster.CredentialTarget(profile));
        if (Roster.Delete(id))
        {
            try
            {
                credentials.Write(null);
            }
            catch (Exception)
            {
                // A stored session with no profile is never read again.
            }
        }
    }

    /// <summary>A new profile signed in to an account. Giving up on the sign-in leaves everything
    /// as it was, and an account already here goes to its own profile rather than a second one.</summary>
    public static async Task AddSignedInAsync()
    {
        var before = Roster.ActiveId;
        var added = Roster.AddDeviceProfile("");
        await SwitchAsync(added.Id);
        await YouTubeSignInDialog.ShowSignInAsync();

        if (!App.Account.IsSignedIn)
        {
            await SwitchAsync(before);
            await DeleteAsync(added.Id);
            return;
        }

        FollowAccount();
        var signedIn = Roster.Profile(added.Id);
        var twin = signedIn is null ? null : Roster.Profiles.FirstOrDefault(p => p.Id != added.Id && SameAccount(p, signedIn));
        if (twin is not null)
        {
            await SwitchAsync(twin.Id);
            await DeleteAsync(added.Id);
            App.Shell?.Note($"That account is already {twin.DisplayName}");
            return;
        }

        ForgetSavedHome();
        App.Shell?.NavigateRoot(typeof(HomePage), null, "home", fresh: true);
    }
}
