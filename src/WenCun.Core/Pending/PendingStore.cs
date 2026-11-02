using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WenCun.Core.Domain;

namespace WenCun.Core.Pending;

/// <summary>待采集清单：只存公众号源与文章元数据，和已保存的文章分开。</summary>
public sealed class PendingStore(string root)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Directory { get; } = Path.Combine(root, "pending");

    public IReadOnlyList<PendingAccount> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var items = new List<PendingAccount>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                var account = JsonSerializer.Deserialize<PendingAccount>(File.ReadAllText(file), Options);
                if (Valid(account) && string.Equals(Path.GetFullPath(file), Path.GetFullPath(PathFor(account!.Biz)), StringComparison.OrdinalIgnoreCase))
                    items.Add(account!);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 单个损坏文件不影响其他公众号的清单。
            }
        }
        return items.OrderByDescending(account => account.LastDiscovered ?? DateTimeOffset.MinValue).ToArray();
    }

    public PendingAccount? Load(string biz)
    {
        var path = PathFor(biz);
        if (!File.Exists(path)) return null;
        try
        {
            var account = JsonSerializer.Deserialize<PendingAccount>(File.ReadAllText(path), Options);
            if (!Valid(account) || account!.Biz != biz) throw new JsonException();
            return account;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new WenCunException(FailureKind.Storage, "待采集清单无法读取，原文件已保留；请检查清单文件夹。");
        }
    }

    public void Save(PendingAccount account)
    {
        if (!Valid(account)) throw new WenCunException(FailureKind.Storage, "待采集清单结构无效，未写入。");
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(account.Biz);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(account, Options));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public PendingAccount Merge(string biz, string account, IEnumerable<PendingArticle> articles, int offset, bool complete)
    {
        var existing = Load(biz) ?? new PendingAccount(biz, account, 0, false, null, []);
        var map = existing.Articles.ToDictionary(article => article.Id, StringComparer.Ordinal);
        foreach (var article in articles)
        {
            if (map.TryGetValue(article.Id, out var old))
            {
                map[article.Id] = old with
                {
                    Title = string.IsNullOrWhiteSpace(article.Title) ? old.Title : article.Title,
                    Account = string.IsNullOrWhiteSpace(article.Account) ? old.Account : article.Account,
                    Author = string.IsNullOrWhiteSpace(article.Author) ? old.Author : article.Author,
                    PublishedAt = article.PublishedAt ?? old.PublishedAt,
                    Url = article.Url
                };
            }
            else map[article.Id] = article;
        }
        var merged = new PendingAccount(
            biz,
            string.IsNullOrWhiteSpace(account) ? existing.Account : account,
            offset,
            complete,
            DateTimeOffset.UtcNow,
            map.Values.OrderByDescending(article => article.PublishedAt ?? DateTimeOffset.MinValue).ToArray());
        if (!string.IsNullOrWhiteSpace(merged.Account) && merged.Account != biz)
            merged = merged with { Articles = merged.Articles.Select(article => article with { Account = merged.Account }).ToArray() };
        Save(merged);
        return merged;
    }

    public void RemoveArticles(string biz, IEnumerable<string> ids)
    {
        var existing = Load(biz);
        if (existing is null) return;
        var remove = ids.ToHashSet(StringComparer.Ordinal);
        Save(existing with
        {
            Articles = existing.Articles.Where(article => !remove.Contains(article.Id)).ToArray(),
            LastDiscovered = DateTimeOffset.UtcNow
        });
    }

    public void SetError(string biz, string id, string error)
    {
        var existing = Load(biz);
        if (existing is null) return;
        Save(existing with
        {
            Articles = existing.Articles.Select(article => article.Id == id ? article with { LastError = error } : article).ToArray()
        });
    }

    public void SetAccountName(string biz, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var existing = Load(biz);
        if (existing is null) return;
        Save(existing with { Account = name, Articles = existing.Articles.Select(article => article with { Account = name }).ToArray() });
    }

    public void Clear(string biz)
    {
        var existing = Load(biz);
        if (existing is null) return;
        Save(existing with { Articles = [], LastDiscovered = DateTimeOffset.UtcNow });
    }

    public void RemoveAccount(string biz)
    {
        var path = PathFor(biz);
        if (File.Exists(path)) File.Delete(path);
    }

    public string PathFor(string biz) =>
        Path.Combine(Directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(biz)))[..16] + ".json");

    private static bool Valid(PendingAccount? account) =>
        account is not null && !string.IsNullOrWhiteSpace(account.Biz) && account.Account is not null &&
        account.Offset >= 0 && account.Articles is not null &&
        account.Articles.All(article => article is not null && !string.IsNullOrWhiteSpace(article.Id) &&
            article.Biz == account.Biz && article.Account is not null && article.Title is not null &&
            article.Author is not null && !string.IsNullOrWhiteSpace(article.Url)) &&
        account.Articles.Select(article => article.Id).Distinct(StringComparer.Ordinal).Count() == account.Articles.Count;
}
