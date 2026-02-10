using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace LetterboxdSync;

public static class IndexHtmlTransformer
{
    private const string ScriptTag = "<script plugin=\"LetterboxdSync\" src=\"/Jellyfin.Plugin.LetterboxdSync/ClientScript\" defer></script>";

    public static async Task TransformIndexHtml(string path, Stream contents)
    {
        if (!path.EndsWith("index.html"))
        {
            return;
        }

        contents.Position = 0;
        string html;
        using (var reader = new StreamReader(contents, Encoding.UTF8, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (html.Contains("plugin=\"LetterboxdSync\""))
        {
            return;
        }

        html = html.Replace("</body>", $"{ScriptTag}\n</body>");

        contents.Position = 0;
        contents.SetLength(0);
        using (var writer = new StreamWriter(contents, Encoding.UTF8, leaveOpen: true))
        {
            await writer.WriteAsync(html).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }
}
