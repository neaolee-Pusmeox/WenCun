namespace WenCun.Core.Domain;

public enum ContentState { NotFetched, Ready, Partial, Failed }

public enum ExportFormat { Html, Markdown, Text, Word, Pdf }

public enum ImageMode { Download, Link, None }

public enum ExportState { None, Succeeded, Failed }

public enum FailureKind { Input, Session, RateLimited, Network, Protocol, Unavailable, Storage, Renderer }

public sealed class WenCunException(FailureKind kind, string message) : Exception(message)
{
    public FailureKind Kind { get; } = kind;
}

public sealed record Article
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public string Biz { get; init; } = "";
    public string Account { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public int Index { get; init; } = 1;
    public DateTimeOffset? PublishedAt { get; init; }
    public ContentState State { get; init; }
    public string? LastError { get; init; }
    public ExportState ExportState { get; init; }
    public string? ExportError { get; init; }
    public DateTimeOffset? ExportedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record Asset(string Source, string Kind, string? LocalPath = null, string? Error = null);

public sealed record ArticleContent(
    string ArticleId,
    string BodyHtml,
    DateTimeOffset FetchedAt,
    IReadOnlyList<Asset> Assets,
    IReadOnlyList<string> Warnings);

public sealed record ArticlePage(IReadOnlyList<Article> Articles, int NextOffset, bool HasMore);

public sealed record SaveOptions(IReadOnlyList<ExportFormat> Formats, ImageMode Images)
{
    public static SaveOptions Default { get; } = new([ExportFormat.Html], ImageMode.Download);
}

public sealed record Session
{
    public required string Cookie { get; init; }
    public required string UserAgent { get; init; }
    public required string Biz { get; init; }
    public string Account { get; init; } = "";
    public string AppMsgToken { get; init; } = "";
    public string PassTicket { get; init; } = "";
    public string Parameters { get; init; } = "";
    public string SampleUrl { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record Account(
    string Biz,
    string Name,
    int Count,
    int Saved,
    int Failed,
    int Exported,
    int ExportFailed,
    DateTimeOffset? Latest,
    DateTimeOffset? LastCollected,
    bool Complete);

public sealed record PendingArticle(
    string Id,
    string Biz,
    string Account,
    string Title,
    string Author,
    string Url,
    DateTimeOffset? PublishedAt,
    DateTimeOffset DiscoveredAt,
    string? LastError = null);

public sealed record PendingAccount(
    string Biz,
    string Account,
    int Offset,
    bool Complete,
    DateTimeOffset? LastDiscovered,
    IReadOnlyList<PendingArticle> Articles);

public sealed record LibraryQuery(
    string Search = "",
    string? Biz = null,
    ContentState? State = null,
    int Page = 1,
    int PageSize = 50,
    bool Descending = true);

public sealed record LibraryPage(IReadOnlyList<Article> Items, int Total, int Page, int PageSize);

public sealed record CollectProgress(string Stage, int Pages, int Found, int Saved, string Detail);

public sealed record ExportRequest(
    IReadOnlyList<Article> Articles,
    string Directory,
    IReadOnlyList<ExportFormat> Formats);

public sealed record ExportItem(string ArticleId, string Title, ExportFormat Format, bool Ok, string? Path, string? Error);

public sealed record ExportResult(IReadOnlyList<ExportItem> Items)
{
    public int Succeeded => Items.Count(i => i.Ok);
    public int Failed => Items.Count(i => !i.Ok);
}
