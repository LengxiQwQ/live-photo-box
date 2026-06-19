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

        public static readonly Regex MotionPhotoLengthRegex = new(
            @"Item:Semantic=""MotionPhoto""[^>]*Item:Length=""(?<value>\d+)""|Item:Length=""(?<value>\d+)""[^>]*Item:Semantic=""MotionPhoto""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));
    }
}
