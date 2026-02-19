using System;
using System.Net.Mime;
using System.Threading.Tasks;
using LetterboxdLog.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LetterboxdLog.API;

[ApiController]
[Produces(MediaTypeNames.Application.Json)]
// [Authorize(Policy = Policies.SubtitleManagement)]
public class LetterboxdLogController : ControllerBase
{
    [HttpPost("Jellyfin.Plugin.LetterboxdLog/Authenticate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> Authenticate([FromBody] Account body)
    {
        var api = new LetterboxdApi(body.CookiesUserAgent ?? body.UserAgent ?? string.Empty);
        try
        {
            api.SetRawCookies(body.CookiesRaw ?? body.Cookie);
            await api.Authenticate(body.UserLetterboxd ?? string.Empty, body.PasswordLetterboxd ?? string.Empty).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex)
        {
            return Unauthorized(new { Message = ex.Message });
        }
    }
}
