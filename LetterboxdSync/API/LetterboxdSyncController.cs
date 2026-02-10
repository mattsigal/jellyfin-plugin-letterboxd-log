using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LetterboxdSync.API;

[ApiController]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class LetterboxdSyncController : ControllerBase
{
    private static readonly object _configLock = new();

    [HttpGet("Jellyfin.Plugin.LetterboxdSync/ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var resourceName = $"{typeof(Plugin).Namespace}.Web.plugin.js";
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    [HttpPost("Jellyfin.Plugin.LetterboxdSync/Authenticate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> Authenticate([FromBody] Account body)
    {
        var api = new LetterboxdApi();
        try
        {
            // If user provided pre-solved cookies (e.g., cf_clearance), inject them before attempting auth
            if (!string.IsNullOrWhiteSpace(body.CookiesRaw))
            {
                api.SetRawCookies(body.CookiesRaw!);
            }

            await api.Authenticate(body.UserLetterboxd, body.PasswordLetterboxd).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex)
        {
            return Unauthorized(new { Message = ex.Message });
        }
    }

    [HttpGet("Jellyfin.Plugin.LetterboxdSync/UserConfig")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<Account> GetUserConfig()
    {
        var userId = GetCurrentUserId();
        var userIdStr = userId.ToString("N");

        var accounts = Plugin.Instance!.Configuration.Accounts;
        var account = accounts.FirstOrDefault(a =>
            string.Equals(a.UserJellyfin, userIdStr, StringComparison.OrdinalIgnoreCase));

        return Ok(account ?? new Account { UserJellyfin = userIdStr });
    }

    [HttpPost("Jellyfin.Plugin.LetterboxdSync/UserConfig")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult SaveUserConfig([FromBody] Account body)
    {
        var userId = GetCurrentUserId();
        var userIdStr = userId.ToString("N");

        // Force the UserJellyfin to the authenticated user's ID for security
        body.UserJellyfin = userIdStr;

        lock (_configLock)
        {
            var config = Plugin.Instance!.Configuration;
            config.Accounts.RemoveAll(a =>
                string.Equals(a.UserJellyfin, userIdStr, StringComparison.OrdinalIgnoreCase));
            config.Accounts.Add(body);
            Plugin.Instance.SaveConfiguration();
        }

        return Ok();
    }

    [HttpPost("Jellyfin.Plugin.LetterboxdSync/UserAuthenticate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> UserAuthenticate([FromBody] Account body)
    {
        var api = new LetterboxdApi();
        try
        {
            if (!string.IsNullOrWhiteSpace(body.CookiesRaw))
            {
                api.SetRawCookies(body.CookiesRaw!);
            }

            await api.Authenticate(body.UserLetterboxd, body.PasswordLetterboxd).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex)
        {
            return Unauthorized(new { Message = ex.Message });
        }
    }

    private Guid GetCurrentUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId");
        if (claim != null && Guid.TryParse(claim.Value, out var userId))
        {
            return userId;
        }

        throw new UnauthorizedAccessException();
    }
}
