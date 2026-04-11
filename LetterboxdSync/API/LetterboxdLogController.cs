using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdLog.API.Models;
using LetterboxdLog.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LetterboxdLog.API;

[ApiController]
[Produces("application/json")]
public class LetterboxdLogController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILogger<LetterboxdLogController> _logger;

    public LetterboxdLogController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IPlaylistManager playlistManager,
        ILogger<LetterboxdLogController> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _playlistManager = playlistManager;
        _logger = logger;
    }

    [HttpPost("Jellyfin.Plugin.LetterboxdLog/Authenticate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Authenticate([FromBody] Account body)
    {
        var api = new LetterboxdApi(body.CookiesUserAgent ?? body.UserAgent ?? string.Empty);
        try
        {
            api.SetRawCookies(body.CookiesRaw ?? body.Cookie);
            await api.Authenticate(body.UserLetterboxd ?? string.Empty, body.PasswordLetterboxd ?? string.Empty).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex)
        {
            return Unauthorized(new { Message = ex.Message });
        }
    }

    [HttpGet("Jellyfin.Plugin.LetterboxdLog/GetPlaylists")]
    public IActionResult GetPlaylists()
    {
        var playlists = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true
        }).Where(p => p is Playlist && p.SourceType != SourceType.Channel).Cast<Playlist>();

        var result = playlists.Select(p => new
        {
            Id = p.Id.ToString("N"),
            Name = p.Name
        }).OrderBy(p => p.Name);

        return Ok(result);
    }

    [HttpGet("Jellyfin.Plugin.LetterboxdLog/GetMovies")]
    public IActionResult GetMovies([FromQuery] string userId, [FromQuery] string? playlistId = null)
    {
        if (!Guid.TryParse(userId, out var userGuid))
        {
            return BadRequest("Invalid user ID");
        }

        var user = _userManager.GetUserById(userGuid);
        if (user == null)
        {
            return BadRequest("User not found");
        }

        var movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true
        });

        // Build selected playlist membership set
        HashSet<Guid> playlistItems = new();
        if (!string.IsNullOrEmpty(playlistId) && Guid.TryParse(playlistId, out var playlistGuid))
        {
            var playlist = _libraryManager.GetItemById(playlistGuid) as Playlist;
            if (playlist != null)
            {
                var children = playlist.GetItemList(new InternalItemsQuery(user)
                {
                    Recursive = false,
                    DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(true)
                });
                foreach (var child in children)
                {
                    playlistItems.Add(child.Id);
                }
            }
        }

        // Build all-playlists lookup: movieId -> list of playlist names
        var allPlaylists = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true
        }).Where(p => p is Playlist && p.SourceType != SourceType.Channel).Cast<Playlist>().ToList();

        var moviePlaylistMap = new Dictionary<Guid, List<string>>();
        foreach (var pl in allPlaylists)
        {
            var children = pl.GetItemList(new InternalItemsQuery(user)
            {
                Recursive = false,
                DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(true)
            });
            foreach (var child in children)
            {
                if (!moviePlaylistMap.TryGetValue(child.Id, out var names))
                {
                    names = new List<string>();
                    moviePlaylistMap[child.Id] = names;
                }

                names.Add(pl.Name);
            }
        }

        var result = movies.Select(m =>
        {
            var userData = _userDataManager.GetUserData(user, m);
            var tags = m.Tags.ToList();
            moviePlaylistMap.TryGetValue(m.Id, out var plNames);
            return new
            {
                Id = m.Id.ToString("N"),
                Name = m.Name,
                Year = m.ProductionYear,
                IsPlayed = userData?.Played ?? false,
                HasIgnore = tags.Contains(".ignore", StringComparer.OrdinalIgnoreCase),
                SkipTag = tags.FirstOrDefault(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase)),
                IsInPlaylist = playlistItems.Contains(m.Id),
                AllPlaylists = plNames ?? new List<string>()
            };
        }).OrderBy(m => m.Name);

        return Ok(result);
    }

    [HttpPost("Jellyfin.Plugin.LetterboxdLog/TogglePlaylist")]
    public async Task<IActionResult> TogglePlaylist([FromBody] PlaylistRequest request)
    {
        if (!Guid.TryParse(request.UserId, out var userGuid))
        {
            return BadRequest("Invalid user ID");
        }

        var user = _userManager.GetUserById(userGuid);
        if (user == null)
        {
            return BadRequest("User not found");
        }

        if (!Guid.TryParse(request.PlaylistId, out var playlistGuid))
        {
            return BadRequest("Invalid playlist ID");
        }

        var playlist = _libraryManager.GetItemById(playlistGuid) as Playlist;
        if (playlist == null)
        {
            return BadRequest("Playlist not found");
        }

        if (!Guid.TryParse(request.MovieId, out var movieGuid))
        {
            return BadRequest("Invalid movie ID");
        }

        if (request.InPlaylist)
        {
            await _playlistManager.AddItemToPlaylistAsync(playlistGuid, new[] { movieGuid }, userGuid).ConfigureAwait(false);
        }
        else
        {
            // For removal, we try to find the movie in the playlist to get its children if needed,
            // but many versions support removing by ItemId string if it's a simple playlist.
            await _playlistManager.RemoveItemFromPlaylistAsync(playlistGuid.ToString(), new[] { request.MovieId }).ConfigureAwait(false);
        }

        return Ok();
    }

    [HttpPost("Jellyfin.Plugin.LetterboxdLog/MarkWatchedLocally")]
    public async Task<IActionResult> MarkWatchedLocally([FromBody] MarkWatchedRequest request)
    {
        if (!Guid.TryParse(request.UserId, out var userGuid))
        {
            return BadRequest("Invalid user ID");
        }

        var user = _userManager.GetUserById(userGuid);
        if (user == null)
        {
            return BadRequest("User not found");
        }

        if (!Guid.TryParse(request.MovieId, out var movieGuid))
        {
            return BadRequest("Invalid movie ID");
        }

        var movie = _libraryManager.GetItemById(movieGuid) as Movie;
        if (movie == null)
        {
            return BadRequest("Movie not found");
        }

        // 1. Update Played Status
        var userData = _userDataManager.GetUserData(user, movie);
        if (userData == null)
        {
            userData = new MediaBrowser.Controller.Entities.UserItemData
            {
                Key = movie.Id.ToString()
            };
        }

        userData.Played = request.Watched;
        if (request.Watched && !userData.LastPlayedDate.HasValue)
        {
            userData.LastPlayedDate = DateTime.UtcNow;
        }

        _userDataManager.SaveUserData(user, movie, userData, UserDataSaveReason.TogglePlayed, default);

        // 2. Update Tags (.ignore and LetterboxdSkip)
        var tags = movie.Tags.ToList();
        if (request.Watched)
        {
            if (!tags.Contains(".ignore", StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(".ignore");
            }

            var todayTag = $"LetterboxdSkip:{DateTime.Today:yyyy-MM-dd}";
            tags.RemoveAll(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase));
            tags.Add(todayTag);
        }
        else
        {
            tags.RemoveAll(t => t.Equals(".ignore", StringComparison.OrdinalIgnoreCase));
            tags.RemoveAll(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase));
        }

        movie.Tags = tags.ToArray();
        await movie.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, default).ConfigureAwait(false);

        return Ok();
    }

    [HttpGet("Jellyfin.Plugin.LetterboxdLog/GetHistory")]
    public IActionResult GetHistory([FromQuery] string userId)
    {
        if (!Guid.TryParse(userId, out var userGuid))
        {
            return BadRequest("Invalid user ID");
        }

        var user = _userManager.GetUserById(userGuid);
        if (user == null)
        {
            return BadRequest("User not found");
        }

        var configDir = System.IO.Path.GetDirectoryName(Plugin.Instance!.ConfigurationFilePath) ?? string.Empty;
        var userIdStr = userGuid.ToString("N");

        var config = Plugin.Instance!.Configuration;
        var account = config.Accounts.FirstOrDefault(a => a.UserJellyfin == userIdStr);
        var offset = account?.TimezoneOffset ?? 0;

        // Pre-fetch all played movies once into a dictionary for fast lookups
        var allMovies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            IsPlayed = true,
            Recursive = true
        });
        var movieMap = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in allMovies)
        {
            movieMap[m.Id.ToString("N")] = m;
        }

        // Dedup key: movieId+date (date portion only for dedup)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HistoryResult>();

        void AddResult(HistoryResult r)
        {
            if (string.IsNullOrEmpty(r.DateLogged))
            {
                return;
            }

            string? datePart;
            if (r.DateLogged.Contains('T', StringComparison.Ordinal))
            {
                if (DateTime.TryParse(r.DateLogged, out var dt))
                {
                    // If it has a 'Z' or was parsed as UTC, we adjust it to the user's offset
                    // to match the date-only format used in the cache.
                    if (r.DateLogged.EndsWith('Z') || dt.Kind == DateTimeKind.Utc)
                    {
                        dt = dt.AddHours(offset);
                    }

                    datePart = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                else
                {
                    datePart = r.DateLogged.Substring(0, 10);
                }
            }
            else
            {
                datePart = r.DateLogged;
            }

            var key = $"{r.Id}:{datePart}";
            if (seen.Add(key))
            {
                result.Add(r);
            }
        }

        HistoryResult? MovieToResult(string movieId, string? dateLogged, string? fallbackName, int? fallbackYear, string? fallbackTmdbId, bool rewatch = false)
        {
            movieMap.TryGetValue(movieId, out var movie);
            var tmdbId = movie?.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb) ?? fallbackTmdbId;
            return new HistoryResult
            {
                Id = movieId,
                Name = movie?.Name ?? fallbackName ?? "Unknown",
                Year = movie?.ProductionYear ?? fallbackYear,
                DateLogged = dateLogged,
                Rewatch = rewatch,
                LetterboxdUrl = !string.IsNullOrEmpty(tmdbId)
                    ? $"https://letterboxd.com/tmdb/{tmdbId}/"
                    : null
            };
        }

        // Source 1: Persistent history log (written by sync task on new syncs)
        var historyPath = System.IO.Path.Combine(configDir, "LetterboxdLog_History.json");
        if (System.IO.File.Exists(historyPath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(historyPath);
                var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
                if (entries != null)
                {
                    foreach (var entry in entries.Where(e => string.Equals(e.UserId, userIdStr, StringComparison.OrdinalIgnoreCase)))
                    {
                        var r = MovieToResult(entry.MovieId ?? string.Empty, entry.DateLogged, entry.Name, entry.Year, entry.TmdbId, entry.Rewatch ?? false);
                        if (r != null)
                        {
                            AddResult(r);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading history file");
            }
        }

        // Source 2: Sync cache — keys are "userId:movieId:dateString"
        var cachePath = System.IO.Path.Combine(configDir, "LetterboxdLog_SyncCache.json");
        if (System.IO.File.Exists(cachePath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                if (cache != null)
                {
                    foreach (var kvp in cache)
                    {
                        var parts = kvp.Key.Split(':');
                        if (parts.Length < 3)
                        {
                            continue;
                        }

                        if (!string.Equals(parts[0], userIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var movieId = parts[1];
                        var dateLogged = parts[2];

                        if (movieMap.ContainsKey(movieId))
                        {
                            var r = MovieToResult(movieId, dateLogged, null, null, null);
                            if (r != null)
                            {
                                AddResult(r);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading sync cache for history");
            }
        }

        // Source 3: Tag-based detection — movies with LetterboxdSkip tag but no .ignore
        foreach (var m in allMovies)
        {
            var movieIdStr = m.Id.ToString("N");
            var tags = m.Tags.ToList();
            bool hasIgnore = tags.Contains(".ignore", StringComparer.OrdinalIgnoreCase);
            var skipTag = tags.FirstOrDefault(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase));

            if (skipTag != null && !hasIgnore)
            {
                var datePart = skipTag.Split(':').Length > 1 ? skipTag.Split(':')[1] : null;
                var r = MovieToResult(movieIdStr, datePart, null, null, null);
                if (r != null)
                {
                    AddResult(r);
                }
            }
        }

        // Sort by date descending (most recent first)
        var sorted = result
            .OrderByDescending(r => r.DateLogged ?? string.Empty)
            .ToList();

        return Ok(sorted);
    }

    private sealed class HistoryEntry
    {
        public string? UserId { get; set; }

        public string? MovieId { get; set; }

        public string? Name { get; set; }

        public int? Year { get; set; }

        public string? DateLogged { get; set; }

        public string? TmdbId { get; set; }

        public bool? Rewatch { get; set; }
    }

    private sealed class HistoryResult
    {
        public string Id { get; set; } = string.Empty;

        public string? Name { get; set; }

        public int? Year { get; set; }

        public string? DateLogged { get; set; }

        public string? LetterboxdUrl { get; set; }

        public bool Rewatch { get; set; }
    }
}
