using System;
using System.Net.Mime;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LetterboxdSync.API;

[ApiController]
[Produces(MediaTypeNames.Application.Json)]
//[Authorize(Policy = Policies.SubtitleManagement)]
public class LetterboxdSyncController : ControllerBase
{
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
}
