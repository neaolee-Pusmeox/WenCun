using System.Globalization;
using Microsoft.Data.Sqlite;
using WenCun.Core.Content;
using WenCun.Core.Domain;
using WenCun.Core.Export;

namespace WenCun.Core.Storage;

/// <summary>一个文件夹就是一座资料库：library.db 存索引与正文，assets 存图片，articles 存导出文件。</summary>
public sealed class Library
{
    private const string Schema = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS articles(
            id TEXT PRIMARY KEY,
            biz TEXT NOT NULL,
            account TEXT NOT NULL,
            title TEXT NOT NULL,
            author TEXT NOT NULL,
            url TEXT NOT NULL,
            article_index INTEGER NOT NULL DEFAULT 1,
            published TEXT,
            state INTEGER NOT NULL,
            last_error TEXT,
            export_state INTEGER NOT NULL DEFAULT 0,
            export_error TEXT,
            exported TEXT,
            updated TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS idx_articles_biz ON articles(biz, published DESC);
        CREATE INDEX IF NOT EXISTS idx_articles_published ON articles(published DESC, id);
        CREATE TABLE IF NOT EXISTS contents(
            article_id TEXT PRIMARY KEY REFERENCES articles(id) ON DELETE CASCADE,
            body TEXT NOT NULL,
            fetched TEXT NOT NULL,
            warnings TEXT NOT NULL,
            assets TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS accounts(
            biz TEXT PRIMARY KEY,
            name TEXT NOT NULL DEFAULT '',
            offset INTEGER NOT NULL DEFAULT 0,
            complete INTEGER NOT NULL DEFAULT 0,
            last_collected TEXT,
            updated TEXT NOT NULL);
        """;

    private readonly string _connectionString;

    public Library(string rootDirectory)
    {
        RootDirectory = FileBoundary.Normalize(rootDirectory);
        AssetsDirectory = Path.Combine(RootDirectory, "assets");
        DatabasePath = Path.Combine(RootDirectory, "library.db");
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 15
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
        Migrate(connection);
    }

    public string RootDirectory { get; }
    public string DatabasePath { get; }
    public string AssetsDirectory { get; }

    private static void Migrate(SqliteConnection connection)
    {
        AddMissing(connection, "articles", "article_index", "INTEGER NOT NULL DEFAULT 1");
        AddMissing(connection, "articles", "last_error", "TEXT");
        AddMissing(connection, "articles", "export_state", "INTEGER NOT NULL DEFAULT 0");
        AddMissing(connection, "articles", "export_error", "TEXT");
        AddMissing(connection, "articles", "exported", "TEXT");
        AddMissing(connection, "accounts", "last_collected", "TEXT");
    }

    private static void AddMissing(SqliteConnection connection, string table, string column, string definition)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        if (columns.Contains(column)) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public (int Added, int Updated) UpsertArticles(IEnumerable<Article> articles)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        int added = 0, updated = 0;
        foreach (var article in articles)
        {
            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM articles WHERE id=$id";
            exists.Parameters.AddWithValue("$id", article.Id);
            var known = exists.ExecuteScalar() is not null;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = known
                ? """
                  UPDATE articles SET biz=$biz,
                    account=CASE WHEN $account='' THEN account ELSE $account END,
                    title=CASE WHEN $title='' THEN title ELSE $title END,
                    author=CASE WHEN $author='' THEN author ELSE $author END,
                    url=$url, article_index=$index, published=COALESCE($published, published),
                    state=CASE WHEN $state=0 THEN state ELSE $state END, updated=$updated
                  WHERE id=$id
                  """
                : """
                  INSERT INTO articles(id,biz,account,title,author,url,article_index,published,state,updated)
                  VALUES($id,$biz,$account,$title,$author,$url,$index,$published,$state,$updated)
                  """;
            command.Parameters.AddWithValue("$id", article.Id);
            command.Parameters.AddWithValue("$biz", article.Biz);
            command.Parameters.AddWithValue("$account", article.Account);
            command.Parameters.AddWithValue("$title", article.Title);
            command.Parameters.AddWithValue("$author", article.Author);
            command.Parameters.AddWithValue("$url", article.Url);
            command.Parameters.AddWithValue("$index", article.Index);
            command.Parameters.AddWithValue("$published", article.PublishedAt is { } published ? Stamp(published) : DBNull.Value);
            command.Parameters.AddWithValue("$state", (int)article.State);
            command.Parameters.AddWithValue("$updated", Stamp(article.UpdatedAt));
            command.ExecuteNonQuery();
            if (known) updated++; else added++;
        }
        transaction.Commit();
        return (added, updated);
    }

    public Article? GetArticle(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectArticles + " WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadArticle(reader) : null;
    }

    public ArticleContent? GetContent(string articleId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body,fetched,warnings,assets FROM contents WHERE article_id=$id";
        command.Parameters.AddWithValue("$id", articleId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ArticleContent(
            articleId,
            reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Json.Deserialize<List<Asset>>(reader.GetString(3)) ?? [],
            Json.Deserialize<List<string>>(reader.GetString(2)) ?? []);
    }

    public void SaveContent(ArticleContent content, ContentState state)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO contents(article_id,body,fetched,warnings,assets)
                VALUES($id,$body,$fetched,$warnings,$assets)
                ON CONFLICT(article_id) DO UPDATE SET body=excluded.body, fetched=excluded.fetched,
                    warnings=excluded.warnings, assets=excluded.assets
                """;
            command.Parameters.AddWithValue("$id", content.ArticleId);
            command.Parameters.AddWithValue("$body", content.BodyHtml);
            command.Parameters.AddWithValue("$fetched", Stamp(content.FetchedAt));
            command.Parameters.AddWithValue("$warnings", Json.Serialize(content.Warnings));
            command.Parameters.AddWithValue("$assets", Json.Serialize(content.Assets));
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE articles SET state=$state, last_error=NULL, updated=$updated WHERE id=$id";
            command.Parameters.AddWithValue("$state", (int)state);
            command.Parameters.AddWithValue("$updated", Stamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", content.ArticleId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SetState(string articleId, ContentState state)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE articles SET state=$state, last_error=CASE WHEN $state=3 THEN last_error ELSE NULL END, updated=$updated WHERE id=$id";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$updated", Stamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", articleId);
        command.ExecuteNonQuery();
    }

    public void SetFailure(string articleId, string error)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE articles SET state=3, last_error=$error, updated=$updated WHERE id=$id";
        command.Parameters.AddWithValue("$error", Trim(error));
        command.Parameters.AddWithValue("$updated", Stamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", articleId);
        command.ExecuteNonQuery();
    }

    public void SetExportResult(string articleId, ExportState state, string? error)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE articles SET export_state=$state, export_error=$error, exported=$exported WHERE id=$id";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$error", error is null ? DBNull.Value : Trim(error));
        command.Parameters.AddWithValue("$exported", state == ExportState.Succeeded ? Stamp(DateTimeOffset.UtcNow) : DBNull.Value);
        command.Parameters.AddWithValue("$id", articleId);
        command.ExecuteNonQuery();
    }

    public LibraryPage Query(LibraryQuery query)
    {
        var where = new List<string>();
        var args = new List<(string Name, object Value)>();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("(title LIKE $search ESCAPE '\\' OR account LIKE $search ESCAPE '\\' OR author LIKE $search ESCAPE '\\')");
            args.Add(("$search", "%" + EscapeLike(query.Search.Trim()) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(query.Biz))
        {
            where.Add("biz=$biz");
            args.Add(("$biz", query.Biz));
        }
        if (query.State is { } state)
        {
            where.Add("state=$state");
            args.Add(("$state", (int)state));
        }
        var clause = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        using var connection = Open();
        int total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM articles" + clause;
            foreach (var (name, value) in args) count.Parameters.AddWithValue(name, value);
            total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        using var command = connection.CreateCommand();
        command.CommandText = SelectArticles + clause +
            $" ORDER BY published IS NULL, published {(query.Descending ? "DESC" : "ASC")}, id LIMIT $limit OFFSET $offset";
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);
        var items = new List<Article>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(ReadArticle(reader));
        return new LibraryPage(items, total, page, pageSize);
    }

    public IReadOnlyList<Article> FailedArticles(string? biz = null, int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectArticles + (biz is null ? " WHERE state=3" : " WHERE biz=$biz AND state=3") +
            " ORDER BY published IS NULL, published DESC, id LIMIT $limit";
        if (biz is not null) command.Parameters.AddWithValue("$biz", biz);
        command.Parameters.AddWithValue("$limit", limit);
        var items = new List<Article>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(ReadArticle(reader));
        return items;
    }

    public IReadOnlyList<Article> ExportFailedArticles(string? biz = null, int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectArticles + (biz is null ? " WHERE export_state=2" : " WHERE biz=$biz AND export_state=2") +
            " ORDER BY published IS NULL, published DESC, id LIMIT $limit";
        if (biz is not null) command.Parameters.AddWithValue("$biz", biz);
        command.Parameters.AddWithValue("$limit", limit);
        var items = new List<Article>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(ReadArticle(reader));
        return items;
    }

    public IReadOnlyList<Account> Accounts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(a.biz, x.biz) AS biz,
                   COALESCE(NULLIF(a.name,''), MAX(NULLIF(x.account,'')), COALESCE(a.biz, x.biz)) AS name,
                   COUNT(x.id) AS count,
                   COALESCE(SUM(CASE WHEN x.state IN (1,2) THEN 1 ELSE 0 END), 0) AS saved,
                   COALESCE(SUM(CASE WHEN x.state=3 THEN 1 ELSE 0 END), 0) AS failed,
                   COALESCE(SUM(CASE WHEN x.export_state=1 THEN 1 ELSE 0 END), 0) AS exported,
                   COALESCE(SUM(CASE WHEN x.export_state=2 THEN 1 ELSE 0 END), 0) AS export_failed,
                   MAX(x.published) AS latest,
                   a.last_collected,
                   COALESCE(a.complete, 0) AS complete
            FROM accounts a LEFT JOIN articles x ON x.biz = a.biz
            GROUP BY a.biz
            UNION ALL
            SELECT x.biz, MAX(NULLIF(x.account,'')), COUNT(*),
                   SUM(CASE WHEN x.state IN (1,2) THEN 1 ELSE 0 END),
                   SUM(CASE WHEN x.state=3 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN x.export_state=1 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN x.export_state=2 THEN 1 ELSE 0 END),
                   MAX(x.published), NULL, 0
            FROM articles x WHERE x.biz NOT IN (SELECT biz FROM accounts)
            GROUP BY x.biz
            ORDER BY latest DESC
            """;
        var items = new List<Account>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new Account(
                reader.GetString(0),
                reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : ParseStamp(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseStamp(reader.GetString(8)),
                reader.GetInt32(9) != 0));
        }
        return items;
    }

    public (int Offset, bool Complete) GetCheckpoint(string biz)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT offset, complete FROM accounts WHERE biz=$biz";
        command.Parameters.AddWithValue("$biz", biz);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1) != 0) : (0, false);
    }

    public string? GetAccountName(string biz)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM accounts WHERE biz=$biz";
        command.Parameters.AddWithValue("$biz", biz);
        return command.ExecuteScalar() as string;
    }

    public void SaveCheckpoint(string biz, string name, int offset, bool complete)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts(biz,name,offset,complete,last_collected,updated) VALUES($biz,$name,$offset,$complete,$updated,$updated)
            ON CONFLICT(biz) DO UPDATE SET
                name=CASE WHEN excluded.name='' THEN accounts.name ELSE excluded.name END,
                offset=excluded.offset, complete=excluded.complete,
                last_collected=excluded.last_collected, updated=excluded.updated
            """;
        command.Parameters.AddWithValue("$biz", biz);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$complete", complete ? 1 : 0);
        command.Parameters.AddWithValue("$updated", Stamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    public void EnsureAccount(string biz, string name)
    {
        if (string.IsNullOrWhiteSpace(biz)) return;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts(biz,name,offset,complete,last_collected,updated) VALUES($biz,$name,0,0,NULL,$updated)
            ON CONFLICT(biz) DO UPDATE SET
                name=CASE WHEN excluded.name='' THEN accounts.name ELSE excluded.name END,
                updated=excluded.updated
            """;
        command.Parameters.AddWithValue("$biz", biz);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$updated", Stamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    public void SetAccountName(string biz, string name)
    {
        if (string.IsNullOrWhiteSpace(biz) || string.IsNullOrWhiteSpace(name)) return;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE articles SET account=$name WHERE biz=$biz AND account='';
            UPDATE accounts SET name=$name WHERE biz=$biz;
            """;
        command.Parameters.AddWithValue("$biz", biz);
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    public void DeleteArticles(IEnumerable<string> ids)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (list.Length == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var id in list)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM contents WHERE article_id=$id; DELETE FROM articles WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>删除旧版本遗留在资料库里的“待采集”记录；新待采集清单在 pending JSON。</summary>
    public int DeleteLegacyPending()
    {
        var ids = new List<string>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM articles WHERE state=0";
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        if (ids.Count == 0) return 0;
        DeleteArticles(ids);
        return ids.Count;
    }

    public (int Articles, int Assets, int Exports) DeleteAccountData(string biz)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var articles = new List<Article>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = SelectArticles;
            using var reader = read.ExecuteReader();
            while (reader.Read()) articles.Add(ReadArticle(reader));
        }
        var selected = articles.Where(article => article.Biz == biz).ToArray();
        var articlesRoot = Path.Combine(RootDirectory, "articles");
        var otherLegacyFolders = articles.Where(article => article.Biz != biz)
            .Select(article => Path.GetFullPath(ExportPaths.LegacyFolderFor(articlesRoot, article)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM articles WHERE biz=$biz; DELETE FROM accounts WHERE biz=$biz;";
            command.Parameters.AddWithValue("$biz", biz);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        var assets = 0;
        var exports = 0;
        foreach (var article in selected)
        {
            if (article.Id.Length > 0 && article.Id is not ("." or "..") &&
                article.Id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                FileBoundary.TryDeleteDirectory(AssetsDirectory, Path.Combine(AssetsDirectory, article.Id))) assets++;
            if (FileBoundary.TryDeleteDirectory(articlesRoot, ExportPaths.FolderFor(articlesRoot, article))) exports++;
            var legacy = Path.GetFullPath(ExportPaths.LegacyFolderFor(articlesRoot, article));
            // A display name alone cannot establish ownership of an old directory.
            if (!otherLegacyFolders.Contains(legacy) && FileBoundary.TryDeleteDirectory(articlesRoot, legacy)) exports++;
        }
        return (selected.Length, assets, exports);
    }

    public void CopyTo(string target)
    {
        var full = FileBoundary.Normalize(target);
        if (string.Equals(full, RootDirectory, StringComparison.OrdinalIgnoreCase)) return;
        if (FileBoundary.IsWithin(RootDirectory, full))
            throw new IOException("目标文件夹不能在当前文库内部。");
        FileBoundary.RequireNoLinks(RootDirectory);
        FileBoundary.RequireNoLinks(full);
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new IOException("目标文件夹不是空文件夹。");
        var staging = FileBoundary.TemporarySibling(full);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in FileBoundary.Files(RootDirectory))
            {
                var relative = Path.GetRelativePath(RootDirectory, file);
                if (relative.Equals("library.db", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("library.db-wal", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("library.db-shm", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("library.db-journal", StringComparison.OrdinalIgnoreCase)) continue;
                var destination = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            using (var source = Open())
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(staging, "library.db"), Pooling = false
            }.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }
            // Only publish a complete copy; a newly populated target must fail safely.
            if (Directory.Exists(full)) Directory.Delete(full);
            Directory.Move(staging, full);
        }
        finally { FileBoundary.TryDeleteDirectory(Path.GetDirectoryName(staging)!, staging); }
    }

    private const string SelectArticles =
        "SELECT id,biz,account,title,author,url,published,state,last_error,export_state,export_error,exported,updated,article_index FROM articles";

    private static Article ReadArticle(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Biz = reader.GetString(1),
        Account = reader.GetString(2),
        Title = reader.GetString(3),
        Author = reader.GetString(4),
        Url = reader.GetString(5),
        PublishedAt = reader.IsDBNull(6) ? null : ParseStamp(reader.GetString(6)),
        State = (ContentState)reader.GetInt32(7),
        LastError = reader.IsDBNull(8) ? null : reader.GetString(8),
        ExportState = (ExportState)reader.GetInt32(9),
        ExportError = reader.IsDBNull(10) ? null : reader.GetString(10),
        ExportedAt = reader.IsDBNull(11) ? null : ParseStamp(reader.GetString(11)),
        UpdatedAt = ParseStamp(reader.GetString(12)),
        Index = reader.GetInt32(13)
    };

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string Trim(string value) => value.Length > 500 ? value[..500] : value;

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
