namespace LetterboxdLog.API.Models;

/// <summary>
/// Model for Mark Watched request.
/// </summary>
public class MarkWatchedRequest
{
    /// <summary>
    /// Gets or sets the user identifier.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the movie identifier.
    /// </summary>
    public string MovieId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the movie is watched.
    /// </summary>
    public bool Watched { get; set; }
}
