using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdLog.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdLog;

public class LetterboxdLogSyncTask : IScheduledTask
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public LetterboxdLogSyncTask(
            IUserManager userManager,
            ILoggerFactory loggerFactory,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager)
        {
            _logger = loggerFactory.CreateLogger<LetterboxdLogSyncTask>();
            _loggerFactory = loggerFactory;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
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
                continue;

            var lstMoviesPlayed = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new List<BaseItemKind>() { BaseItemKind.Movie }.ToArray(),
                IsVirtualItem = false,
                IsPlayed = true,
            });

            if (lstMoviesPlayed.Count == 0)
                continue;

            // Apply date filtering if enabled
            if (account.EnableDateFilter)
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-account.DateFilterDays);
                lstMoviesPlayed = lstMoviesPlayed.Where(movie =>
                {
                    var userItemData = _userDataManager.GetUserData(user, movie);
                    return userItemData.LastPlayedDate.HasValue && userItemData.LastPlayedDate.Value >= cutoffDate;
                }).ToList();
            }

            var api = new LetterboxdApi(account.CookiesUserAgent ?? account.UserAgent ?? string.Empty);
            try
            {
                api.SetRawCookies(account.CookiesRaw ?? account.Cookie);
                await api.Authenticate(account.UserLetterboxd, account.PasswordLetterboxd).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    @"{Message}
                    User: {Username} ({UserId})",
                    ex.Message,
                    user.Username, user.Id.ToString("N"));

                continue;
            }

            foreach (var movie in lstMoviesPlayed)
            {
                if (cancellationToken.IsCancellationRequested) return;

                int tmdbid;
                string title = movie.OriginalTitle;
                var userItemData = _userDataManager.GetUserData(user, movie);
                bool favorite = movie.IsFavoriteOrLiked(user) && account.SendFavorite;
                DateTime? viewingDate = userItemData.LastPlayedDate;
                string[] tags = Array.Empty<string>();

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
                    string imdbid = movie.GetProviderId(MetadataProvider.Imdb);
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
                        var dateLastLog = await api.GetDateLastLog(filmResult.filmSlug).ConfigureAwait(false);

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
                            _logger.LogInformation(
                                @"Film already logged in Letterboxd for this date ({Date})
                                User: {Username}
                                Movie: {Movie}",
                                viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                user.Username, title);
                        }
                        else
                        {
                            // human-like delay between films
                            await Task.Delay(1000 + Random.Shared.Next(2000), cancellationToken).ConfigureAwait(false);

                            await api.MarkAsWatched(filmResult.filmSlug, filmResult.filmId, viewingDate, tags, favorite).ConfigureAwait(false);

                            _logger.LogInformation(
                                @"Film logged in Letterboxd
                                User: {Username}
                                Movie: {Movie}
                                Date: {ViewingDate}",
                                user.Username, title, viewingDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            @"Error logging film {Movie}: {Message}
                            StackTrace: {StackTrace}",
                            title, ex.Message, ex.StackTrace);
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
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromDays(1).Ticks
                }
            };
}
