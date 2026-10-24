# Changelog

What changed in each release of Koda for Windows, newest first.

## 0.2.0 - 2026-10-24

Windows catches up with the Mac: downloads, Last.fm, crossfade, Discord, and a player that keeps
your place.

### New

- **Downloads.** Download any song from its right-click menu, or a whole album or playlist from
  the Download button on its page. Two download at a time, an unfinished download carries on at
  the next launch, and the Downloads page shows the songs, the space they take and what is still
  coming. Downloaded songs play without a connection.
- **Crossfade.** The end of one song overlaps the start of the next, for 1 to 12 seconds, with
  equal-power fades so the two together stay as loud as one. Repeat one crossfades into itself.
  A skip fades the old song out quickly instead of cutting it off. With crossfade off, the next
  song is loaded ahead and starts the moment the last one ends, with no gap.
- **Normalize volume.** Loud masters come down to YouTube's -14 LUFS target, so songs play at a
  similar level. Quiet ones are left alone, never boosted.
- **Last.fm.** A Last.fm page in the sidebar connects your account with Last.fm's own approval
  page (Koda never sees your password) and connects by itself once you approve. A play counts
  once you've heard half the song or four minutes; offline plays wait and are sent in order
  later; videos are not scrobbled. The sign-in is kept in the Windows Credential Manager, never in
  a backup.
- **Discord.** Your Discord profile can show "Listening to" the song, with its cover, its artist
  and a link to it on YouTube Music. Off until you turn it on.
- **Suggest less.** From any song's menu, keep that song, or an artist on it, out of Home, Your
  Mix, radio and autoplay. Settings lists what you hid, each with Suggest again. Songs you pick
  yourself still play.
- **Share lyrics.** Right-click a lyric line (or use More on Now Playing), choose up to six lines
  and a background, and copy or save the card as an image.
- **Sleep timer.** Stop after 5 to 90 minutes, or at the end of the song; a pill on Now Playing
  counts down.
- **Song notifications.** A notification with the song and Previous, Play and Next while Koda is
  minimized or in the tray. Never while you can see the window.
- **Jump List.** Right-click Koda on the taskbar for Play/Pause, Next and Previous.
- **Settings.** New rows for normalize volume, crossfade, Last.fm, Discord, downloads, Suggest
  less and the Koda for Android import. Lyrics text size is now a slider.
- **Energy Saver.** While Windows Energy Saver is on, the moving colours behind Now Playing hold
  still.
- **The shell.** A sidebar with the library's sections, counts, your own playlists and what plays
  next; Mica and Koda's own title bar with search in it; the window opens where you left it.
- **History and Stats pages.** Every play by the day it was played, and your listening at a
  glance; both are kept in a library backup.
- **Mini player.** A small always-on-top player for while you work.
- **Skip back and ahead** buttons beside previous and next, by 5, 10, 15 or 30 seconds, also on
  Ctrl+Alt+Left and Right.
- **The Windows ways in.** Open at login, taskbar progress and thumbnail buttons, `koda://`
  links, the Windows share sheet for a song's link, and a choice of audio output.

### Fixed

- **Songs no longer jump back or start over on their own.** A pause and resume, or Windows
  rebuilding its audio pipeline (minimizing, switching devices), could send a song back to its
  first second; a network hiccup could put it back a few seconds; a seek made while audio was
  being read could be undone. All three are gone.
- **A stream that breaks keeps your place.** A song that loses its connection reloads where you
  were and keeps what was already downloaded, instead of fetching the whole song again from the
  start. A song stuck waiting on the network for 15 seconds is reloaded rather than left spinning.
- **No speakers, no endless spinner.** With no speakers or headphones connected, Koda says so at
  once instead of loading a song that can never play.
- **The position slider** can no longer get stuck after a right-click, and moves the song from the
  keyboard (arrow keys, Home, End).
- **Screen readers** read song rows as "title, artist" instead of the app's internal record text.
- **The sidebar's Up next and listening rows** are ordinary sidebar rows now, lined up with the
  rest, and no longer push the library items out of sight.
- **Home in a narrower window** keeps Your Mix beside Quick picks, with the picks filling the room,
  instead of leaving the right-hand side empty.
- Settings saved by 0.1.0, including the old lyrics size, carry over.

## 0.1.0 - 2026-10-20

The first Koda for Windows.

- Home with shelves, Your Mix, Quick picks, recently played and charts.
- Search by song, artist, album or playlist.
- Album, artist, playlist and mood pages.
- Library: liked songs, your playlists, listening history and stats.
- Queue with shuffle, repeat, Play next, drag to reorder, undo and radio continuation.
- Now Playing with the cover's palette as its background.
- Lyrics word by word, from YouLyPlus, BetterLyrics and LRCLIB.
- Media keys, the Windows media card, a tray transport and close-to-tray playback.
- Import a Koda for Android backup.
