using System;
using System.Text.RegularExpressions;

namespace LivePhotoBox.Models
{
    /// <summary>Shared constants for Live Photo detection / splitting.</summary>
    public static class LivePhotoConstants
    {
        public const int MetadataProbeBytes = 1024 * 1024;

        public static readonly Regex MicroVideoOffsetRegex = new(
            @"GCamera:MicroVideoOffset=""(?<value>\d+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        /// <summary>Max video duration (seconds) for a MOV/MP4 to be considered a Live Photo.
        /// iPhone Live Photos are typically 1–3s; 3.5s adds safety margin (~3.09s observed max).</summary>
        public const double MaxLivePhotoVideoDurationSeconds = 3.5;

        public static readonly Regex MotionPhotoLengthRegex = new(
            @"Item:Semantic=""MotionPhoto""[^>]*Item:Length=""(?<value>\d+)""|Item:Length=""(?<value>\d+)""[^>]*Item:Semantic=""MotionPhoto""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));
    }
}
