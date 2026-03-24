using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdLog.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
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
    private readonly ILogger<LetterboxdLogController> _logger;

    public LetterboxdLogController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<LetterboxdLogController> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
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

    [HttpGet("Jellyfin.Plugin.LetterboxdLog/GetMovies")]
    public IActionResult GetMovies([FromQuery] string userId)
    {
        if (!Guid.TryParse(userId, out var userGuid)) return BadRequest("Invalid user ID");
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

        var result = movies.Select(m =>
        {
            var userData = _userDataManager.GetUserData(user, m);
            var tags = m.Tags.ToList();
            return new
            {
                Id = m.Id.ToString("N"),
                Name = m.Name,
                Year = m.ProductionYear,
                IsPlayed = userData?.Played ?? false,
                HasIgnore = tags.Contains(".ignore", StringComparer.OrdinalIgnoreCase),
                SkipTag = tags.FirstOrDefault(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase))
            };
        }).OrderBy(m => m.Name);

        return Ok(result);
    }

    [HttpPost("Jellyfin.Plugin.LetterboxdLog/MarkWatchedLocally")]
    public async Task<IActionResult> MarkWatchedLocally([FromBody] MarkWatchedRequest request)
    {
        if (!Guid.TryParse(request.UserId, out var userGuid)) return BadRequest("Invalid user ID");
        var user = _userManager.GetUserById(userGuid);
        if (user == null) return BadRequest("User not found");

        if (!Guid.TryParse(request.MovieId, out var movieGuid)) return BadRequest("Invalid movie ID");
        var movie = _libraryManager.GetItemById(movieGuid) as Movie;
        if (movie == null) return BadRequest("Movie not found");

        // 1. Update Played Status
        var userData = _userDataManager.GetUserData(user, movie);
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

    public class MarkWatchedRequest
    {
        public string UserId { get; set; } = string.Empty;
        public string MovieId { get; set; } = string.Empty;
        public bool Watched { get; set; }
    }
}
