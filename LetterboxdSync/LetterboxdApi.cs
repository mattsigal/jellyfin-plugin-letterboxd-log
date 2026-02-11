using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace LetterboxdSync;

public class LetterboxdApi
{
    private string csrf = string.Empty;
    private string username = string.Empty;

    public string Csrf => csrf;

    private readonly CookieContainer cookieContainer = new CookieContainer();
    private readonly HttpClientHandler handler;
    private readonly HttpClient client;

    private static readonly Uri BaseUri = new Uri("https://letterboxd.com/");

    private bool HasAuthenticatedSession()
    {
        var cookies = cookieContainer.GetCookies(BaseUri);
        return !string.IsNullOrWhiteSpace(cookies["letterboxd.user.CURRENT"]?.Value);
    }

    public void SetRawCookies(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        var baseUri = new Uri("https://letterboxd.com/");
        foreach (var part in raw.Split(';'))
        {
            var kv = part.Trim();
            if (string.IsNullOrEmpty(kv)) continue;
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            var name = kv.Substring(0, eq).Trim();
            var val = kv.Substring(eq + 1).Trim();
            try
            {
                val = WebUtility.UrlDecode(val);
                var cookie = new Cookie(name, val, "/", "letterboxd.com")
                {
                    HttpOnly = false,
                };
                cookieContainer.Add(baseUri, cookie);

                var dotCookie = new Cookie(name, val, "/", ".letterboxd.com")
                {
                    HttpOnly = false,
                };
                cookieContainer.Add(baseUri, dotCookie);

                if (string.Equals(name, "com.xk72.webparts.csrf", StringComparison.OrdinalIgnoreCase))
                {
                    this.csrf = val;
                }
            }
            catch
            {
                // ignore malformed cookie entries
            }
        }
    }

    private async Task RefreshCsrfFromFilmPageAsync(string filmSlug)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/film/{filmSlug}/");
        SetNavigationHeaders(request.Headers, "same-origin");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        var fresh = ExtractHiddenInput(html, "__csrf");
        if (string.IsNullOrWhiteSpace(fresh))
            throw new Exception($"Could not extract __csrf from film page /film/{filmSlug}/");

        this.csrf = fresh!;
    }

    private string GetCsrfFromCookie()
    {
        var cookies = cookieContainer.GetCookies(BaseUri);
        return cookies["com.xk72.webparts.csrf"]?.Value ?? string.Empty;
    }

    private async Task RefreshCsrfCookieAsync()
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/"))
        {
            SetNavigationHeaders(request.Headers);
            using var _ = await client.SendAsync(request).ConfigureAwait(false);
        }

        var token = GetCsrfFromCookie();
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("Could not read CSRF cookie 'com.xk72.webparts.csrf' after refreshing.");
        this.csrf = token;
    }

    public LetterboxdApi(string userAgent = "")
    {
        handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true
        };

        client = new HttpClient(handler)
        {
            BaseAddress = BaseUri
        };

        client.DefaultRequestHeaders.UserAgent.Clear();
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }
        else
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36");
        }

        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        client.DefaultRequestHeaders.Connection.Add("keep-alive");

        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "document");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "navigate");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site", "none");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-user", "?1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("upgrade-insecure-requests", "1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Priority", "u=0, i");
    }

    private void SetNavigationHeaders(HttpRequestHeaders headers, string site = "none", string? referrer = null)
    {
        headers.Accept.Clear();
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/avif"));
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/apng"));
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

        headers.TryAddWithoutValidation("sec-fetch-dest", "document");
        headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
        headers.TryAddWithoutValidation("sec-fetch-site", site);
        headers.TryAddWithoutValidation("sec-fetch-user", "?1");
        headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");

        if (headers.Contains("Priority")) headers.Remove("Priority");
        headers.TryAddWithoutValidation("Priority", "u=0, i");

        if (referrer != null)
        {
            headers.Referrer = new Uri(referrer);
        }
    }

    public async Task Authenticate(string username, string password)
    {
        this.username = username;

        if (HasAuthenticatedSession())
        {
            try
            {
                await RefreshCsrfCookieAsync().ConfigureAwait(false);
                return;
            }
            catch
            {
                // Proceed to normal login
            }
        }

        await Task.Delay(500 + Random.Shared.Next(1000)).ConfigureAwait(false);

        using (var signInRequest = new HttpRequestMessage(HttpMethod.Get, "/sign-in/"))
        {
            SetNavigationHeaders(signInRequest.Headers);

            using (var signInResponse = await client.SendAsync(signInRequest).ConfigureAwait(false))
            {
                if (signInResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (cookieContainer.GetCookies(BaseUri).Cast<Cookie>().Any(c => c.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase)))
                    {
                        await Task.Delay(1500).ConfigureAwait(false);
                        using var warmup = new HttpRequestMessage(HttpMethod.Get, "/");
                        SetNavigationHeaders(warmup.Headers);
                        using var _ = await client.SendAsync(warmup).ConfigureAwait(false);

                        using var retryReq = new HttpRequestMessage(HttpMethod.Get, "/sign-in/");
                        SetNavigationHeaders(retryReq.Headers);
                        using var retryRes = await client.SendAsync(retryReq).ConfigureAwait(false);
                        if (retryRes.StatusCode == HttpStatusCode.Forbidden)
                        {
                            var rbody = await retryRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (rbody.Length > 300) rbody = rbody.Substring(0, 300);
                            throw new Exception("Letterboxd returned 403 on /sign-in/ even after using provided Cloudflare cookies. Body: " + rbody);
                        }

                        var retryHtml = await retryRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var csrfFromRetry = ExtractHiddenInput(retryHtml, "__csrf");
                        if (string.IsNullOrWhiteSpace(csrfFromRetry))
                            throw new Exception("Could not find __csrf token on /sign-in/ after retry.");
                        this.csrf = csrfFromRetry;
                        goto AfterSignIn;
                    }

                    var body = await signInResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 300) body = body.Substring(0, 300);
                    throw new Exception("Letterboxd returned 403 on /sign-in/. Body: " + body);
                }

                if (signInResponse.StatusCode != HttpStatusCode.OK)
                {
                    var body = await signInResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 300) body = body.Substring(0, 300);
                    throw new Exception($"Letterboxd returned {(int)signInResponse.StatusCode} on /sign-in/. Body: " + body);
                }

                var signInHtml = await signInResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                var csrfFromSignIn = ExtractHiddenInput(signInHtml, "__csrf");
                if (string.IsNullOrWhiteSpace(csrfFromSignIn))
                {
                    throw new Exception("Could not find __csrf token on /sign-in/ (login flow likely changed).");
                }

                this.csrf = csrfFromSignIn;
            }
        }
        AfterSignIn:;

        await Task.Delay(3000 + Random.Shared.Next(4000)).ConfigureAwait(false);
        using (var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/user/login.do"))
        {
            loginRequest.Headers.Accept.Clear();
            loginRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            loginRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/javascript"));
            loginRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.01));
            loginRequest.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            loginRequest.Headers.Referrer = new Uri("https://letterboxd.com/sign-in/");
            loginRequest.Headers.TryAddWithoutValidation("Origin", "https://letterboxd.com");
            loginRequest.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
            loginRequest.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
            loginRequest.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
            loginRequest.Headers.TryAddWithoutValidation("Priority", "u=1, i");

            loginRequest.Headers.Remove("sec-fetch-user");
            loginRequest.Headers.Remove("upgrade-insecure-requests");

            loginRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "username", username },
                { "password", password },
                { "__csrf", this.csrf },
                { "remember", "true" },
                { "authenticationCode", "" }
            });

            using (var loginResponse = await client.SendAsync(loginRequest).ConfigureAwait(false))
            {
                if (loginResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    var body = await loginResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 300) body = body.Substring(0, 300);
                    throw new Exception("Letterboxd returned 403 during login. Body: " + body);
                }

                if (!loginResponse.IsSuccessStatusCode)
                {
                    var body = await loginResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 300) body = body.Substring(0, 300);
                    throw new Exception($"Letterboxd returned {(int)loginResponse.StatusCode} during login. Body: " + body);
                }

                var loginBody = await loginResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (JsonDocument doc = JsonDocument.Parse(loginBody))
                {
                    var json = doc.RootElement;
                    if (json.TryGetProperty("result", out var resultEl) && resultEl.GetString() == "error")
                    {
                        var msg = "Login failed";
                        if (json.TryGetProperty("messages", out var msgsEl))
                        {
                            var sb = new StringBuilder();
                            foreach (var m in msgsEl.EnumerateArray())
                                sb.Append(m.GetString()).Append(' ');
                            msg = sb.ToString().Trim();
                        }
                        throw new Exception("Letterboxd login error: " + msg);
                    }
                }
            }
        }

        await RefreshCsrfCookieAsync().ConfigureAwait(false);
    }

    public async Task<FilmResult> SearchFilmByTmdbId(int tmdbid)
    {
        await Task.Delay(500 + Random.Shared.Next(500)).ConfigureAwait(false);

        var tmdbPath = $"/tmdb/{tmdbid}";

        using (var searchRequest = new HttpRequestMessage(HttpMethod.Get, tmdbPath))
        {
            SetNavigationHeaders(searchRequest.Headers, "same-origin");

            using (var res = await client.SendAsync(searchRequest).ConfigureAwait(false))
            {
                if (res.StatusCode == HttpStatusCode.Forbidden)
                {
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 300) body = body.Substring(0, 300);
                    throw new Exception($"TMDB lookup returned 403 (Forbidden) for https://letterboxd.com/tmdb/{tmdbid}. Body: " + body);
                }

                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 300) body = body.Substring(0, 300);
                    throw new Exception($"TMDB lookup returned {(int)res.StatusCode} for https://letterboxd.com/tmdb/{tmdbid}. Body: " + body);
                }

                var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);

                string filmUrl = htmlDoc.DocumentNode
                    .SelectSingleNode("//link[@rel='canonical']")
                    ?.GetAttributeValue("href", string.Empty) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(filmUrl))
                {
                    var a = htmlDoc.DocumentNode.SelectSingleNode("//a[starts-with(@href, '/film/')]");
                    var href = a?.GetAttributeValue("href", string.Empty) ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(href))
                        filmUrl = href.StartsWith("/") ? "https://letterboxd.com" + href : href;
                }

                if (string.IsNullOrWhiteSpace(filmUrl))
                {
                    var finalUri = res?.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
                    throw new Exception($"The search returned no results (Could not resolve film URL from TMDB page). FinalUrl='{finalUri}'");
                }

                var filmUri = new Uri(filmUrl, UriKind.Absolute);
                var segments = filmUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length < 2 || !segments[0].Equals("film", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"TMDB page resolved to non-film URL: '{filmUrl}'");

                string filmSlug = segments[1];

                using (var filmRequest = new HttpRequestMessage(HttpMethod.Get, $"/film/{filmSlug}/"))
                {
                    SetNavigationHeaders(filmRequest.Headers, "same-origin", $"https://letterboxd.com/tmdb/{tmdbid}");
                    using (var filmRes = await client.SendAsync(filmRequest).ConfigureAwait(false))
                    {
                        if (!filmRes.IsSuccessStatusCode)
                        {
                            var body = await filmRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (body.Length > 300) body = body.Substring(0, 300);
                            throw new Exception($"Film page lookup returned {(int)filmRes.StatusCode} for https://letterboxd.com/film/{filmSlug}/. Body: " + body);
                        }

                        var filmHtml = await filmRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var filmDoc = new HtmlDocument();
                        filmDoc.LoadHtml(filmHtml);

                        HtmlNode? elForId =
                            filmDoc.DocumentNode.SelectSingleNode($"//div[@data-film-slug='{filmSlug}']") ??
                            filmDoc.DocumentNode.SelectSingleNode($"//div[@data-item-link='/film/{filmSlug}/']") ??
                            filmDoc.DocumentNode.SelectSingleNode("//div[@data-film-id]");

                        if (elForId == null)
                            throw new Exception("The search returned no results (No html element found to get letterboxd filmId)");

                        string filmId = elForId.GetAttributeValue("data-film-id", string.Empty);

                        if (string.IsNullOrEmpty(filmId))
                            throw new Exception("The search returned no results (data-film-id attribute is empty)");

                        return new FilmResult(filmSlug, filmId);
                    }
                }
            }
        }
    }

    public async Task<FilmResult> SearchFilmByImdbId(string imdbid)
    {
        await Task.Delay(500 + Random.Shared.Next(500)).ConfigureAwait(false);

        var imdbPath = $"/imdb/{imdbid}";

        using (var searchRequest = new HttpRequestMessage(HttpMethod.Get, imdbPath))
        {
            SetNavigationHeaders(searchRequest.Headers, "same-origin");

            using (var res = await client.SendAsync(searchRequest).ConfigureAwait(false))
            {
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 300) body = body.Substring(0, 300);
                    throw new Exception($"IMDb lookup returned {(int)res.StatusCode} for https://letterboxd.com/imdb/{imdbid}. Body: " + body);
                }

                var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);

                string filmUrl = htmlDoc.DocumentNode
                    .SelectSingleNode("//link[@rel='canonical']")
                    ?.GetAttributeValue("href", string.Empty) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(filmUrl))
                {
                    var a = htmlDoc.DocumentNode.SelectSingleNode("//a[starts-with(@href, '/film/')]");
                    var href = a?.GetAttributeValue("href", string.Empty) ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(href))
                        filmUrl = href.StartsWith("/") ? "https://letterboxd.com" + href : href;
                }

                if (string.IsNullOrWhiteSpace(filmUrl))
                    throw new Exception($"The search returned no results (Could not resolve film URL from IMDb page).");

                var filmUri = new Uri(filmUrl, UriKind.Absolute);
                var segments = filmUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length < 2 || !segments[0].Equals("film", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"IMDb page resolved to non-film URL: '{filmUrl}'");

                string filmSlug = segments[1];

                // Reuse the same logic to get filmId from slug
                return await SearchFilmBySlug(filmSlug).ConfigureAwait(false);
            }
        }
    }

    private async Task<FilmResult> SearchFilmBySlug(string filmSlug)
    {
        using (var filmRequest = new HttpRequestMessage(HttpMethod.Get, $"/film/{filmSlug}/"))
        {
            SetNavigationHeaders(filmRequest.Headers, "same-origin");
            using (var filmRes = await client.SendAsync(filmRequest).ConfigureAwait(false))
            {
                if (!filmRes.IsSuccessStatusCode)
                    throw new Exception($"Film page lookup failed for {filmSlug}");

                var filmHtml = await filmRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                var filmDoc = new HtmlDocument();
                filmDoc.LoadHtml(filmHtml);

                HtmlNode? elForId = filmDoc.DocumentNode.SelectSingleNode("//div[@data-film-id]");
                if (elForId == null) throw new Exception("No data-film-id found");

                string filmId = elForId.GetAttributeValue("data-film-id", string.Empty);
                return new FilmResult(filmSlug, filmId);
            }
        }
    }

    public async Task MarkAsWatched(string filmSlug, string filmId, DateTime? date, string[] tags, bool liked = false)
    {
        string url = "/s/save-diary-entry";
        DateTime viewingDate = date == null ? DateTime.Now : (DateTime)date;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await RefreshCsrfCookieAsync().ConfigureAwait(false);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Referrer = new Uri($"https://letterboxd.com/film/{filmSlug}/");
                request.Headers.TryAddWithoutValidation("Origin", "https://letterboxd.com");
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
                request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
                request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
                request.Headers.Remove("sec-fetch-user");
                request.Headers.Remove("upgrade-insecure-requests");
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/javascript"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.01));

                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "__csrf", this.csrf },
                    { "json", "true" },
                    { "viewingId", string.Empty },
                    { "filmId", filmId },
                    { "specifiedDate", "true" },
                    { "viewingDateStr", viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                    { "review", string.Empty },
                    { "tags", tags.Length > 0 ? $"[{string.Join(",", tags)}]" : string.Empty },
                    { "rating", "0" },
                    { "liked", liked.ToString().ToLowerInvariant() }
                });

                using (var response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        if (response.StatusCode == HttpStatusCode.Forbidden) throw new Exception("403 Forbidden during diary entry");
                        throw new Exception($"Letterboxd returned {(int)response.StatusCode}");
                    }

                    using (JsonDocument doc = JsonDocument.Parse(body))
                    {
                        var json = doc.RootElement;
                        if (SuccessOperation(json, out string message)) return;

                        if (attempt == 0 && message.Contains("expired", StringComparison.OrdinalIgnoreCase)) continue;
                        throw new Exception(message);
                    }
                }
            }
        }
        throw new Exception("Failed to submit diary entry after retry.");
    }

    public async Task<DateTime?> GetDateLastLog(string filmSlug)
    {
        string url = $"/{this.username}/film/{filmSlug}/diary/";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        SetNavigationHeaders(request.Headers, "same-origin");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var responseHtml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(responseHtml);

        var monthElements = htmlDoc.DocumentNode.SelectNodes("//a[contains(@class, 'month')]");
        var dayElements = htmlDoc.DocumentNode.SelectNodes("//a[contains(@class, 'date') or contains(@class, 'daydate')]");
        var yearElements = htmlDoc.DocumentNode.SelectNodes("//a[contains(@class, 'year')]");

        var lstDates = new List<DateTime>();

        if (monthElements != null && dayElements != null && yearElements != null)
        {
            var minCount = Math.Min(Math.Min(monthElements.Count, dayElements.Count), yearElements.Count);

            for (int i = 0; i < minCount; i++)
            {
                var month = monthElements[i].InnerText?.Trim();
                var day = dayElements[i].InnerText?.Trim();
                var year = yearElements[i].InnerText?.Trim();

                if (!string.IsNullOrEmpty(month) && !string.IsNullOrEmpty(day) && !string.IsNullOrEmpty(year))
                {
                    var dateString = $"{day} {month} {year}";
                    if (DateTime.TryParse(dateString, out DateTime parsedDate))
                        lstDates.Add(parsedDate);
                }
            }
        }

        return lstDates.Count > 0 ? lstDates.Max() : null;
    }

    private bool SuccessOperation(JsonElement json, out string message)
    {
        message = string.Empty;
        if (json.TryGetProperty("messages", out JsonElement messagesElement))
        {
            StringBuilder sb = new StringBuilder();
            foreach (var i in messagesElement.EnumerateArray()) sb.Append(i.GetString());
            message = sb.ToString();
        }

        if (json.TryGetProperty("result", out JsonElement statusElement))
        {
            if (statusElement.ValueKind == JsonValueKind.String) return statusElement.GetString() != "error";
            if (statusElement.ValueKind == JsonValueKind.True) return true;
        }
        return false;
    }

    private static string? ExtractHiddenInput(string html, string name)
    {
        var pattern = $@"<input[^>]*\bname\s*=\s*[""']{Regex.Escape(name)}[""'][^>]*\bvalue\s*=\s*[""']([^""']*)[""'][^>]*>";
        var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }
}

public class FilmResult
{
    public string filmSlug { get; set; }
    public string filmId { get; set; }

    public FilmResult(string slug, string id)
    {
        filmSlug = slug;
        filmId = id;
    }
}
