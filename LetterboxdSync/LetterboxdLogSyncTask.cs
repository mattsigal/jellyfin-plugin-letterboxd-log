using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http; // Added for IHttpClientFactory
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
    private readonly ILogger<LetterboxdLogSyncTask> _logger; // Changed to ILogger<T>
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IActivityManager _activityManager; // Reverted to IActivityManager
    private readonly IHttpClientFactory _httpClientFactory; // Added

    public LetterboxdLogSyncTask(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            IActivityManager activityManager, // Reverted to IActivityManager
            ILogger<LetterboxdLogSyncTask> logger, // Changed to ILogger<T>
            IHttpClientFactory httpClientFactory) // Added
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
            _activityManager = activityManager; // Reverted to IActivityManager
            _logger = logger; // Assigned directly
            _httpClientFactory = httpClientFactory; // Assigned
        }

    private static PluginConfiguration Configuration =>
            Plugin.Instance!.Configuration;

    public string Name => "LetterboxdLog Sync";

    public string Key => "LetterboxdLogSync";

    public string Description => "Sync movies with Letterboxd (LetterboxdLog)";

    public string Category => "LetterboxdLog";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
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

            // Apply date filtering if enabled
            if (account.EnableDateFilter)
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-account.DateFilterDays);
                lstMoviesPlayed = lstMoviesPlayed.Where(movie =>
                {
                    var userItemData = _userDataManager.GetUserData(user, movie);
                    return userItemData != null && userItemData.LastPlayedDate.HasValue && userItemData.LastPlayedDate.Value >= cutoffDate;
                }).ToList();
            }

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

                                 // Remove Skip Tag
                                 movieTags.Remove(skipTag);
                                 // Remove .ignore tag if present (User requirement: delete .ignore on rewatch)
                                 if (hasIgnore)
                                 {
                                     movieTags.Remove(".ignore"); // Remove all instances? Case insensitive? List ref might be tricky.
                                     movieTags.RemoveAll(t => t.Equals(".ignore", StringComparison.OrdinalIgnoreCase));
                                 }

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
                        var dateLastLog = await api.GetDateLastLog(filmResult.FilmSlug).ConfigureAwait(false);

                        // Apply user-configured timezone offset
                        if (viewingDate.HasValue)
                        {
                            viewingDate = viewingDate.Value.AddHours(account.TimezoneOffset);
                        }

                        // Use only the date part for comparison
                        DateTime viewingDateOnly = viewingDate.HasValue
                            ? new DateTime(viewingDate.Value.Year, viewingDate.Value.Month, viewingDate.Value.Day)
                            : DateTime.Today;

                        if (dateLastLog != null && dateLastLog.Value.Date == viewingDateOnly.Date)
                        {
                            _logger.LogInformation("Film already logged in Letterboxd for this date ({Date}) User: {Username} Movie: {Movie}", viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), user.Username, title);
                        }
                        else
                        {
                            // human-like delay between films
                            await Task.Delay(1000 + Random.Shared.Next(2000), cancellationToken).ConfigureAwait(false);

                            await api.MarkAsWatched(filmResult.FilmSlug, filmResult.FilmId, viewingDate, tags, favorite).ConfigureAwait(false);

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
