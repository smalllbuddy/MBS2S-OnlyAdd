using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicBeePlugin.Models
{
    public class SpotifyPlaylist
    {
        public SimplePlaylist Playlist { get; set; }
        public bool CanSyncToMusicBee { get; set; } = true;
        public string AccessLabel { get; set; } = string.Empty;

        public SpotifyPlaylist(SimplePlaylist pl, bool canSyncToMusicBee = true, string accessLabel = "")
        {
            Playlist = pl;
            CanSyncToMusicBee = canSyncToMusicBee;
            AccessLabel = accessLabel ?? string.Empty;
        }

        public override string ToString()
        {
            string name = Playlist?.Name ?? "Unknown playlist";
            return string.IsNullOrWhiteSpace(AccessLabel) ? name : name + " [" + AccessLabel + "]";
        }
    }
}
