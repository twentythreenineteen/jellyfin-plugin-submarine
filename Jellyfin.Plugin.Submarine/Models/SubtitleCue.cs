using System;

namespace Jellyfin.Plugin.Submarine.Models;

/// <summary>
/// Represents a single timed subtitle cue across any subtitle format.
/// </summary>
public sealed class SubtitleCue
{
    /// <summary>
    /// Gets or sets the 1-based sequence index of the cue.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the start timestamp of the cue.
    /// </summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end timestamp of the cue.
    /// </summary>
    public TimeSpan EndTime { get; set; }

    /// <summary>
    /// Gets or sets the subtitle text (may contain multiple lines and basic HTML/override styling tags).
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional speaker name or ASS/SSA style name.
    /// </summary>
    public string? SpeakerOrStyle { get; set; }
}
