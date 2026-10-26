namespace Koda.Core;

/// <summary>A signed-in account as the app hands it over: whose it is, and its cookies.</summary>
public sealed record AccountSession(Guid AccountId, YouTubeCookies Cookies);

/// <summary>YouTube's word on a session, from a response to a request signed with it.</summary>
/// <param name="SignedIn">False once YouTube answered a signed request as anonymous: the session
/// is over and only signing in again brings it back. True again if a later answer says so.</param>
public sealed record AccountVerdict(Guid AccountId, bool SignedIn);

/// <summary>What one request went out with, so its response is credited to that sign-in even
/// when the account has been signed in afresh since.</summary>
public sealed record AccountTicket(Guid AccountId, int Generation, YouTubeCookies Cookies);

/// <summary>
/// The session <see cref="YouTubeMusic"/> signs requests with, and the bookkeeping that follows them.
///
/// One sign-in is a <em>generation</em>: signing in again starts a new one, while Google rotating
/// a cookie keeps it. Rotated cookies from a response are folded into the account its request was
/// made for and written to the store; a response for a sign-in that has since ended writes
/// nothing anywhere.
///
/// The only verdict on a session is YouTube's own <c>logged_in</c> in a response's
/// <c>responseContext</c>. HTTP 200 proves nothing (a dead session reads public pages fine), and
/// cookies are never dropped over one bad answer: a later rotation can revive them.
///
/// The Swift port is an actor; this one takes a lock, because callers are synchronous .NET code.
/// Events are raised outside the lock so a listener that calls back in cannot deadlock.
/// </summary>
public sealed class AccountSessions
{
    private sealed class Entry
    {
        public int Generation { get; init; }
        public YouTubeCookies Cookies { get; set; } = new([]);
        public bool? SignedIn { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private Guid? _activeId;
    private int _generations;

    /// <summary>Cookies Google rotated mid-session, so the app can keep them in its secret store.</summary>
    public event Action<YouTubeCookies>? CookiesChanged;

    /// <summary>Each change in an account's verdict, for the app to follow.</summary>
    public event Action<AccountVerdict>? VerdictChanged;

    /// <summary>Sign requests as <paramref name="session"/>'s account, as on launch. If this run has
    /// already seen the account, the cookies it holds are kept: they include rotations the app's
    /// copy may predate. An account seen for the first time takes <paramref name="session"/>'s.</summary>
    public void Activate(AccountSession session)
    {
        lock (_lock)
        {
            if (!_entries.ContainsKey(session.AccountId))
            {
                _entries[session.AccountId] = new Entry { Generation = NextGeneration(), Cookies = session.Cookies };
            }
            _activeId = session.AccountId;
        }
    }

    /// <summary>A fresh sign-in for the account: replaces whatever it had, writes it to the store,
    /// and makes it active. Responses to requests from before it no longer count.</summary>
    public void SignIn(AccountSession session)
    {
        lock (_lock)
        {
            _entries[session.AccountId] = new Entry { Generation = NextGeneration(), Cookies = session.Cookies };
            _activeId = session.AccountId;
        }
        CookiesChanged?.Invoke(session.Cookies);
    }

    /// <summary>Send requests signed out, for a library on this device only.</summary>
    public void Deactivate()
    {
        lock (_lock) _activeId = null;
    }

    /// <summary>The account signed out: nothing more is written for it, not even by requests still
    /// in flight. Deleting its stored cookies is the caller's.</summary>
    public void Forget(Guid accountId)
    {
        lock (_lock)
        {
            _entries.Remove(accountId);
            if (_activeId == accountId) _activeId = null;
        }
    }

    /// <summary>The session requests are signed with now, cookies as of the latest rotation.</summary>
    public AccountSession? Active
    {
        get
        {
            lock (_lock)
            {
                return _activeId is { } id && _entries.TryGetValue(id, out var entry)
                    ? new AccountSession(id, entry.Cookies)
                    : null;
            }
        }
    }

    /// <summary>The account's newest cookies this run, rotations included.</summary>
    public YouTubeCookies? CookiesFor(Guid accountId)
    {
        lock (_lock) return _entries.TryGetValue(accountId, out var entry) ? entry.Cookies : null;
    }

    /// <summary>The last verdict this run for the account, null before any signed answer.</summary>
    public bool? SignedIn(Guid accountId)
    {
        lock (_lock) return _entries.TryGetValue(accountId, out var entry) ? entry.SignedIn : null;
    }

    /// <summary>What a request goes out with; null when nothing is signed in.</summary>
    public AccountTicket? Ticket()
    {
        lock (_lock)
        {
            return _activeId is { } id && _entries.TryGetValue(id, out var entry)
                ? new AccountTicket(id, entry.Generation, entry.Cookies)
                : null;
        }
    }

    /// <summary>What the page cache is keyed by, so one session's signed-in pages are never served
    /// to another (or to the same account signed in afresh).</summary>
    public string CacheScope
    {
        get
        {
            lock (_lock)
            {
                return _activeId is { } id && _entries.TryGetValue(id, out var entry)
                    ? $"{id}#{entry.Generation}"
                    : "";
            }
        }
    }

    /// <summary>Fold a response's <c>Set-Cookie</c> headers into the cookies of the sign-in
    /// <paramref name="ticket"/> was issued for.</summary>
    public void Received(IReadOnlyList<string> setCookieHeaders, AccountTicket ticket, DateTimeOffset? now = null)
    {
        if (setCookieHeaders.Count == 0) return;
        YouTubeCookies merged;
        lock (_lock)
        {
            if (!_entries.TryGetValue(ticket.AccountId, out var entry) || entry.Generation != ticket.Generation) return;
            merged = entry.Cookies.Merging(SetCookie.Parse(setCookieHeaders, now), ticket.Cookies);
            if (merged.Equals(entry.Cookies)) return;
            entry.Cookies = merged;
        }
        CookiesChanged?.Invoke(merged);
    }

    /// <summary>Record YouTube's verdict on the sign-in <paramref name="ticket"/> was issued for.</summary>
    public void Note(bool signedIn, AccountTicket ticket)
    {
        AccountVerdict? verdict = null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(ticket.AccountId, out var entry) || entry.Generation != ticket.Generation
                || entry.SignedIn == signedIn) return;
            entry.SignedIn = signedIn;
            verdict = new AccountVerdict(ticket.AccountId, signedIn);
        }
        VerdictChanged?.Invoke(verdict);
    }

    private int NextGeneration() => ++_generations;
}
