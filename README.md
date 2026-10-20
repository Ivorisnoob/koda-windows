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
design system. [Koda for Android](https://github.com/Ivorisnoob/Koda) and
[Koda for iOS and macOS](https://github.com/Ivorisnoob/iKoda) are the shipped ones; this repository is
the Windows leg - WinUI 3, Fluent, and the Windows shell's own surfaces.

It works without an official YouTube API key, and an account is optional.

> **Status: early.** There is no published release yet. The app runs straight from the build output
> - see [Building from source](#building-from-source). Releases will follow.

## What is here today

Working and in the app:

- **Home** with shelves, Your Mix, Quick picks, recently played and charts.
- **Search** by song, artist, album or playlist, with recent searches.
- **Album, artist, playlist and mood pages**, each with its own hero and track list.
- **Library**: liked songs, your playlists, listening history and stats.
- **Queue** with shuffle, repeat, Play next, drag to reorder, undo and radio continuation.
- **Now Playing**: the cover's palette as the page's own background, art-adaptive accent.
- **Lyrics**: word by word, in the panel, from three services at once (YouLyPlus, BetterLyrics and
  LRCLIB). The line being sung lights and fills as it is sung, held notes swell, backing vocals
  sit under their line, the second voice of a duet takes the right side, and the panel follows the
  song.
- **System integration**: media keys and the Windows media card (SMTC), a tray transport, and
  close-to-tray playback.
- **Android import**: read a Koda for Android backup - liked songs, history, playlists, albums.

On the way: Last.fm, Discord Rich Presence, downloads and Ready Offline, sign-in, toasts, and
`koda://` protocol handling.

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

The release artifacts - the portable zip, the MSI installer, and the MSIX package when a signing
certificate is given - are built with:

```powershell
.\scripts\build-release.ps1
.\scripts\build-release.ps1 -CertificateThumbprint <thumbprint>
```

The MSI is built with the [WiX Toolset](https://wixtoolset.org) (`dotnet tool install --tool-path
<path> wix --version 5.0.2`); the project is `installer/Koda.wxs`. The MSIX package must be signed
by a certificate the operating system already trusts - the self-signed build in
`scripts/build-release.ps1` exists for local development and is not distributable.

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
| `src/Koda` | The WinUI 3 app: shell, pages, playback, lyrics panel, system integration. |
| `src/Koda.Core` | The engine: InnerTube client, parsers, playback transport, library, lyrics. |
| `tests/Koda.Core.Tests` | Offline tests, driven by recorded responses in `Fixtures`. |
| `installer` | The WiX project for the MSI installer. |
| `scripts` | The release build script. |

The engine speaks to YouTube's InnerTube endpoints directly. Responses are recorded into fixtures
and the parsers are written against those, never from memory - so the test suite runs offline and
does not need an account.

## License

GPL-3.0, like the rest of the family. See [LICENSE](LICENSE).

Koda is an independent project and is not affiliated with Google or YouTube.
