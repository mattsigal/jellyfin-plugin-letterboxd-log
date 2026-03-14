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
                                _logger.LogInformation("Skipping Letterboxd sync for {Movie}. Reason: Previously skipped and no new watch detected (Played: {Played} == Skip: {Skip}).", title, viewingDate.Value.Date, skipDate.Date);
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
                    try
                    {
                        // Apply user-configured timezone offset
                        if (viewingDate.HasValue)
                        {
                            viewingDate = viewingDate.Value.AddHours(account.TimezoneOffset);
                        }

                        // Use only the date part for comparison
                        DateTime viewingDateOnly = viewingDate.HasValue
                            ? new DateTime(viewingDate.Value.Year, viewingDate.Value.Month, viewingDate.Value.Day)
                            : DateTime.Today;

                        // Check in-memory cache: skip if already confirmed for this user+movie+date
                        string cacheKey = $"{user.Id:N}:{movie.Id:N}:{viewingDateOnly:yyyy-MM-dd}";
                        if (_syncCache.ContainsKey(cacheKey))
                        {
                            _logger.LogDebug("Cache hit — skipping Letterboxd check for {Movie} ({Date})", title, viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                            continue;
                        }

                        var dateLastLog = await api.GetDateLastLog(filmResult.FilmSlug).ConfigureAwait(false);

                        if (dateLastLog != null && dateLastLog.Value.Date == viewingDateOnly.Date)
                        {
                            // Confirmed on Letterboxd — cache it so we don't check again this window
                            if (_syncCache.TryAdd(cacheKey, DateTime.UtcNow))
                            {
                                SaveCache();
                            }
                            _logger.LogInformation("Film already logged in Letterboxd for this date ({Date}) User: {Username} Movie: {Movie}", viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), user.Username, title);
                        }
                        else
                        {
                            // human-like delay between films
                            await Task.Delay(1000 + Random.Shared.Next(2000), cancellationToken).ConfigureAwait(false);

                            await api.MarkAsWatched(filmResult.FilmSlug, filmResult.FilmId, viewingDate, tags, favorite, rating: null).ConfigureAwait(false);

                            // Successfully pushed — cache it
                            if (_syncCache.TryAdd(cacheKey, DateTime.UtcNow))
                            {
                                SaveCache();
                            }
                            _logger.LogInformation("Film logged in Letterboxd User: {Username} Movie: {Movie} Date: {ViewingDate}", user.Username, title, viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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
