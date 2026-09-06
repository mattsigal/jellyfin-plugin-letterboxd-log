using System;

namespace LetterboxdLog;

public class DiaryEntryResult
{
    public DiaryEntryResult(string viewingId, DateTime date, int? rating)
    {
        ViewingId = viewingId;
        Date = date;
        Rating = rating;
    }

    public string ViewingId { get; set; }

    public DateTime Date { get; set; }

    public int? Rating { get; set; }
}
