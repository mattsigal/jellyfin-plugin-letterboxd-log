using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdLog.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdLog;

/// <summary>
/// Listens for playback-stopped events and syncs the film to Letterboxd in real time.
/// </summary>
public sealed class PlaybackHandler : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions HistorySerializerOptions = new() { WriteIndented = true };

    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaybackHandler> _logger;

    public PlaybackHandler(
        ISessionManager sessionManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        ILogger<PlaybackHandler> logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    private string HistoryPath => Path.Combine(
        Path.GetDirectoryName(Plugin.Instance!.ConfigurationFilePath) ?? string.Empty,
        "LetterboxdLog_History.json");

    private string CachePath => Path.Combine(
        Path.GetDirectoryName(Plugin.Instance!.ConfigurationFilePath) ?? string.Empty,
        "LetterboxdLog_SyncCache.json");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("LetterboxdLog real-time sync handler registered");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            await HandlePlaybackStoppedAsync(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in LetterboxdLog real-time sync handler");
        }
    }

    private async Task HandlePlaybackStoppedAsync(PlaybackStopEventArgs e)
    {
        // Only handle movies
        if (e.Item == null || e.Item.GetBaseItemKind() != BaseItemKind.Movie)
        {
            return;
        }

        // Must have actually played (not just opened and closed)
        if (!e.PlayedToCompletion)
        {
            return;
        }

        var userId = e.Session?.UserId ?? Guid.Empty;
        if (userId == Guid.Empty)
        {
            return;
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return;
        }

        var account = Configuration.Accounts.FirstOrDefault(a =>
            a.UserJellyfin == userId.ToString("N") && a.Enable);
        if (account == null)
        {
            return;
        }

        var movie = e.Item;
        string title = movie.OriginalTitle ?? movie.Name ?? "Unknown Title";

        // Check for .ignore tag — skip if present (user explicitly excluded this movie)
        var movieTags = movie.Tags.ToList();
        if (movieTags.Contains(".ignore", StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Real-time sync: Skipping {Movie} due to .ignore tag", title);
            return;
        }

        // Compute viewing date with timezone offset
        var viewingDate = DateTime.UtcNow;
        var adjustedViewingDate = viewingDate.AddHours(account.TimezoneOffset);
        var viewingDateOnly = new DateTime(adjustedViewingDate.Year, adjustedViewingDate.Month, adjustedViewingDate.Day);

        // Check sync cache
        string cacheKey = $"{userId:N}:{movie.Id:N}:{viewingDateOnly:yyyy-MM-dd}";
        var syncCache = LoadSyncCache();
        if (syncCache.ContainsKey(cacheKey))
        {
            _logger.LogDebug("Real-time sync: Cache hit for {Movie} ({Date}), skipping", title, viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return;
        }

        _logger.LogInformation("Real-time sync: Processing {Movie} for user {User}", title, user.Username);

        // Determine favorite status
        var userItemData = _userDataManager.GetUserData(user, movie);
        bool favorite = movie.IsFavoriteOrLiked(user, userItemData) && account.SendFavorite;

        // Authenticate with Letterboxd
        var api = new LetterboxdApi(account.CookiesUserAgent ?? account.UserAgent ?? string.Empty);
        try
        {
            api.SetRawCookies(account.CookiesRaw ?? account.Cookie);
            await api.Authenticate(account.UserLetterboxd ?? string.Empty, account.PasswordLetterboxd ?? string.Empty).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Real-time sync: Auth failed for user {User}", user.Username);
            return;
        }

        // Find the film on Letterboxd
        FilmResult? filmResult = null;
        if (int.TryParse(movie.GetProviderId(MetadataProvider.Tmdb), out int tmdbid))
        {
            try
            {
                filmResult = await api.SearchFilmByTmdbId(tmdbid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Real-time sync: TMDB lookup failed for {Movie}: {Msg}", title, ex.Message);
            }
        }

        if (filmResult == null)
        {
            string? imdbid = movie.GetProviderId(MetadataProvider.Imdb);
            if (!string.IsNullOrEmpty(imdbid))
            {
                try
                {
                    filmResult = await api.SearchFilmByImdbId(imdbid).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Real-time sync: IMDb lookup failed for {Movie}: {Msg}", title, ex.Message);
                }
            }
        }

        if (filmResult == null)
        {
            _logger.LogWarning("Real-time sync: Could not find {Movie} on Letterboxd", title);
            return;
        }

        try
        {
            // Check for existing diary entry (rewatch detection)
            var dateLastLog = await api.GetDateLastLog(filmResult.FilmSlug).ConfigureAwait(false);
            bool isRewatch = dateLastLog.HasValue;

            if (dateLastLog != null && dateLastLog.Value.Date == viewingDateOnly.Date)
            {
                // Already logged today — cache and skip
                syncCache[cacheKey] = DateTime.UtcNow;
                SaveSyncCache(syncCache);

                _logger.LogInformation("Real-time sync: Already logged today ({Date}) — {Movie}", viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), title);
                AppendHistory(userId.ToString("N"), movie.Id.ToString("N"), title, movie.ProductionYear, movie.GetProviderId(MetadataProvider.Tmdb), isRewatch);
                return;
            }

            // Human-like delay
            await Task.Delay(1000 + Random.Shared.Next(2000)).ConfigureAwait(false);

            await api.MarkAsWatched(
                filmResult.FilmSlug,
                filmResult.ProductionId,
                adjustedViewingDate,
                Array.Empty<string>(),
                favorite,
                rating: null,
                rewatch: isRewatch,
                log: msg => _logger.LogInformation("[RT-Sync] {Message}", msg)).ConfigureAwait(false);

            // Cache the successful sync
            syncCache[cacheKey] = DateTime.UtcNow;
            SaveSyncCache(syncCache);

            string rewatchLabel = isRewatch ? " (rewatch)" : string.Empty;
            _logger.LogInformation("Real-time sync: Logged {Movie}{Rewatch} for {User} on {Date}", title, rewatchLabel, user.Username, viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            AppendHistory(userId.ToString("N"), movie.Id.ToString("N"), title, movie.ProductionYear, movie.GetProviderId(MetadataProvider.Tmdb), isRewatch);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Real-time sync: Error logging {Movie}", title);
        }
    }

    private Dictionary<string, DateTime> LoadSyncCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading sync cache");
        }

        return new();
    }

    private void SaveSyncCache(Dictionary<string, DateTime> cache)
    {
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving sync cache");
        }
    }

    private void AppendHistory(string userId, string movieId, string name, int? year, string? tmdbId, bool rewatch = false)
    {
        try
        {
            List<Dictionary<string, object?>> history;
            if (File.Exists(HistoryPath))
            {
                var existing = File.ReadAllText(HistoryPath);
                history = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(existing) ?? new();
            }
            else
            {
                history = new();
            }

            string dateLogged = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            bool alreadyExists = history.Any(e =>
                e.TryGetValue("UserId", out var u) && string.Equals(u?.ToString(), userId, StringComparison.OrdinalIgnoreCase) &&
                e.TryGetValue("MovieId", out var m) && string.Equals(m?.ToString(), movieId, StringComparison.OrdinalIgnoreCase) &&
                e.TryGetValue("DateLogged", out var d) && string.Equals(d?.ToString()?.Substring(0, 10), dateLogged.Substring(0, 10), StringComparison.OrdinalIgnoreCase));

            if (!alreadyExists)
            {
                history.Add(new Dictionary<string, object?>
                {
                    ["UserId"] = userId,
                    ["MovieId"] = movieId,
                    ["Name"] = name,
                    ["Year"] = year,
                    ["DateLogged"] = dateLogged,
                    ["TmdbId"] = tmdbId,
                    ["Rewatch"] = rewatch
                });

                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history, HistorySerializerOptions));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error appending to history log");
        }
    }
}
