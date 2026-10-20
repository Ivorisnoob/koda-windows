<p align="center">
  <img src="src/Koda/Assets/icon.png" width="120" alt="Koda logo"/>
</p>

<h1 align="center">Koda for Windows</h1>

<p align="center">
  <b>A native music player for Windows, powered by YouTube Music.</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-11%2B-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Windows 11 and newer"/>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/WinUI%203-6750A4?style=for-the-badge&logo=windows&logoColor=white" alt="WinUI 3"/>
  <img src="https://img.shields.io/badge/license-GPL--3.0-8B4513?style=for-the-badge&label=license" alt="GPL-3.0 license"/>
</p>

Koda is a family of apps: one native app per operating system, each built with that platform's own
design system. [Koda for Android](https://github.com/Ivorisnoob/Koda) is the shipped one; this
repository is the Windows leg - WinUI 3, Fluent, and the Windows shell's own surfaces.

It works without an official YouTube API key, and an account is optional.

## What is here today

Shipped and working:

- **Home** with shelves, Your Mix, Quick picks, recently played and charts.
- **Search** by song, artist, album or playlist, with recent searches.
- **Album, artist and playlist pages**, each with its own hero and track list.
- **Library**: liked songs, your playlists, listening history and stats.
- **Queue** with shuffle, repeat, Play next, drag to reorder, undo and radio continuation.
- **Now Playing**: the cover's palette as the page's own background, art-adaptive accent.
- **System integration**: media keys and the Windows media card (SMTC), a tray transport, and
  close-to-tray playback.
- **Android import**: read a Koda for Android backup - liked songs, history, playlists, albums.

On the way: lyrics, Last.fm, Discord Rich Presence, downloads and Ready Offline, sign-in,
toasts, `koda://` protocol handling, and a separate Settings window.

## Design rules this app holds to

- **States, in this order:** loading shows a breathing skeleton in the page's own shape; then
  content; then empty (it says how to fill it); then error (title, cause, Try again). Never a
  spinner on a page, never a blank page, never a modal for an error.
- **One source of truth for state.** Anything shown in two places is read from the Library or the
  PlaybackService and redrawn when it changes, never copied and left to go stale.
- **Covers are the only decoration.** Colour comes from the art itself.
- **Signed-out is first-class.** The product works without an account; signing in adds, never
  gates.
- **Native or nothing.** Fluent 2 and Windows 11 conventions are the construction material;
  Koda's personality shows through its theming surface.

## Installing

**Portable build (today).** Download `Koda-<version>-win-x64-portable.zip`, unpack it anywhere,
and run `Koda.exe`. Nothing is installed, nothing is registered, no runtime setup, no prompts.
The app is self-contained: the Windows App SDK runtime travels inside the zip.

**Windows package (the native install).** The repository also builds an MSIX package - the format
Windows installs natively: double-click, App Installer, Start menu entry, automatic updates, and
an uninstall entry in Settings. A package handed to a user must be signed by a certificate the
operating system already trusts:

- sign it with a code-signing certificate from a public CA (the package then installs like any
  store app, with no warnings), or
- publish it to the Microsoft Store, which signs it with Microsoft's own certificate.

Both are release-time decisions. The self-signed build in `scripts/build-release.ps1` exists for
local development and is not distributable - Windows refuses to deploy it on a normal machine.

## Building from source

Requirements:

- Windows 11 (22H2 or newer)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`net10.0-windows`)

The app is unpackaged and self-contained: it runs straight from the build output, with no install
step and no separate runtime setup.

```powershell
dotnet build src\Koda\Koda.csproj -p:Platform=x64
dotnet test tests\Koda.Core.Tests
```

The app executable lands in `src\Koda\bin\x64\Debug\net10.0-windows10.0.26100.0\`.

To build the release artifacts (the portable zip, and the MSIX package when a signing
certificate is given):

```powershell
.\scripts\build-release.ps1
.\scripts\build-release.ps1 -CertificateThumbprint <thumbprint>
```

## Where your data lives

Everything Koda keeps - settings, library, cached audio - goes into your per-user local app data
(`%LOCALAPPDATA%\Koda`).

Set the `KODA_DATA` environment variable to put it somewhere else, for example next to the app
itself for a portable install:

```powershell
$env:KODA_DATA = 'D:\KodaData'
```

## Repository layout

| Path | What it is |
|---|---|
| `src/Koda` | The WinUI 3 app: shell, pages, playback, system integration. |
| `src/Koda.Core` | The engine: InnerTube client, parsers, playback transport, library. |
| `tests/Koda.Core.Tests` | Offline tests, driven by recorded responses in `Fixtures`. |
| `docs` | Windows implementation notes, parsing discipline, porting audit. |

The engine speaks to YouTube's InnerTube endpoints directly. Responses are recorded into
fixtures and parsers are written against those, never from memory - see
[docs/parsing.md](docs/parsing.md).

## License

GPL-3.0, like the rest of the family. See [LICENSE](LICENSE).

Koda is an independent project and is not affiliated with Google or YouTube.
