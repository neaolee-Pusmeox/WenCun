using WenCun.Core.Domain;
using WenCun.Desktop.Infrastructure;

namespace WenCun.Desktop.ViewModels;

public sealed class ArticleRow(Article article, PendingArticle? pending = null) : ObservableObject
{
    private bool _selected;

    public Article Article { get; } = article;
    public PendingArticle? Pending { get; } = pending;
    public bool IsPending => Pending is not null;
    public bool IsNormal => !IsPending;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Title => string.IsNullOrWhiteSpace(Article.Title) ? "（未命名文章）" : Article.Title;
    public string Account => string.IsNullOrWhiteSpace(Article.Account) ? "未命名公众号" : Article.Account;
    public string Date => Article.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "日期未知";
    public string Meta => Account + " · " + Date;
    public string StateText => IsPending ? "待采集" : Article.State switch
    {
        ContentState.Ready => "已保存",
        ContentState.Partial => "部分正文",
        ContentState.Failed => "采集失败",
        _ => "待采集"
    };
    public bool IsFailed => !IsPending && Article.State == ContentState.Failed;
    public bool IsSaved => !IsPending && Article.State is ContentState.Ready or ContentState.Partial;
    public string ErrorText => IsPending
        ? Pending?.LastError ?? ""
        : Article.State == ContentState.Failed
            ? Article.LastError ?? "上次采集失败，未记录原因；可点击重试。"
            : "";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public string ExportText => IsPending ? "" : Article.ExportState switch
    {
        ExportState.Succeeded => "已导出",
        ExportState.Failed => "导出失败",
        _ => ""
    };
    public bool HasExport => !IsPending && Article.ExportState != ExportState.None;
    public bool HasExportError => !IsPending && Article.ExportState == ExportState.Failed;
    public string ExportErrorText => Article.ExportError ?? "";
}

public sealed class AccountRow(Account account, int pending = 0)
{
    public Account Account { get; } = account;
    public int Pending { get; } = pending;
    public string Name => string.IsNullOrWhiteSpace(Account.Name) ? Account.Biz : Account.Name;
    public string Counts => Account.Count + " 篇";
    public string Stats => $"已保存 {Account.Saved} · 采集失败 {Account.Failed} · 已导出 {Account.Exported} · 导出失败 {Account.ExportFailed} · 待采集 {Pending}";
    public string LatestText => Account.Latest is { } latest ? "最近文章 " + latest.ToLocalTime().ToString("yyyy-MM-dd") : "还没有文章";
    public string CollectedText => Account.LastCollected is { } time ? "上次采集 " + time.ToLocalTime().ToString("MM-dd HH:mm") : "尚未采集";
    public string StatusText => Account.Complete ? "目录已完整" : "目录未完成";
    public bool HasFailed => Account.Failed > 0;
    public bool HasExportFailed => Account.ExportFailed > 0;
    public bool HasPending => Pending > 0;
}
