using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Koda.Pages;

/// <summary>
/// Signing in to YouTube Music on Google's own page, in a WebView2 with a fresh temporary
/// profile: a second sign-in can never silently be the first one again, and nothing of Google's
/// stays behind once the dialog closes. Koda never sees the password; once Google sends the
/// browser back to YouTube Music, the youtube.com cookies it set are the session.
/// </summary>
public sealed partial class YouTubeSignInDialog : ContentDialog
{
    /// <summary>Through youtube.com's own sign-in handler, as the YouTube website does, which
    /// then sends the browser on to YouTube Music.</summary>
    private const string StartUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true"
        + "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue"
        + "%26app%3Ddesktop%26hl%3Den%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    /// <summary>Google offers passkeys to the page, but the passkey flow dead-ends in an
    /// embedded browser, so hiding the API before Google's scripts look sends it down the
    /// password path. The same script the Mac's sign-in view injects.</summary>
    private const string HidePasskeys = """
        (function () {
            try {
                delete window.PublicKeyCredential;
                Object.defineProperty(window, "PublicKeyCredential", { value: undefined, writable: false, configurable: false });
            } catch (e) {}
            try {
                var credentials = window.navigator && window.navigator.credentials;
                if (!credentials) { return; }
                var prototype = Object.getPrototypeOf(credentials);
                var wrap = function (original) {
                    if (typeof original !== "function") { return original; }
                    return function (options) {
                        if (options && options.publicKey) {
                            return Promise.reject(new DOMException("Passkeys are not available in this app.", "NotAllowedError"));
                        }
                        return original.apply(this, arguments);
                    };
                };
                prototype.get = wrap(prototype.get);
                prototype.create = wrap(prototype.create);
            } catch (e) {}
        })();
        """;

    private readonly string _profileFolder;
    private bool _initialized;
    private bool _finishing;
    private bool _closed;

    public YouTubeSignInDialog()
    {
        InitializeComponent();
        _profileFolder = Path.Combine(Path.GetTempPath(), "Koda",
            "youtube-signin-" + Guid.NewGuid().ToString("N"));
        Browser.Loaded += OnBrowserLoaded;
        Browser.NavigationCompleted += OnNavigationCompleted;
        Closed += OnClosed;
    }

    /// <summary>The dialog over the shell, sized to the room the window has.</summary>
    public static async Task ShowSignInAsync()
    {
        if (App.Shell?.Content?.XamlRoot is not { } root)
        {
            return;
        }

        var dialog = new YouTubeSignInDialog { XamlRoot = root };
        dialog.Fit(root.Size);
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // Another dialog is up: the sign-in steps aside rather than crashing the app.
            dialog.Cleanup();
        }
    }

    private void Fit(Windows.Foundation.Size size)
    {
        // The dialog's template caps its border at 548 DIP, so the page is kept under that:
        // Google's two-column sign-in clips on the right when the content is wider than the room.
        var width = Math.Clamp(size.Width - 160, 360, 500);
        var height = Math.Clamp(size.Height - 240, 260, 640);
        Layout.Width = width;
        Layout.Height = height;
        MaxWidth = width + 80;
        MaxHeight = height + 180;
    }

    private async void OnBrowserLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _closed) return;
        _initialized = true;
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            string version;
            try
            {
                version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception)
            {
                ShowError("The sign-in page needs Microsoft Edge WebView2, which isn't installed. "
                    + "Install it from Microsoft's website, then try again.");
                return;
            }

            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null!,
                userDataFolder: _profileFolder,
                options: new CoreWebView2EnvironmentOptions());
            if (_closed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed || Browser.CoreWebView2 is null) return;

            var settings = Browser.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            // Google turns away browsers it takes for embedded ones. The WebView2 engine is
            // Chromium, so it presents Chrome's own Windows identity, as the Mac's view presents
            // Safari's. The major comes from the installed runtime, not a version that ages out.
            if (version.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } major)
            {
                settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                    + "AppleWebKit/537.36 (KHTML, like Gecko) "
                    + $"Chrome/{major}.0.0.0 Safari/537.36";
            }

            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                Browser.CoreWebView2?.Navigate(args.Uri);
            };
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HidePasskeys);
            if (_closed) return;
            Browser.CoreWebView2.Navigate(StartUrl);
        }
        catch (Exception)
        {
            if (!_closed)
            {
                ShowError("The sign-in page couldn't open. Check the connection and try again.");
            }
        }
    }

    /// <summary>Each page that finishes on YouTube is a chance the session's cookies are there.</summary>
    private async void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_closed || _finishing || !args.IsSuccess || Browser.CoreWebView2 is null) return;
        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var uri)) return;
        if (!IsYouTubeHost(uri.Host)) return;
        await ReadSessionAsync();
    }

    private async Task ReadSessionAsync()
    {
        if (_closed || _finishing || Browser.CoreWebView2 is null) return;

        YouTubeCookies cookies;
        try
        {
            var reported = await Browser.CoreWebView2.CookieManager
                .GetCookiesAsync("https://music.youtube.com/");
            cookies = YouTubeCookies.FromWebView(reported.Select(cookie => new YouTubeCookies.WebCookie(
                cookie.Name,
                cookie.Value,
                cookie.Domain,
                !cookie.IsSession && cookie.Expires > 0 && cookie.Expires < 253402300799
                    ? DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires)
                    : null)));
        }
        catch (Exception)
        {
            return;
        }

        // The five cookies Google sets on a real sign-in, not just a jar with look-alikes.
        if (_closed || _finishing || !cookies.HasSignInCookies) return;

        _finishing = true;
        ShowBusy();
        try
        {
            await EngineHost.Account.CompleteSignInAsync(cookies);
            if (!_closed) Hide();
        }
        catch (YouTubeSignInException error)
        {
            _finishing = false;
            if (!_closed) ShowError(error.Message);
        }
        catch (MusicException error)
        {
            _finishing = false;
            if (!_closed) ShowError(SignInProblem(error));
        }
        catch (Exception)
        {
            _finishing = false;
            if (!_closed) ShowError("YouTube Music didn't accept the sign-in. Try again in a moment.");
        }
    }

    private static string SignInProblem(MusicException error) => error.Kind switch
    {
        MusicException.Reason.SignedOut => "YouTube didn't accept the sign-in. Try again in a moment.",
        MusicException.Reason.Network => "Koda couldn't reach YouTube. Check the connection and try again.",
        MusicException.Reason.RateLimited => "YouTube asked Koda to wait a moment. Try again shortly.",
        _ => "YouTube Music didn't accept the sign-in. Try again in a moment.",
    };

    private async void OnTryAgainClick(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        _finishing = false;
        ShowBrowser();

        if (Browser.CoreWebView2 is null)
        {
            _initialized = false;
            await InitializeAsync();
            return;
        }

        try
        {
            // A clean jar, as the first attempt had: a half-finished Google session must not
            // decide the next one.
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
            if (_closed) return;
            Browser.CoreWebView2.Navigate(StartUrl);
        }
        catch (Exception)
        {
            if (!_closed)
            {
                ShowError("The sign-in page couldn't be opened again. Close this and try once more.");
            }
        }
    }

    private void ShowBrowser()
    {
        Browser.Visibility = Visibility.Visible;
        BusyShade.Visibility = Visibility.Collapsed;
        ErrorShade.Visibility = Visibility.Collapsed;
    }

    private void ShowBusy()
    {
        Browser.Visibility = Visibility.Collapsed;
        BusyShade.Visibility = Visibility.Visible;
        ErrorShade.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        Browser.Visibility = Visibility.Collapsed;
        BusyShade.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorShade.Visibility = Visibility.Visible;
    }

    private static bool IsYouTubeHost(string host) =>
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => Cleanup();

    /// <summary>The browser and its profile go; the profile's files can take a moment to let go.</summary>
    private void Cleanup()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            Browser.Close();
        }
        catch (Exception)
        {
            // Already gone, or never started.
        }
        DeleteLater(_profileFolder);
    }

    private static void DeleteLater(string folder) => _ = Task.Run(async () =>
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception)
            {
                await Task.Delay(300);
            }
        }
    });
}
