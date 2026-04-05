using MusicBeePlugin.Models;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using SpotifyAPI.Web.Http;
using System;
using System.Net;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Documents;
using static Swan.Terminal;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace MusicBeePlugin.Services
{
    public class SpotifySyncHelper
    {
        private PrivateUser Profile;
        private SpotifyClient Spotify;
        private Action<string> Log;
                private MusicBeeSyncHelper musicBeeSyncHelper;
        private readonly string failureLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBeeSyncToService", "LoggedFailures.txt");
        private const string FailureLogSeparator = "\t|\t";

        public List<SimplePlaylist> Playlists { get; set; } = new List<SimplePlaylist>();
        public SyncRunReport LastRunReport { get; private set; } = new SyncRunReport();

        public string GetFailureLogPath()
        {
            return failureLogPath;
        }

        private Dictionary<string, string> LoadLoggedFailures()
        {
            try
            {
                string directory = Path.GetDirectoryName(failureLogPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (!File.Exists(failureLogPath))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rawLine in File.ReadAllLines(failureLogPath))
                {
                    var line = rawLine?.Trim();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string signature = line;
                    string description = line;
                    int separatorIndex = line.IndexOf(FailureLogSeparator, StringComparison.Ordinal);
                    if (separatorIndex >= 0)
                    {
                        signature = line.Substring(0, separatorIndex).Trim();
                        description = line.Substring(separatorIndex + FailureLogSeparator.Length).Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(signature) && !results.ContainsKey(signature))
                    {
                        results[signature] = string.IsNullOrWhiteSpace(description) ? signature : description;
                    }
                }

                return results;
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveLoggedFailures(Dictionary<string, string> failures)
        {
            try
            {
                string directory = Path.GetDirectoryName(failureLogPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var lines = failures
                    .OrderBy(kvp => kvp.Value ?? kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kvp => $"{kvp.Key}{FailureLogSeparator}{kvp.Value ?? kvp.Key}")
                    .ToArray();

                File.WriteAllLines(failureLogPath, lines);
            }
            catch
            {
            }
        }

        private string GetSongDisplayName(MusicBeeSong mbSong, FullTrack spotifySong)
        {
            if (spotifySong != null)
            {
                return $"{String.Join("; ", spotifySong.Artists.Select(a => a.Name))} - {spotifySong.Name}";
            }

            if (mbSong != null)
            {
                return $"{mbSong.Artist} - {mbSong.Title}";
            }

            return "Unknown song";
        }

        private void ResetLastRunReport()
        {
            LastRunReport = new SyncRunReport();
        }

        public SpotifySyncHelper(Action<string> log, MusicBeeSyncHelper mb)
        {
            if (mb == null)
            {
                throw new ArgumentNullException(nameof(mb), "MusicBeeSyncHelper cannot be null");
            }

            Log = log;
            musicBeeSyncHelper = mb;
        }

        public async Task<bool> LoginAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            await LoginToSpotify(tcs);
            return await tcs.Task;
        }

        private async Task LoginToSpotify(TaskCompletionSource<bool> tcs)
        {
            string redirectUri = SpotifySecrets.REDIRECT_URI;
            string state = Guid.NewGuid().ToString("N");
            string scopes = "user-library-read playlist-modify-public";

            string authUrl = $"https://accounts.spotify.com/authorize" +
                             $"?client_id={SpotifySecrets.CLIENT_ID}" +
                             $"&response_type=code" +
                             $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                             $"&scope={Uri.EscapeDataString(scopes)}" +
                             $"&state={state}";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            });

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add("http://127.0.0.1:5000/callback/");
                listener.Start();
                Log("Waiting for Spotify authorization callback...");

                var context = await listener.GetContextAsync();
                var request = context.Request;
                var response = context.Response;

                string code = request.QueryString["code"];
                string error = request.QueryString["error"];

                string htmlResponse;

                if (!string.IsNullOrEmpty(error))
                {
                    htmlResponse = "<html><body><h1>Authorization failed: " + error + "</h1></body></html>";
                    tcs.SetResult(false);
                }
                else if (!string.IsNullOrEmpty(code))
                {
                    htmlResponse = @"<html><body style=""font-family:Segoe UI,Arial,sans-serif;padding:24px;""><h1>Authorization complete.</h1><p>You can close this window if it does not close automatically.</p><script>setTimeout(function(){window.open('','_self');window.close();}, 300);</script></body></html>";

                    // Send POST to get access token
                    var config = SpotifyClientConfig.CreateDefault();

                    var tokenRequest = new AuthorizationCodeTokenRequest(
                        SpotifySecrets.CLIENT_ID,
                        SpotifySecrets.CLIENT_SECRET,
                        code,
                        new Uri(redirectUri)
                    );

                    var tokenResponse = await new OAuthClient(config).RequestToken(tokenRequest);

                    Spotify = new SpotifyClient(tokenResponse.AccessToken);
                    Profile = await Spotify.UserProfile.Current();
                    tcs.SetResult(Spotify != null);
                }
                else
                {
                    htmlResponse = "<html><body><h1>No code received.</h1></body></html>";
                    tcs.SetResult(false);
                }

                byte[] buffer = Encoding.UTF8.GetBytes(htmlResponse);
                response.ContentLength64 = buffer.Length;
                response.ContentType = "text/html";
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.OutputStream.Close();
                listener.Stop();
            }
        }

        public bool IsLoggedIn()
        {
            return Spotify != null;
        }

        public async Task<List<SimplePlaylist>> RefreshPlaylists()
        {
            Playlists.Clear();
            Paging<SimplePlaylist> response = await Spotify.Playlists.GetUsers(Profile.Id);
            var all = await Spotify.PaginateAll(response);
            foreach (var item in all)
            {
                Playlists.Add(item);
            }

            return Playlists;
        }

        public async Task SyncToSpotify(List<MusicBeePlaylist> mbPlaylistsToSync,
            SyncToSpotifySettings settings)
        {
            PopulateErrors.Clear();
            ResetLastRunReport();

            foreach (MusicBeePlaylist playlist in mbPlaylistsToSync)
            {
                PopulateMusicBeePlaylist = playlist;
                await PopulateSpotifyPlaylistSongsFromMbPlaylist(false);

                await SaveSpotifyPlaylistPopulatedFromMbPlaylist(settings);
            }
        }

        public async Task SyncToMusicBee(List<SimplePlaylist> playlists)
        {
            PopulateErrors.Clear();
            ResetLastRunReport();

            // Go through each playlist we want to sync in turn
            foreach (SimplePlaylist playlist in playlists)
            {
                PopulateSpotifyPlaylist = playlist;
                await PopulateMbPlaylistSongsFromSpotifyPlaylist(false);

                await SaveMbPlaylistPopulatedFromSpotifyPlaylist();
            }

            // Get the local playlists again
            musicBeeSyncHelper.RefreshMusicBeePlaylists();
        }

        private async Task<List<FullTrack>> GetFullTracksFromSpotifyPlaylist(SimplePlaylist playlist)
        {
            var tracks = new List<FullTrack>();

            // Get all tracks for playlist
            var fp = await Spotify.Playlists.Get(playlist.Id);
            var allTracks = await Spotify.PaginateAll(fp.Tracks);
            
            foreach (var t in allTracks)
            {
                if (t.Track is FullTrack track)
                {
                    tracks.Add(track);
                }
            }

            return tracks;
        }

        private MusicBeeSong FindMusicBeeSongFromSpotifyTrack(FullTrack track)
        {
            MusicBeeSong bestSong = null;
            int bestScore = int.MinValue;

            foreach (MusicBeeSong mbSong in musicBeeSyncHelper.Songs)
            {
                int score = GetSongMatchScore(track, mbSong);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSong = mbSong;
                }
            }

            return bestScore >= 10 ? bestSong : null;
        }

        public List<IPlaylistSyncError> PopulateErrors { get; private set; } = new List<IPlaylistSyncError>();
        public List<PopulatedSong> PopulatedSongs { get; private set; } = new List<PopulatedSong>();
        public SimplePlaylist PopulateSpotifyPlaylist { get; set; } = null;
        public MusicBeePlaylist PopulateMusicBeePlaylist { get; set; } = null;

        public async Task PopulateMbPlaylistSongsFromSpotifyPlaylist(bool clearErrors = true)
        {
            if (PopulateSpotifyPlaylist == null)
            {
                throw new ArgumentNullException(nameof(PopulateSpotifyPlaylist), "PopulateSpotifyPlaylist cannot be null");
            }

            if (clearErrors)
            {
                PopulateErrors.Clear();
                ResetLastRunReport();
            }

            PopulatedSongs.Clear();

            // Get all tracks for playlist
            var tracks = await GetFullTracksFromSpotifyPlaylist(PopulateSpotifyPlaylist);

            PopulatedSongs.AddRange(tracks.Select(t => new PopulatedSong()
            {
                MbSong = null,
                SpotifySong = t
            }));

            foreach (var song in PopulatedSongs)
            {
                var trackToAdd = FindMusicBeeSongFromSpotifyTrack(song.SpotifySong);

                if (trackToAdd != null)
                {
                    song.MbSong = trackToAdd;
                    song.Checked = true;
                }
                else
                {
                    PopulateErrors.Add(new UnableToFindSpotifyTrackError()
                    {
                        AlbumName = song.SpotifySong.Album.Name,
                        ArtistName = song.SpotifySong.Artists.FirstOrDefault().Name,
                        PlaylistName = PopulateSpotifyPlaylist.Name,
                        TrackName = song.SpotifySong.Name,
                        SearchedService = false
                    });
                }
            }
        }
        public async Task SaveMbPlaylistPopulatedFromSpotifyPlaylist() =>
            await Task.Run(() =>
            {
                if (PopulateSpotifyPlaylist == null)
                {
                    throw new ArgumentNullException(nameof(PopulateSpotifyPlaylist));
                }

                var playlistResult = new PlaylistSyncResult()
                {
                    PlaylistName = PopulateSpotifyPlaylist.Name
                };

                Log($"📀 Updating MusicBee playlist: {PopulateSpotifyPlaylist.Name}");

                var matchedPlaylist = musicBeeSyncHelper.Playlists
                    .Select(p => new
                    {
                        Name = p.Name.Split('\\').Last(),
                        FullName = p.Name,
                        Songs = p.Songs
                    })
                    .FirstOrDefault(p => p.Name.Equals(PopulateSpotifyPlaylist.Name, StringComparison.OrdinalIgnoreCase));

                bool playlistExists = matchedPlaylist != null;
                string targetPlaylistName = playlistExists ? matchedPlaylist.FullName : PopulateSpotifyPlaylist.Name;

                string[] currentFiles = playlistExists
                    ? matchedPlaylist.Songs
                        .Select(s => s.Filename)
                        .Where(f => !string.IsNullOrWhiteSpace(f))
                        .ToArray()
                    : Array.Empty<string>();

                HashSet<string> existingFiles = new HashSet<string>(currentFiles, StringComparer.OrdinalIgnoreCase);
                var songsToAdd = PopulatedSongs
                    .Where(s => s.Checked && s.MbSong != null && !string.IsNullOrWhiteSpace(s.MbSong.Filename) && !existingFiles.Contains(s.MbSong.Filename))
                    .ToList();

                playlistResult.AddedSongs.AddRange(songsToAdd.Select(s => GetSongDisplayName(s.MbSong, s.SpotifySong)));

                var failedSongs = PopulatedSongs
                    .Where(s => s.SpotifySong != null && (s.MbSong == null || !s.Checked))
                    .ToList();

                foreach (var failed in failedSongs)
                {
                    playlistResult.FailedSongs.Add(new SyncFailureDetail
                    {
                        Signature = $"MB::{targetPlaylistName}::{failed.SpotifySong?.Id ?? failed.SpotifySongDisplayName}",
                        Description = $"{targetPlaylistName}: {GetSongDisplayName(failed.MbSong, failed.SpotifySong)}"
                    });
                }

                string[] newFiles = songsToAdd
                    .Select(s => s.MbSong.Filename)
                    .ToArray();

                string[] mergedFiles = currentFiles.Concat(newFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                Log($"📀 Final merged playlist will have {mergedFiles.Length} tracks");

                if (playlistExists)
                {
                    musicBeeSyncHelper.MbApiInterface.Playlist_DeletePlaylist(matchedPlaylist.FullName);
                }

                string createdPlaylist = musicBeeSyncHelper.TryCreatePlaylist(targetPlaylistName, mergedFiles);
                if (string.IsNullOrWhiteSpace(createdPlaylist))
                {
                    string description = $"{targetPlaylistName}: could not create or update the MusicBee playlist";
                    playlistResult.FailedSongs.Add(new SyncFailureDetail
                    {
                        Signature = $"MBCREATE::{targetPlaylistName}",
                        Description = description
                    });
                    Log($"❌ {description}");
                }

                LastRunReport.Playlists.Add(playlistResult);
            });

        public async Task PopulateSpotifyPlaylistSongsFromMbPlaylist(bool clearErrors = true)
        {
            if (PopulateMusicBeePlaylist == null)
            {
                throw new ArgumentNullException(nameof(PopulateMusicBeePlaylist), "PopulateMusicBeePlaylist cannot be null");
            }

            if (clearErrors)
            {
                PopulateErrors.Clear();
                ResetLastRunReport();
            }
            
            PopulatedSongs.Clear();

            PopulatedSongs.AddRange(PopulateMusicBeePlaylist.Songs.Select(s => new PopulatedSong()
            {
                MbSong = s,
                SpotifySong = null
            }));

            foreach (var song in PopulatedSongs)
            {
                string title = song.MbSong.Title;
                string artist = song.MbSong.Artist;
                string album = song.MbSong.Album;

                string artistEsc = EscapeChar(artist.ToLower());
                string titleEsc = EscapeChar(title.ToLower());
                string searchStr = $"artist:{artistEsc} track:{titleEsc}";
                var request = new SearchRequest(SearchRequest.Types.Track, searchStr);
                SearchResponse search = await Spotify.Search.Item(request);

                if (search.Tracks == null || search.Tracks.Items == null)
                {
                    Log($"Could not find track on Spotify '{searchStr}' for '{title}' by '{artist}'");
                    continue;
                }

                if (search.Tracks.Items.Count == 0)
                {
                    Log($"Found 0 results on Spotify for: {searchStr} for '{title}' by '{artist}'");
                    continue;
                }

                FullTrack trackToAdd = null;
                int bestScore = int.MinValue;
                foreach (FullTrack track in search.Tracks.Items)
                {
                    int score = GetSongMatchScore(track, song.MbSong);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        trackToAdd = track;
                    }
                }

                if (trackToAdd == null || bestScore < 10)
                {
                    Log($"No confident match found for '{title}' by '{artist}' on Spotify.");
                    song.SpotifySong = null;
                    song.Checked = false;
                    continue;
                }

                if (bestScore < 16)
                {
                    Log($"Using closest Spotify match for '{title}' by '{artist}': '{trackToAdd?.Name}' by '{trackToAdd?.Artists.FirstOrDefault()?.Name}' (score {bestScore})");
                }

                song.SpotifySong = trackToAdd;
                song.Checked = true;
            }
        }

                public async Task SaveSpotifyPlaylistPopulatedFromMbPlaylist(SyncToSpotifySettings settings)
        {
            if (PopulateMusicBeePlaylist == null)
            {
                throw new ArgumentNullException(nameof(PopulateMusicBeePlaylist), "PopulateMusicBeePlaylist cannot be null");
            }

            string spotifyPlaylistName = null;
            if (settings.IncludeFoldersInPlaylistName)
            {
                spotifyPlaylistName = PopulateMusicBeePlaylist.Name;
            }
            else
            {
                spotifyPlaylistName = PopulateMusicBeePlaylist.Name.Split('\\').Last();
            }

            if (settings.IncludeZAtStartOfDatePlaylistName)
            {
                if (spotifyPlaylistName.StartsWith("2"))
                {
                    spotifyPlaylistName = $"Z {spotifyPlaylistName}";
                }
            }

            var playlistResult = new PlaylistSyncResult()
            {
                PlaylistName = spotifyPlaylistName
            };

            SimplePlaylist thisPlaylist = Playlists.FirstOrDefault(p => p.Name == spotifyPlaylistName);
            string thisPlaylistId;

            if (thisPlaylist != null)
            {
                thisPlaylistId = thisPlaylist.Id;
            }
            else
            {
                var createRequest = new PlaylistCreateRequest(spotifyPlaylistName);
                FullPlaylist newPlaylist = await Spotify.Playlists.Create(Profile.Id, createRequest);
                thisPlaylistId = newPlaylist.Id;

                thisPlaylist = new SimplePlaylist
                {
                    Id = newPlaylist.Id,
                    Name = newPlaylist.Name
                };

                Playlists.Add(thisPlaylist);
            }

            List<FullTrack> existingTracks = await GetFullTracksFromSpotifyPlaylist(thisPlaylist);
            HashSet<string> existingUris = new HashSet<string>(existingTracks.Select(t => t.Uri));

            var songsToAdd = PopulatedSongs
                .Where(w => w.Checked && w.SpotifySong != null && !existingUris.Contains(w.SpotifySong.Uri))
                .ToList();

            playlistResult.AddedSongs.AddRange(songsToAdd.Select(s => GetSongDisplayName(s.MbSong, s.SpotifySong)));

            Log($"🔎 Found {existingUris.Count} songs already in the Spotify playlist.");
            Log($"📥 Will add {songsToAdd.Count} new songs to Spotify.");

            var missing = PopulatedSongs
                .Where(w => w.Checked && w.SpotifySong == null)
                .ToList();

            foreach (var missingSong in missing)
            {
                playlistResult.FailedSongs.Add(new SyncFailureDetail
                {
                    Signature = $"SP::{spotifyPlaylistName}::{missingSong.MbSong?.Filename ?? missingSong.MbSongDisplayName}",
                    Description = $"{spotifyPlaylistName}: {GetSongDisplayName(missingSong.MbSong, missingSong.SpotifySong)}"
                });
            }

            if (missing.Count > 0)
            {
                Log($"Skipping {missing.Count} MusicBee-only songs that couldn't be found on Spotify.");
            }

            if (songsToAdd.Count == 0)
            {
                Log("✅ All songs already exist in the Spotify playlist. Nothing to add.");
                LastRunReport.Playlists.Add(playlistResult);
                return;
            }

            var uris = songsToAdd.Select(s => s.SpotifySong.Uri).ToList();
            while (uris.Count > 0)
            {
                List<string> currUris = uris.Take(75).ToList();
                if (currUris.Count == 0)
                {
                    break;
                }

                uris.RemoveRange(0, currUris.Count);
                var request = new PlaylistAddItemsRequest(currUris);
                var resp = await Spotify.Playlists.AddItems(thisPlaylistId, request);
                if (resp == null)
                {
                    Log("Error while trying to update playlist with track uris");
                    return;
                }
            }

            LastRunReport.Playlists.Add(playlistResult);
        }

        public void LogCompletionReport()
        {
            var report = LastRunReport ?? new SyncRunReport();
            var loggedFailures = LoadLoggedFailures();
            bool savedLoggedFailures = false;
            int repeatedFailureCountTotal = 0;
            List<string> newFailureLines = new List<string>();
            List<string> addedLines = new List<string>();

            foreach (var playlist in report.Playlists)
            {
                foreach (var song in playlist.AddedSongs)
                {
                    addedLines.Add($"{song} added to playlist {playlist.PlaylistName}");
                }

                foreach (var failure in playlist.FailedSongs)
                {
                    if (string.IsNullOrWhiteSpace(failure?.Signature))
                    {
                        continue;
                    }

                    string description = failure.Description ?? "Unknown song";
                    int separatorIndex = description.IndexOf(": ");
                    string playlistName = separatorIndex >= 0 ? description.Substring(0, separatorIndex) : playlist.PlaylistName;
                    string songName = separatorIndex >= 0 ? description.Substring(separatorIndex + 2) : description;
                    string formattedLine = $"{songName} failed to add to playlist {playlistName}";

                    if (!loggedFailures.ContainsKey(failure.Signature))
                    {
                        savedLoggedFailures = true;
                        loggedFailures[failure.Signature] = formattedLine;
                        newFailureLines.Add(formattedLine);
                    }
                    else
                    {
                        repeatedFailureCountTotal++;
                        string previousLine = loggedFailures[failure.Signature];
                        if (string.IsNullOrWhiteSpace(previousLine) || StringComparer.OrdinalIgnoreCase.Equals(previousLine, failure.Signature))
                        {
                            loggedFailures[failure.Signature] = formattedLine;
                            savedLoggedFailures = true;
                            previousLine = formattedLine;
                        }
                    }
                }
            }

            Log("Complete");
            Log("===========================================");

            if (repeatedFailureCountTotal > 0)
            {
                Log($"{repeatedFailureCountTotal} Previously logged failures not added");
                Log(string.Empty);
            }

            if (!addedLines.Any() && !newFailureLines.Any())
            {
                Log("No new playlist changes.");
            }
            else
            {
                foreach (var line in addedLines)
                {
                    Log(line);
                }

                if (addedLines.Any() && newFailureLines.Any())
                {
                    Log(string.Empty);
                }

                foreach (var line in newFailureLines)
                {
                    Log(line);
                }
            }

            if (savedLoggedFailures)
            {
                SaveLoggedFailures(loggedFailures);
            }
        }

        private int GetSongMatchScore(FullTrack track, MusicBeeSong mbSong)
        {
            if (track == null || mbSong == null)
            {
                return int.MinValue;
            }

            string trackTitle = track.Name ?? string.Empty;
            string mbTitle = mbSong.Title ?? string.Empty;
            string trackAlbum = track.Album?.Name ?? string.Empty;
            string mbAlbum = mbSong.Album ?? string.Empty;
            var trackArtists = track.Artists?.Select(a => a?.Name ?? string.Empty).Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? new List<string>();
            string mbArtist = mbSong.Artist ?? string.Empty;

            int score = 0;

            if (NormalizedExactMatch(trackTitle, mbTitle))
            {
                score += 10;
            }
            else if (FlexibleStringMatch(trackTitle, mbTitle))
            {
                score += 6;
            }
            else
            {
                return int.MinValue;
            }

            if (trackArtists.Any(a => NormalizedExactMatch(a, mbArtist)))
            {
                score += 8;
            }
            else if (trackArtists.Any(a => FlexibleStringMatch(a, mbArtist)))
            {
                score += 5;
            }
            else
            {
                return int.MinValue;
            }

            if (string.IsNullOrWhiteSpace(trackAlbum) || string.IsNullOrWhiteSpace(mbAlbum))
            {
                score += 1;
            }
            else if (NormalizedExactMatch(trackAlbum, mbAlbum))
            {
                score += 4;
            }
            else if (FlexibleStringMatch(trackAlbum, mbAlbum))
            {
                score += 2;
            }

            return score;
        }

        private bool NormalizedExactMatch(string a, string b)
        {
            var x = NormalizeForComparison(a);
            var y = NormalizeForComparison(b);
            return !string.IsNullOrWhiteSpace(x) && !string.IsNullOrWhiteSpace(y) && x == y;
        }

        private string NormalizeForComparison(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            string value = input
                .Replace("’", "")
                .Replace("‘", "")
                .Replace("'", "")
                .Replace("“", "")
                .Replace("”", "")
                .Replace("&", " and ");

            value = Regex.Replace(value, @"\((.*?)\)", " ");
            value = Regex.Replace(value, @"\[(.*?)\]", " ");
            value = Regex.Replace(value, @"\b(feat|featuring|ft)\b.*$", " ", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\b(remaster(ed)?|mono|stereo|deluxe|explicit|clean|version|edit|mix)\b", " ", RegexOptions.IgnoreCase);

            string normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(' ');
                }
            }

            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }

        private string EscapeChar(string input)
        {
            int openIndex = input.IndexOf("(f");
            int closeIndex = input.IndexOf(")");
            if (openIndex >= 0 && closeIndex > 0 && openIndex < closeIndex)
            {
                input = input.Remove(openIndex, closeIndex-openIndex);
            }

            return input.Replace(",", " ")
                .Replace(".", " ")
                .Replace("!", " ")
                .Replace("?", " ")
                .Replace("'",  "")
                .Replace(")", " ")
                .Replace("(", " ")
                .Replace("#", " ")
                .Replace("\\"," ")
                .Replace("/", " ")
                .Replace("&", " ")
                .Replace(":", " ")
                .Replace(";", " ")
                .Replace(" ", " ");
        }

        // artist, title, albums sometimes contain something like "featuring ..." which can cause a mismatch between services
        // so we'll be more flexible in what we consider a match
        private bool FlexibleStringMatch(string a, string b)
        {
            var x = NormalizeForComparison(a);
            var y = NormalizeForComparison(b);

            if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y))
            {
                return false;
            }

            return x == y
                || x.Contains(y)
                || y.Contains(x);
        }

        public async Task<IEnumerable<FullTrack>> FindSongsInSpotifyDatabase(string artist, string title)
        {
            string artistEsc = EscapeChar(artist.ToLower());
            string titleEsc = EscapeChar(title.ToLower());
            string searchStr = $"artist:{artistEsc} track:{titleEsc}";
            var request = new SearchRequest(SearchRequest.Types.Track, searchStr);
            
            SearchResponse search = await Spotify.Search.Item(request);

            if (search.Tracks == null || search.Tracks.Items == null)
                return new List<FullTrack>(0);

            return search.Tracks.Items.ToList();
        }
    }
}
