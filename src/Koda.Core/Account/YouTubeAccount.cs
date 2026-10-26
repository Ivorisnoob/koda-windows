using System.Text.Json;

namespace Koda.Core;

/// <summary>Google didn't finish a sign-in: the page never set a session's cookies.</summary>
public sealed class YouTubeSignInException(string message) : Exception(message);

/// <summary>
/// The library's YouTube Music account: which session signs requests, signing in and out,
/// YouTube's verdicts on the session, and keeping the library in step with the account.
///
/// The Windows app has no profiles, so there is one account for the whole library. Session
/// changes, writes and history pings go through <c>_work</c> one at a time, and every write checks
/// the session is still live before it goes out. Reading the account's library runs on its own and
/// is dropped if the account signs out while it ran.
///
/// Cookies live in <see cref="ISecretStore"/> (the Windows Credential Manager in the app); the
/// account's name and photo live in <c>account.json</c>, and what the account held last time plus
/// the listener's unsent changes live in <c>account-mirror.json</c>, both in the data root.
/// </summary>
public sealed class YouTubeAccount : IDisposable
{
    /// <summary>One account per library: the key its session is filed under.</summary>
    private static readonly Guid AccountId = new("3f1b8c1e-6f2a-4d0e-9a51-2c7b4a9d8e10");

    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(15);

    private readonly YouTubeMusic _music;
    private readonly Library _library;
    private readonly Settings _settings;
    private readonly ISecretStore _secrets;
    private readonly string _directory;
    private readonly string _infoFile;
    private readonly string _mirrorFile;

    /// <summary>One piece of account work at a time: sign-in, sign-out, sync, a write, a ping.</summary>
    private readonly SemaphoreSlim _work = new(1, 1);
    /// <summary>The mirror and the last library snapshot, together: a push reads both.</summary>
    private readonly object _mirrorLock = new();
    private readonly object _syncGate = new();
    private readonly HeardThreshold _threshold = new();
    private readonly CancellationTokenSource _stopping = new();

    private AccountMirror _mirror;
    private LibrarySnapshot _lastSeen;
    private AccountInfo? _info;
    private DateTimeOffset? _lastSync;
    private volatile bool _isExpired;
    private volatile bool _isSyncing;
    private bool _syncingLibrary;
    private bool _started;
    private int _changeVersion;
    private int _disposed;

    public YouTubeAccount(YouTubeMusic music, Library library, Settings settings, ISecretStore secrets, string directory)
    {
        _music = music;
        _library = library;
        _settings = settings;
        _secrets = secrets;
        _directory = directory;
        _infoFile = Path.Combine(directory, "account.json");
        _mirrorFile = Path.Combine(directory, "account-mirror.json");
        _mirror = LoadMirror();
        _lastSeen = Snapshot();
        _syncingLibrary = settings.SyncsYouTubeLibrary;
    }

    /// <summary>Raised when what the Settings page shows changes: signed in, expired, syncing.</summary>
    public event Action? Changed;

    /// <summary>Who is signed in, once the account menu has answered (or a previous run saved it).</summary>
    public AccountInfo? Info => _info;

    public bool IsSignedIn => _music.Accounts.Active is not null;

    /// <summary>YouTube answered a signed request as anonymous: only signing in again brings it back.</summary>
    public bool IsExpired => _isExpired;

    public bool IsSyncing => _isSyncing;

    /// <summary>At launch: the saved session, then the account's library, then a sync every quarter hour.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _music.Accounts.CookiesChanged += PersistCookies;
        _music.Accounts.VerdictChanged += OnVerdict;
        _library.Changed += OnLibraryChanged;
        _settings.Changed += OnSettingsChanged;

        RestoreSession();
        if (IsSignedIn)
        {
            _ = SyncAsync(force: true);
            _ = _music.RenewVisitorIdentityAsync();
        }
        _ = SyncLoopAsync();
    }

    // MARK: Signing in and out

    /// <summary>
    /// The sign-in page has youtube.com's cookies. YouTube is asked who they belong to before the
    /// app calls the sign-in complete; a failure puts back the session that was there before.
    /// </summary>
    public async Task CompleteSignInAsync(YouTubeCookies cookies, CancellationToken cancellation = default)
    {
        if (!cookies.LooksSignedIn)
        {
            throw new YouTubeSignInException("Google didn't finish signing in to YouTube Music. Try again.");
        }

        await _work.WaitAsync(cancellation);
        try
        {
            var previous = _music.Accounts.Active?.Cookies;
            _music.Accounts.SignIn(new AccountSession(AccountId, cookies));
            AccountInfo info;
            try
            {
                info = await _music.AccountInfoAsync(cancellation);
            }
            catch
            {
                // The cookies never proved a session: put back what the app had, so a failed
                // sign-in cannot sign out the account that was working.
                _music.Accounts.Forget(AccountId);
                if (previous is not null)
                {
                    _music.Accounts.Activate(new AccountSession(AccountId, previous));
                    PersistCookies(previous);
                }
                else
                {
                    TryWriteSecret(null);
                }
                _music.ClearPageCache();
                throw;
            }

            _info = info;
            _isExpired = false;
            SaveInfo();
            _music.ClearPageCache();
            lock (_mirrorLock) _lastSeen = Snapshot();
            Notify();
        }
        finally
        {
            _work.Release();
        }

        _ = _music.RenewVisitorIdentityAsync();
        await SyncAsync(force: true, cancellation);
    }

    /// <summary>The account is let go; the library stays on this device with everything it holds.</summary>
    public void SignOut()
    {
        _music.Accounts.Forget(AccountId);
        TryWriteSecret(null);
        _info = null;
        _isExpired = false;
        DeleteFile(_infoFile);
        DeleteFile(_mirrorFile);
        lock (_mirrorLock)
        {
            _mirror = new AccountMirror();
            _lastSeen = Snapshot();
        }
        _music.ClearPageCache();
        _ = _music.RenewVisitorIdentityAsync();
        Notify();
    }

    // MARK: Library sync

    /// <summary>
    /// Bring the library in step with the account: unsent changes go first, then what the account
    /// holds comes in and what was removed from it goes. At most every 15 minutes unless forced
    /// (sign-in, a setting turned on, a manual refresh).
    /// </summary>
    public async Task SyncAsync(bool force = false, CancellationToken cancellation = default)
    {
        if (!_settings.SyncsYouTubeLibrary || _music.Accounts.Active is null || _isExpired) return;
        lock (_syncGate)
        {
            if (!force && _lastSync is { } last && DateTimeOffset.UtcNow - last < SyncInterval) return;
            _lastSync = DateTimeOffset.UtcNow;
        }

        await _work.WaitAsync(cancellation);
        try
        {
            if (_music.Accounts.Active is null) return;
            await SendPendingAsync(cancellation);
            if (_music.Accounts.Active is null || _isExpired) return;

            _isSyncing = true;
            Notify();

            AccountLibrary account;
            try
            {
                var liked = _music.LikedSongsAsync(cancellation: cancellation);
                var albums = _music.LibraryAlbumsAsync(cancellation: cancellation);
                var playlists = _music.LibraryPlaylistsAsync(cancellation: cancellation);
                var artists = _music.LibraryArtistsAsync(cancellation: cancellation);
                await Task.WhenAll(liked, albums, playlists, artists);
                account = new AccountLibrary(await liked, await albums, await playlists, await artists);
            }
            catch (MusicException e) when (e.Kind == MusicException.Reason.SignedOut)
            {
                MarkExpired();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Offline or refused for now: the next chance tries again.
                lock (_syncGate) _lastSync = null;
                return;
            }

            // A signed request can have come back as anonymous while the pages were read.
            if (_music.Accounts.Active is null || _isExpired) return;

            try
            {
                var info = await _music.AccountInfoAsync(cancellation);

                // Signing out while the account menu was read must not write the account back.
                if (_music.Accounts.Active is null || _isExpired) return;
                if (info != _info)
                {
                    _info = info;
                    SaveInfo();
                }
            }
            catch (MusicException e) when (e.Kind == MusicException.Reason.SignedOut)
            {
                MarkExpired();
                return;
            }
            catch (Exception)
            {
                // The library is what matters; the name and photo refresh next time.
            }

            // The library merge/drop is quiet (no Changed), so holding the mirror lock across it
            // cannot re-enter a listener. Lock order is always mirror -> library.
            List<AccountChange> local;
            lock (_mirrorLock)
            {
                // Signing out while the account was read must not bring its library in after.
                if (_music.Accounts.Active is null || _isExpired) return;

                // An edit made while the account's pages were read is queued first: without it,
                // Reconcile would bring back a song the listener unliked in the meantime.
                var now = Snapshot();
                local = Differences(_lastSeen, now);
                _lastSeen = now;
                foreach (var change in local) _mirror.Queue(change);
                _mirror.Reconcile(_library, account);
                _lastSeen = Snapshot();
                SaveMirror();
            }
            if (local.Count > 0) await SendPendingAsync(cancellation);
            Notify();
        }
        finally
        {
            _isSyncing = false;
            _work.Release();
            Notify();
        }
    }

    // MARK: The listener's own changes

    /// <summary>
    /// Library.Changed carries no payload, so changes are found by diffing the library against the
    /// snapshot taken after the last change or sync: what is new is a like or save, what is gone
    /// is an undo. A short delay coalesces the several edits one action can make.
    /// </summary>
    private void OnLibraryChanged()
    {
        if (!_settings.SyncsYouTubeLibrary || _music.Accounts.Active is null) return;
        var version = Interlocked.Increment(ref _changeVersion);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, _stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // A newer change is already scheduled and will see this one in its snapshot.
            if (version != Volatile.Read(ref _changeVersion)) return;
            await PushChangesAsync();
        });
    }

    private async Task PushChangesAsync()
    {
        await _work.WaitAsync();
        try
        {
            if (!_settings.SyncsYouTubeLibrary || _music.Accounts.Active is null || _isExpired) return;
            List<AccountChange> changes;
            lock (_mirrorLock)
            {
                var now = Snapshot();
                changes = Differences(_lastSeen, now);
                _lastSeen = now;
                foreach (var change in changes) _mirror.Queue(change);
                if (changes.Count > 0) SaveMirror();
            }
            if (changes.Count > 0) await SendPendingAsync(CancellationToken.None);
        }
        finally
        {
            _work.Release();
        }
    }

    /// <summary>Changes made offline or before a sign-in expired, oldest first; stops at the first
    /// that has to wait, so they reach the account in the order they were made.</summary>
    private async Task SendPendingAsync(CancellationToken cancellation)
    {
        List<AccountChange> pending;
        lock (_mirrorLock) pending = [.. _mirror.Pending];
        foreach (var change in pending)
        {
            bool sent;
            try
            {
                sent = await WriteAsync(change, cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (MusicException e) when (e.Kind == MusicException.Reason.SignedOut)
            {
                MarkExpired();
                return;
            }
            catch (MusicException e) when (e.Kind is MusicException.Reason.Network or MusicException.Reason.RateLimited)
            {
                lock (_syncGate) _lastSync = null;
                return;
            }
            catch (Exception)
            {
                // A change YouTube turns down outright counts as done, or it would block every
                // change behind it.
                sent = true;
            }
            if (!sent) return;
            lock (_mirrorLock)
            {
                _mirror.Sent(change);
                SaveMirror();
            }
        }
    }

    /// <summary>Sends one change; false when it should wait and go again later.</summary>
    private async Task<bool> WriteAsync(AccountChange change, CancellationToken cancellation)
    {
        switch (change.Kind)
        {
            case AccountChangeKind.Liked:
                await _music.SetLikedAsync(change.Id, change.IsOn, cancellation);
                break;
            case AccountChangeKind.Playlist:
                await _music.SetSavedAsync(change.Id, change.IsOn, cancellation);
                break;
            case AccountChangeKind.Artist:
                await _music.SetSubscribedAsync(change.Id, change.IsOn, cancellation);
                break;
            case AccountChangeKind.Album:
                // Albums are saved by their audio playlist, not their MPRE id.
                var page = await _music.AlbumAsync(change.Id, cancellation);
                if (page.AudioPlaylistId is not { } audio) return true;
                await _music.SetSavedAsync(audio, change.IsOn, cancellation);
                break;
        }
        return true;
    }

    // MARK: YouTube history

    /// <summary>A play heard for 15 seconds goes to the account's YouTube history, if the listener
    /// wants that and listening history isn't paused.</summary>
    public void ItemHeard(Guid occurrence, string videoId, double? duration, double seconds)
    {
        if (!_threshold.Add(seconds, occurrence, duration)) return;
        if (!_settings.SendsPlaysToYouTube || !_settings.SavesListeningHistory) return;
        if (_music.Accounts.Active is null || _isExpired) return;

        _ = Task.Run(async () =>
        {
            await _work.WaitAsync();
            try
            {
                if (_music.Accounts.Active is null || _isExpired || !_settings.SendsPlaysToYouTube) return;
                await _music.ReportPlaybackAsync(videoId, _stopping.Token);
            }
            catch (MusicException e) when (e.Kind == MusicException.Reason.SignedOut)
            {
                MarkExpired();
            }
            catch (Exception)
            {
                // A play that couldn't be filed isn't retried: history is a record of now.
            }
            finally
            {
                _work.Release();
            }
        });
    }

    // MARK: Sessions

    private void RestoreSession()
    {
        var stored = _secrets.Read();
        if (string.IsNullOrEmpty(stored)) return;
        try
        {
            var cookies = JsonSerializer.Deserialize<YouTubeCookies>(stored, AccountJson.Options);
            if (cookies is null || !cookies.LooksSignedIn) return;
            _music.Accounts.Activate(new AccountSession(AccountId, cookies));
            _info = LoadInfo();
            _music.ClearPageCache();
        }
        catch (Exception)
        {
            // An unreadable secret starts signed out; signing in again replaces it.
        }
    }

    private void PersistCookies(YouTubeCookies cookies) => TryWriteSecret(
        JsonSerializer.Serialize(cookies, AccountJson.Options));

    private void TryWriteSecret(string? value)
    {
        try
        {
            _secrets.Write(value);
        }
        catch (Exception)
        {
            // A credential the system refused still works this run; the next rotation retries.
        }
    }

    /// <summary>YouTube's word marks the account as needing sign-in, or clears that again.</summary>
    private void OnVerdict(AccountVerdict verdict)
    {
        if (verdict.AccountId != AccountId || _music.Accounts.Active is null) return;
        if (_isExpired == !verdict.SignedIn) return;
        _isExpired = !verdict.SignedIn;
        Notify();
        if (!_isExpired) _ = SyncAsync(force: true);
    }

    private void MarkExpired()
    {
        // A write still in flight when the listener signed out must not bring the warning back.
        if (_isExpired || !IsSignedIn) return;
        _isExpired = true;
        Notify();
    }

    private void OnSettingsChanged(Settings settings)
    {
        if (!settings.SyncsYouTubeLibrary)
        {
            _syncingLibrary = false;
            return;
        }
        if (_syncingLibrary) return;
        _syncingLibrary = true;
        _ = SyncAsync(force: true);
    }

    private async Task SyncLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(SyncInterval);
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                await SyncAsync(force: true, _stopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The app is closing.
        }
        catch (Exception)
        {
            // A sync that threw is the next tick's problem.
        }
    }

    // MARK: The account mirror

    private sealed record LibrarySnapshot(
        HashSet<string> Liked,
        HashSet<string> Albums,
        HashSet<string> Playlists,
        HashSet<string> Artists);

    private LibrarySnapshot Snapshot() => new(
        _library.Liked.Select(song => song.Id).ToHashSet(StringComparer.Ordinal),
        _library.Albums.Select(album => album.Id).ToHashSet(StringComparer.Ordinal),
        _library.Playlists.Select(playlist => playlist.Id).ToHashSet(StringComparer.Ordinal),
        _library.Artists.Select(artist => artist.Id).ToHashSet(StringComparer.Ordinal));

    private static List<AccountChange> Differences(LibrarySnapshot before, LibrarySnapshot after)
    {
        var changes = new List<AccountChange>();

        void Diff(AccountChangeKind kind, HashSet<string> previous, HashSet<string> now)
        {
            foreach (var id in now.Except(previous)) changes.Add(new AccountChange(kind, id, true));
            foreach (var id in previous.Except(now)) changes.Add(new AccountChange(kind, id, false));
        }

        Diff(AccountChangeKind.Liked, before.Liked, after.Liked);
        Diff(AccountChangeKind.Album, before.Albums, after.Albums);
        Diff(AccountChangeKind.Playlist, before.Playlists, after.Playlists);
        Diff(AccountChangeKind.Artist, before.Artists, after.Artists);
        return changes;
    }

    private AccountMirror LoadMirror()
    {
        try
        {
            if (!File.Exists(_mirrorFile)) return new AccountMirror();
            var mirror = JsonSerializer.Deserialize<AccountMirror>(File.ReadAllText(_mirrorFile), AccountJson.Options)
                ?? new AccountMirror();
            mirror.Liked ??= [];
            mirror.Albums ??= [];
            mirror.Playlists ??= [];
            mirror.Artists ??= [];
            mirror.Pending ??= [];
            return mirror;
        }
        catch (Exception)
        {
            return new AccountMirror();
        }
    }

    private AccountInfo? LoadInfo()
    {
        try
        {
            if (!File.Exists(_infoFile)) return null;
            return JsonSerializer.Deserialize<AccountInfo>(File.ReadAllText(_infoFile), AccountJson.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveInfo()
    {
        if (_info is null) return;
        try
        {
            Directory.CreateDirectory(_directory);
            WriteAtomically(_infoFile, JsonSerializer.Serialize(_info, AccountJson.Options));
        }
        catch (Exception)
        {
            // The account still works; the name is fetched again on the next sync.
        }
    }

    private void SaveMirror()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            WriteAtomically(_mirrorFile, JsonSerializer.Serialize(_mirror, AccountJson.Options));
        }
        catch (Exception)
        {
            // Pending changes stay in memory and are written on the next change.
        }
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // A file left behind is read only while its account is signed in.
        }
    }

    private void Notify()
    {
        foreach (var listener in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)listener)();
            }
            catch (Exception)
            {
                // One listener throwing must not keep the others from updating.
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        _music.Accounts.CookiesChanged -= PersistCookies;
        _music.Accounts.VerdictChanged -= OnVerdict;
        _library.Changed -= OnLibraryChanged;
        _settings.Changed -= OnSettingsChanged;
        _stopping.Dispose();
        _work.Dispose();
    }
}
