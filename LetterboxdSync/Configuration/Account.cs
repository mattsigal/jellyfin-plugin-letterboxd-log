using System;

namespace LetterboxdSync.Configuration;

public class Account
{
    public string? UserJellyfin { get; set; }

    public string? UserLetterboxd { get; set; }

    public string? PasswordLetterboxd { get; set; }

    public bool Enable { get; set; }

    public bool SendFavorite { get; set; }

    public bool EnableDateFilter { get; set; }

    public int DateFilterDays { get; set; } = 7;

    public int TimezoneOffset { get; set; } = 0;

    public string? Cookie { get; set; }

    public string? UserAgent { get; set; }

    public string? CookiesRaw { get; set; }

    public string? CookiesUserAgent { get; set; }
}
