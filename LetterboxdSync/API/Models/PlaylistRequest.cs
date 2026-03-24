namespace LetterboxdLog.API.Models;

/// <summary>
/// Model for Playlist request.
/// </summary>
public class PlaylistRequest
{
    /// <summary>
    /// Gets or sets the user identifier.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the playlist identifier.
    /// </summary>
    public string PlaylistId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the movie identifier.
    /// </summary>
    public string MovieId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the movie is in the playlist.
    /// </summary>
    public bool InPlaylist { get; set; }
}
