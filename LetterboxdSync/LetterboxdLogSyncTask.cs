using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdLog.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdLog;

public class LetterboxdLogSyncTask : IScheduledTask
{
    // In-memory cache: tracks films confirmed as logged for a given user+date.
    // Key = "userId:movieId:dateString", Value = UTC timestamp of cache entry.
    // Survives across hourly runs (same plugin lifetime), cleared on Jellyfin restart.
    private static readonly ConcurrentDictionary<string, DateTime> _syncCache = new();
    private static readonly JsonSerializerOptions HistorySerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<LetterboxdLogSyncTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IActivityManager _activityManager;
    private readonly IHttpClientFactory _httpClientFactory;

    public LetterboxdLogSyncTask(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            IActivityManager activityManager,
            ILogger<LetterboxdLogSyncTask> logger,
            IHttpClientFactory httpClientFactory)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
            _activityManager = activityManager;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

    private string CachePath => Path.Combine(Path.GetDirectoryName(Plugin.Instance!.ConfigurationFilePath) ?? string.Empty, "LetterboxdLog_SyncCache.json");

    private string HistoryPath => Path.Combine(Path.GetDirectoryName(Plugin.Instance!.ConfigurationFilePath) ?? string.Empty, "LetterboxdLog_History.json");

    private static PluginConfiguration Configuration =>
            Plugin.Instance!.Configuration;

    public string Name => "LetterboxdLog Sync";

    public string Key => "LetterboxdLogSync";

    public string Description => "Sync movies with Letterboxd (LetterboxdLog)";

    public string Category => "LetterboxdLog";

    private void LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                if (data != null)
                {
                    foreach (var kvp in data)
                    {
                        _syncCache.TryAdd(kvp.Key, kvp.Value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Letterboxd sync cache from {Path}", CachePath);
        }
    }

    private void SaveCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(_syncCache);
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving Letterboxd sync cache to {Path}", CachePath);
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

            // Avoid duplicates
            bool alreadyExists = history.Any(e =>
                e.TryGetValue("UserId", out var u) && string.Equals(u?.ToString(), userId, StringComparison.OrdinalIgnoreCase) &&
                e.TryGetValue("MovieId", out var m) && string.Equals(m?.ToString(), movieId, StringComparison.OrdinalIgnoreCase) &&
                e.TryGetValue("DateLogged", out var d) && string.Equals(d?.ToString(), dateLogged, StringComparison.OrdinalIgnoreCase));

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
            _logger.LogError(ex, "Error appending to Letterboxd history log");
        }
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Persistent caching: Load from disk
        LoadCache();

        // Prune stale cache entries (older than 2 days to cover any filter window)
        var pruneThreshold = DateTime.UtcNow.AddDays(-2);
        bool cacheChanged = false;

        foreach (var key in _syncCache.Keys)
        {
            if (_syncCache.TryGetValue(key, out var ts) && ts < pruneThreshold)
            {
                if (_syncCache.TryRemove(key, out _))
                {
                    cacheChanged = true;
                }
            }
        }

        if (cacheChanged)
        {
            SaveCache();
        }

        // Tag Cleanup Pass: Remove .ignore and LetterboxdSkip from old watches
        // This resets the UI and facilitates re-watch detection after the filter window.
        try
        {
            var moviesWithIgnore = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Tags = new[] { ".ignore" },
                Recursive = true
            });

            if (moviesWithIgnore.Count > 0)
            {
                var cleanupDate = DateTime.Today.AddDays(-14); // 2-week sliding window for tag retention
                int cleanupCount = 0;

                foreach (var m in moviesWithIgnore)
                {
                    var tags = m.Tags.ToList();
                    var skipTag = tags.FirstOrDefault(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase));
                    if (skipTag != null && DateTime.TryParse(skipTag.Split(':')[1], out DateTime skipDate))
                    {
                        if (skipDate < cleanupDate)
                        {
                            tags.RemoveAll(t => t.Equals(".ignore", StringComparison.OrdinalIgnoreCase));
                            tags.RemoveAll(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase));
                            m.Tags = tags.ToArray();
                            await m.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                            cleanupCount++;
                        }
                    }
                }

                if (cleanupCount > 0)
                {
                    _logger.LogInformation("Cleanup: Removed old .ignore tags from {Count} movies.", cleanupCount);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Letterboxd tag cleanup pass.");
        }

        var lstUsers = _userManager.Users;
        foreach (var user in lstUsers)
        {
            var account = Configuration.Accounts.FirstOrDefault(account => account.UserJellyfin == user.Id.ToString("N") && account.Enable);

            if (account == null)
            {
                continue;
            }

            var lstMoviesPlayed = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new List<BaseItemKind>() { BaseItemKind.Movie }.ToArray(),
                IsVirtualItem = false,
                IsPlayed = true,
            });

            if (lstMoviesPlayed.Count == 0)
            {
                continue;
            }

            // Apply date filtering. If not explicitly enabled, default to a 24-hour window to prevent mass-syncing history.
            var filterDays = account.EnableDateFilter ? account.DateFilterDays : 1;
            var cutoffDate = DateTime.UtcNow.AddDays(-filterDays);
            var cutoffLocal = cutoffDate.AddHours(account.TimezoneOffset);

            _logger.LogInformation("Syncing movies played since {CutoffDate} (FilterDays: {Days}, ExplicitlyEnabled: {Enabled})", cutoffLocal, filterDays, account.EnableDateFilter);

            lstMoviesPlayed = lstMoviesPlayed.Where(movie =>
            {
                var userItemData = _userDataManager.GetUserData(user, movie);
                return userItemData != null && userItemData.LastPlayedDate.HasValue && userItemData.LastPlayedDate.Value >= cutoffDate;
            }).ToList();

            var api = new LetterboxdApi(account.CookiesUserAgent ?? account.UserAgent ?? string.Empty);
            try
            {
                api.SetRawCookies(account.CookiesRaw ?? account.Cookie);
                await api.Authenticate(account.UserLetterboxd ?? string.Empty, account.PasswordLetterboxd ?? string.Empty).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Message User: {Username} ({UserId})", user.Username, user.Id.ToString("N"));

                continue;
            }

            foreach (var movie in lstMoviesPlayed)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                int tmdbid;
                string title = movie.OriginalTitle ?? movie.Name ?? "Unknown Title";
                var userItemData = _userDataManager.GetUserData(user, movie);
                if (userItemData == null)
                {
                    continue;
                }

                bool favorite = movie.IsFavoriteOrLiked(user, userItemData) && account.SendFavorite;
                DateTime? viewingDate = userItemData.LastPlayedDate;
                string[] tags = Array.Empty<string>();

                // Verify "Bona Fide" Watch via Activity Log
                // TODO: Re-enable this check once IActivityManager.GetActivityLogEntries signature is confirmed.
                if (viewingDate.HasValue)
                {
                    // Logic to handle Manual Ignored Items
                    var movieTags = movie.Tags.ToList(); // Work with a list for easy modification
                    bool hasIgnore = movieTags.Contains(".ignore", StringComparer.OrdinalIgnoreCase);
                    var skipTag = movieTags.FirstOrDefault(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase));

                    // Case 1: Rewatch Detection (Skip Override)
                    if (skipTag != null)
                    {
                        if (DateTime.TryParse(skipTag.Split(':')[1], out DateTime skipDate))
                        {
                            // If LastPlayed is significantly newer than the skip date (e.g. next day), treating it as a rewatch
                            if (viewingDate.Value.Date > skipDate.Date)
                            {
                                 _logger.LogInformation("Rewatch detected for {Movie}: LastPlayed ({Played}) > SkipDate ({Skip}). Resume syncing.", title, viewingDate.Value.Date, skipDate.Date);

                                 // Remove Skip Tag and .ignore robustly
                                 movieTags.RemoveAll(t => t.StartsWith("LetterboxdSkip:", StringComparison.OrdinalIgnoreCase));
                                 movieTags.RemoveAll(t => t.Equals(".ignore", StringComparison.OrdinalIgnoreCase));

                                 // Commit changes to Metadata
                                 movie.Tags = movieTags.ToArray();
                                 await movie.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                                 // PROCEED TO SYNC (Do not continue loop)
                            }
                            else
                            {
                                // Still within the skipped timeframe (same day or earlier)
                                // If .ignore is missing but SkipTag is present, we still respect the SkipTag until a new date appears.
                                _logger.LogDebug("Skipping Letterboxd sync for {Movie}. Reason: Previously skipped and no new watch detected (Played: {Played} == Skip: {Skip}).", title, viewingDate.Value.Date, skipDate.Date);
                                continue;
                            }
                        }
                    }

                    // Case 2: Explicit Ignore
                    else if (hasIgnore)
                    {
                        // Add Skip Tag for today
                        string todaySkip = $"LetterboxdSkip:{DateTime.Today:yyyy-MM-dd}";
                        if (!movieTags.Contains(todaySkip))
                        {
                            movieTags.Add(todaySkip);
                            movie.Tags = movieTags.ToArray();
                            await movie.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                        }

                        _logger.LogInformation("Skipping Letterboxd sync for {Movie} due to presence of .ignore tag.", title);
                        continue;
                    }
                }

                // Compute viewing date early so the cache check can happen before API calls
                DateTime adjustedViewingDate = viewingDate.HasValue
                    ? viewingDate.Value.AddHours(account.TimezoneOffset)
                    : DateTime.UtcNow;
                DateTime viewingDateOnly = new DateTime(adjustedViewingDate.Year, adjustedViewingDate.Month, adjustedViewingDate.Day);

                // Check in-memory cache BEFORE any Letterboxd API calls.
                // This prevents re-querying films that were already confirmed as logged,
                // even when Letterboxd returns transient errors (e.g. 403).
                string cacheKey = $"{user.Id:N}:{movie.Id:N}:{viewingDateOnly:yyyy-MM-dd}";
                if (_syncCache.ContainsKey(cacheKey))
                {
                    _logger.LogDebug("Cache hit — skipping Letterboxd sync for {Movie} ({Date})", title, viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    continue;
                }

                FilmResult? filmResult = null;

                if (int.TryParse(movie.GetProviderId(MetadataProvider.Tmdb), out tmdbid))
                {
                    try
                    {
                        filmResult = await api.SearchFilmByTmdbId(tmdbid).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("TMDB search failed for {Movie} ({TmdbId}): {Message}. Trying IMDb fallback...", title, tmdbid, ex.Message);
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
                            _logger.LogError("IMDb search failed for {Movie} ({ImdbId}): {Message}", title, imdbid, ex.Message);
                        }
                    }
                }

                if (filmResult != null)
                {
                    _logger.LogDebug("Resolved Letterboxd: {Slug} (filmId={Id}, productionId={Lid}) for {Movie}", filmResult.FilmSlug, filmResult.FilmId, filmResult.ProductionId, title);
                    try
                    {
                        var dateLastLog = await api.GetDateLastLog(filmResult.FilmSlug).ConfigureAwait(false);

                        if (dateLastLog != null && dateLastLog.Value.Date == viewingDateOnly.Date)
                        {
                            // Confirmed on Letterboxd — cache it so we don't check again this window
                            if (_syncCache.TryAdd(cacheKey, DateTime.UtcNow))
                            {
                                SaveCache();
                            }

                            _logger.LogInformation("Film already logged in Letterboxd for this date ({Date}) User: {Username} Movie: {Movie}", viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), user.Username, title);

                            AppendHistory(
                                user.Id.ToString("N"),
                                movie.Id.ToString("N"),
                                title,
                                movie.ProductionYear,
                                adjustedViewingDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                                movie.GetProviderId(MetadataProvider.Tmdb));
                        }
                        else
                        {
                            // Rewatch detection: if there's any prior diary entry, this is a rewatch
                            bool isRewatch = dateLastLog.HasValue;

                            // human-like delay between films
                            await Task.Delay(1000 + Random.Shared.Next(2000), cancellationToken).ConfigureAwait(false);

                            await api.MarkAsWatched(filmResult.FilmSlug, filmResult.ProductionId, adjustedViewingDate, tags, favorite, rating: null, rewatch: isRewatch, log: msg => _logger.LogInformation("[MarkAsWatched] {Message}", msg)).ConfigureAwait(false);

                            // Successfully pushed — cache it
                            if (_syncCache.TryAdd(cacheKey, DateTime.UtcNow))
                            {
                                SaveCache();
                            }

                            string rewatchLabel = isRewatch ? " (rewatch)" : string.Empty;
                            _logger.LogInformation("Film logged{Rewatch} in Letterboxd User: {Username} Movie: {Movie} Date: {ViewingDate}", rewatchLabel, user.Username, title, viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

                            AppendHistory(
                                user.Id.ToString("N"),
                                movie.Id.ToString("N"),
                                title,
                                movie.ProductionYear,
                                adjustedViewingDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                                movie.GetProviderId(MetadataProvider.Tmdb),
                                isRewatch);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error logging film {Movie}: {Message}", title, ex.Message);
                    }
                }
                else
                {
                    _logger.LogWarning("Could not find movie on Letterboxd: {Movie}", title);
                }
            }
        }

        progress.Report(100);
        return;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromDays(1).Ticks
                }
            };
}
