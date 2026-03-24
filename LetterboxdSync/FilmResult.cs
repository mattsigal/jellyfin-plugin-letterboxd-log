namespace LetterboxdLog;

public class FilmResult
{
    public FilmResult(string slug, string id, string productionId)
    {
        FilmSlug = slug;
        FilmId = id;
        ProductionId = productionId;
    }

    public string FilmId { get; set; }

    public string FilmSlug { get; set; }

    public string ProductionId { get; set; }
}
