using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace MusicBeePlugin.WPF
{
    public class FullTrackStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var FullTrack = value as SpotifyAPI.Web.FullTrack;

            if (FullTrack == null)
                return "";

            var artistNames = FullTrack.Artists == null
                ? string.Empty
                : String.Join("; ", FullTrack.Artists.Where(a => !string.IsNullOrWhiteSpace(a?.Name)).Select(a => a.Name));

            return (string.IsNullOrWhiteSpace(artistNames) ? "Unknown artist" : artistNames) + " - " + (FullTrack.Name ?? "Unknown title");         
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
