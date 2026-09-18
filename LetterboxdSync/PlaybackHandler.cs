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
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _logger.LogInformation("LetterboxdLog real-time sync handler registered");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
    }

    private async void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            // Only handle movie rating events; skip playback progress and unrated items immediately to prevent log spam
            if (e.Item == null || e.Item.GetBaseItemKind() != BaseItemKind.Movie || !e.UserData.Rating.HasValue || e.UserData.Rating.Value <= 0)
            {
                return;
            }

            _logger.LogDebug(
                "LetterboxdLog: UserDataSaved event received: Item={ItemName} ({Kind}), Reason={Reason}, User={UserId}, Rating={Rating}",
                e.Item.Name ?? "null",
                e.Item.GetBaseItemKind(),
                e.SaveReason,
                e.UserId,
                e.UserData.Rating);

            await HandleUserDataSavedAsync(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in LetterboxdLog UserDataSaved handler");
        }
    }

    private async Task HandleUserDataSavedAsync(UserDataSaveEventArgs e)
    {
        // Only handle movies
        if (e.Item == null || e.Item.GetBaseItemKind() != BaseItemKind.Movie)
        {
            return;
        }

        // Only handle when rating is present and positive
        if (!e.UserData.Rating.HasValue || e.UserData.Rating.Value <= 0)
        {
            return;
        }

        var userId = e.UserId;
        if (userId == Guid.Empty)
        {
            return;
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.LogWarning("LetterboxdLog: UserDataSaved user not found for ID {UserId}", userId);
            return;
        }

        var account = Configuration.Accounts.FirstOrDefault(a =>
            a.UserJellyfin == userId.ToString("N") && a.Enable);
        if (account == null || !account.SendRating)
        {
            _logger.LogInformation("LetterboxdLog: Rating sync skipped for user {User} (SendRating is disabled or account not mapped)", user.Username);
            return;
        }

        var movie = e.Item;
        string title = movie.OriginalTitle ?? movie.Name ?? "Unknown Title";

        // Check ignore tag
        var movieTags = movie.Tags.ToList();
        if (movieTags.Contains(".ignore", StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Real-time rating sync: Skipping {Movie} due to .ignore tag", title);
            return;
        }

        _logger.LogInformation("Real-time rating sync: Debouncing rating event for {Movie} (Rating={Rating}) User={User}", title, e.UserData.Rating.Value, user.Username);

        // Brief debounce delay in case user taps multiple stars in quick succession
        await Task.Delay(1500).ConfigureAwait(false);

        // Re-read user data to get final settled rating
        var freshData = _userDataManager.GetUserData(user, movie);
        if (freshData == null || !freshData.Rating.HasValue || freshData.Rating.Value <= 0)
        {
            return;
        }

        int jfRating = (int)Math.Round(freshData.Rating.Value);

        // Authenticate with Letterboxd
        var api = new LetterboxdApi(account.CookiesUserAgent ?? account.UserAgent ?? string.Empty);
        try
        {
            api.SetRawCookies(account.CookiesRaw ?? account.Cookie);
            await api.Authenticate(account.UserLetterboxd ?? string.Empty, account.PasswordLetterboxd ?? string.Empty).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Real-time rating sync: Auth failed for user {User}", user.Username);
            return;
        }

        var filmResult = await ResolveFilmAsync(api, movie, title).ConfigureAwait(false);
        if (filmResult == null)
        {
            _logger.LogWarning("Real-time rating sync: Could not find {Movie} on Letterboxd", title);
            return;
        }

        try
        {
            var diaryEntries = await api.GetDiaryEntries(filmResult.FilmSlug).ConfigureAwait(false);
            if (diaryEntries.Count == 0)
            {
                _logger.LogInformation("Real-time rating sync: No diary entries found on Letterboxd for {Movie}. Rating sync applies to logged viewings.", title);
                return;
            }

            DiaryEntryResult? targetEntry = null;
            if (freshData.LastPlayedDate.HasValue)
            {
                var adjustedDate = freshData.LastPlayedDate.Value.AddHours(account.TimezoneOffset);
                var viewingDateOnly = adjustedDate.Date;
                targetEntry = diaryEntries.FirstOrDefault(entry => entry.Date.Date == viewingDateOnly);
            }

            targetEntry ??= diaryEntries.OrderByDescending(entry => entry.Date).FirstOrDefault();

            if (targetEntry != null && !string.IsNullOrEmpty(targetEntry.ViewingId))
            {
                if (targetEntry.Rating == jfRating)
                {
                    _logger.LogInformation("Real-time rating sync: {Movie} is already rated {Rating}/10 on Letterboxd", title, jfRating);
                    return;
                }

                _logger.LogInformation(
                    "Real-time rating sync: Updating Letterboxd rating for {Movie} (ViewingId: {ViewingId}, Current: {CurrentRating}) to {Rating}/10 ({Stars}★) User: {User}",
                    title,
                    targetEntry.ViewingId,
                    targetEntry.Rating?.ToString(CultureInfo.InvariantCulture) ?? "unrated",
                    jfRating,
                    jfRating / 2.0,
                    user.Username);

                await api.ChangeViewingRating(targetEntry.ViewingId, jfRating, log: msg => _logger.LogInformation("[RT-Rating] {Message}", msg)).ConfigureAwait(false);

                // Update cache
                var viewingDate = freshData.LastPlayedDate ?? DateTime.UtcNow;
                var adjustedViewingDate = viewingDate.AddHours(account.TimezoneOffset);
                var viewingDateOnly = new DateTime(adjustedViewingDate.Year, adjustedViewingDate.Month, adjustedViewingDate.Day);
                string cacheKey = $"{userId:N}:{movie.Id:N}:{viewingDateOnly:yyyy-MM-dd}:r{jfRating}";
                var syncCache = LoadSyncCache();
                syncCache[cacheKey] = DateTime.UtcNow;
                SaveSyncCache(syncCache);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Real-time rating sync: Error updating rating for {Movie}: {Message}", title, ex.Message);
        }
    }

    private async Task<FilmResult?> ResolveFilmAsync(LetterboxdApi api, BaseItem movie, string title)
    {
        if (int.TryParse(movie.GetProviderId(MetadataProvider.Tmdb), out int tmdbid))
        {
            try
            {
                return await api.SearchFilmByTmdbId(tmdbid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Letterboxd lookup: TMDB lookup failed for {Movie}: {Msg}", title, ex.Message);
            }
        }

        string? imdbid = movie.GetProviderId(MetadataProvider.Imdb);
        if (!string.IsNullOrEmpty(imdbid))
        {
            try
            {
                return await api.SearchFilmByImdbId(imdbid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Letterboxd lookup: IMDb lookup failed for {Movie}: {Msg}", title, ex.Message);
            }
        }

        return null;
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

        // Skip logic (Rewatch Override)
        var movieTags = movie.Tags.ToList();
        var hasIgnore = movieTags.Contains(".ignore", StringComparer.OrdinalIgnoreCase);
        var skipTag = movieTags.FirstOrDefault(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase));

        // If explicitly ignored ("Watched (No Sync)"), ALWAYS skip and preserve tag
        if (hasIgnore)
        {
            _logger.LogInformation("Real-time sync: Skipping {Movie} due to .ignore tag", title);
            return;
        }

        // Compute viewing date early for tag/rewatch comparison
        var viewingDate = DateTime.UtcNow;
        var adjustedViewingDate = viewingDate.AddHours(account.TimezoneOffset);
        var viewingDateOnly = new DateTime(adjustedViewingDate.Year, adjustedViewingDate.Month, adjustedViewingDate.Day);

        if (skipTag != null)
        {
            if (DateTime.TryParse(skipTag.Split(':')[1], out DateTime skipDate))
            {
                if (viewingDateOnly.Date > skipDate.Date)
                {
                    _logger.LogInformation("Real-time sync: Rewatch detected for {Movie}: ViewingDate ({View}) > SkipDate ({Skip}). Syncing.", title, viewingDateOnly.Date, skipDate.Date);
                    // Remove skip tag — will be re-added upon success
                    movieTags.RemoveAll(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    _logger.LogDebug("Real-time sync: Skipping {Movie} due to recent skip tag ({Date})", title, skipDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    return;
                }
            }
        }

        // Determine user data & rating
        var userItemData = _userDataManager.GetUserData(user, movie);
        bool favorite = movie.IsFavoriteOrLiked(user, userItemData) && account.SendFavorite;
        int? jfRating = (account.SendRating && userItemData?.Rating.HasValue == true && userItemData.Rating.Value > 0)
            ? (int?)Math.Round(userItemData.Rating.Value)
            : null;

        // Check sync cache
        string cacheKey = $"{userId:N}:{movie.Id:N}:{viewingDateOnly:yyyy-MM-dd}:r{jfRating ?? 0}";
        string legacyCacheKey = $"{userId:N}:{movie.Id:N}:{viewingDateOnly:yyyy-MM-dd}";
        var syncCache = LoadSyncCache();
        if (syncCache.ContainsKey(cacheKey) || (!jfRating.HasValue && syncCache.ContainsKey(legacyCacheKey)))
        {
            _logger.LogDebug("Real-time sync: Cache hit for {Movie} ({Date}), skipping", title, viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return;
        }

        _logger.LogInformation("Real-time sync: Processing {Movie} for user {User}", title, user.Username);

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
        var filmResult = await ResolveFilmAsync(api, movie, title).ConfigureAwait(false);
        if (filmResult == null)
        {
            _logger.LogWarning("Real-time sync: Could not find {Movie} on Letterboxd", title);
            return;
        }

        try
        {
            // Check for existing diary entries (rewatch detection & rating sync)
            var diaryEntries = await api.GetDiaryEntries(filmResult.FilmSlug).ConfigureAwait(false);
            var existingEntry = diaryEntries.FirstOrDefault(e => e.Date.Date == viewingDateOnly.Date);
            bool isRewatch = diaryEntries.Count > 0;

            if (existingEntry != null)
            {
                if (account.SendRating && jfRating.HasValue && !string.IsNullOrEmpty(existingEntry.ViewingId) && existingEntry.Rating != jfRating.Value)
                {
                    _logger.LogInformation(
                        "Real-time sync: Updating Letterboxd rating for {Movie} (ViewingId: {ViewingId}) to {Rating}/10 ({Stars}★) User: {User}",
                        title,
                        existingEntry.ViewingId,
                        jfRating.Value,
                        jfRating.Value / 2.0,
                        user.Username);

                    await api.ChangeViewingRating(existingEntry.ViewingId, jfRating.Value, log: msg => _logger.LogInformation("[RT-Rating] {Message}", msg)).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogInformation("Real-time sync: Already logged today ({Date}) — {Movie}", viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), title);
                }

                // Already logged today — cache and skip
                syncCache[cacheKey] = DateTime.UtcNow;
                SaveSyncCache(syncCache);

                AppendHistory(
                    userId.ToString("N"),
                    movie.Id.ToString("N"),
                    title,
                    movie.ProductionYear,
                    adjustedViewingDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                    movie.GetProviderId(MetadataProvider.Tmdb),
                    isRewatch);
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
                rating: jfRating,
                rewatch: isRewatch,
                log: msg => _logger.LogInformation("[RT-Sync] {Message}", msg)).ConfigureAwait(false);

            // Cache the successful sync
            syncCache[cacheKey] = DateTime.UtcNow;
            SaveSyncCache(syncCache);

            // Add skip tag after successful sync to keep Jellyfin UI in sync
            string todaySkip = $"LetterboxdSkip:{viewingDateOnly:yyyy-MM-dd}";
            bool changed = false;

            if (!movieTags.Contains(todaySkip, StringComparer.OrdinalIgnoreCase))
            {
                movieTags.Add(todaySkip);
                changed = true;
            }

            if (changed)
            {
                movie.Tags = movieTags.ToArray();
                await movie.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, default).ConfigureAwait(false);
            }

            string rewatchLabel = isRewatch ? " (rewatch)" : string.Empty;
            string ratingLabel = jfRating.HasValue ? $" with rating {jfRating.Value}/10 ({jfRating.Value / 2.0}★)" : string.Empty;
            _logger.LogInformation("Real-time sync: Logged {Movie}{Rewatch} for {User} on {Date}{Rating}", title, rewatchLabel, user.Username, viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ratingLabel);

            AppendHistory(
                userId.ToString("N"),
                movie.Id.ToString("N"),
                title,
                movie.ProductionYear,
                adjustedViewingDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                movie.GetProviderId(MetadataProvider.Tmdb),
                isRewatch);
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

    private void AppendHistory(string userId, string movieId, string name, int? year, string dateLogged, string? tmdbId, bool rewatch = false)
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
