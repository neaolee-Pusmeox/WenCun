using WenCun.Core.Domain;

namespace WenCun.Desktop.ViewModels;

public interface ILibraryActions
{
    bool IsBusy { get; }
    void Read(Article article);
    Task RetryAsync(Article article);
    Task RetryAsync(IReadOnlyList<Article> articles);
    Task ReexportAsync(IReadOnlyList<Article> articles);
    Task SavePendingAsync(IReadOnlyList<PendingArticle> articles);
    void DeletePending(IReadOnlyList<PendingArticle> articles);
    void CleanupLegacyPending();
    void OpenFolder(Article article);
}
