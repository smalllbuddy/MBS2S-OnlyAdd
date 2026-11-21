MusicBee Sync To Service - Add Only
======================================

A [MusicBee](https://getmusicbee.com) plugin that syncs your MusicBee playlists to Spotify.
This version only **adds** songs: it never removes anything from your Spotify playlists.

It's a fork of [MusicBeeSyncToService](https://github.com/mitchhymel/MusicBeeSyncToService)
by mitchhymel, which grew out of leoedin's MBGmusicSync.

Changes from the original
-------------------------
- Doesn't remove songs, only adds them.
- More robust song searching on Spotify.
- Easier-to-read console log and a few quality-of-life changes.

Building
--------
1. Create an app at https://developer.spotify.com/dashboard and add this Redirect URI to it:
   `http://127.0.0.1:5000/callback/`
2. Copy `MusicBeeSyncToService/Services/SpotifySecrets.cs.example` to `SpotifySecrets.cs` in the
   same folder, then fill in your app's Client ID and Client secret. `SpotifySecrets.cs` is
   git-ignored, so your secret never gets uploaded.
3. Open `MusicBeeSyncToService.sln` in Visual Studio and build the `Release` configuration.

Installing
----------
Copy `mb_MusicBeeSyncToService.dll` and the other DLLs from the build output folder into
MusicBee's `Plugins` folder, then restart MusicBee. Open the plugin from
**Tools → Music Bee Sync To Service**.
