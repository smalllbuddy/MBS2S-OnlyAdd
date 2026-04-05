using System.Collections.Generic;
using System.Linq;

namespace MusicBeePlugin.Models
{
    public class SyncFailureDetail
    {
        public string Signature { get; set; }
        public string Description { get; set; }
    }

    public class PlaylistSyncResult
    {
        public string PlaylistName { get; set; }
        public List<string> AddedSongs { get; set; } = new List<string>();
        public List<SyncFailureDetail> FailedSongs { get; set; } = new List<SyncFailureDetail>();
        public int TotalFailures => FailedSongs?.Count ?? 0;
    }

    public class SyncRunReport
    {
        public List<PlaylistSyncResult> Playlists { get; set; } = new List<PlaylistSyncResult>();
        public int TotalAddedSongs => Playlists.Sum(p => p.AddedSongs?.Count ?? 0);
        public int TotalFailedSongs => Playlists.Sum(p => p.TotalFailures);
    }
}
