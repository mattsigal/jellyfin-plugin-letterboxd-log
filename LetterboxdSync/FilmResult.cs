namespace LetterboxdLog;

public class FilmResult
{
    public FilmResult(string slug, string id)
    {
        FilmSlug = slug;
        FilmId = id;
    }

    public string FilmId { get; set; }

    public string FilmSlug { get; set; }
}
