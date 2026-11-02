using System.Text.Json;

namespace WenCun.Capture;

public sealed record CaptureJournalEntry(
    ProxySettings Original,
    string AppliedServer,
    DateTimeOffset StartedAt);

/// <summary>崩溃后下次启动恢复网络；长期证书由用户单独管理。</summary>
public static class CaptureJournal
{
    public static string PathFor(string appRoot) => Path.Combine(appRoot, "capture-journal.json");

    public static void Write(string path, CaptureJournalEntry entry)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entry);
                stream.Flush(flushToDisk: true);
            }
            // Never replace another capture's recovery state, including after a crash.
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static CaptureJournalEntry? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CaptureJournalEntry>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>返回 null 表示无需恢复；否则返回未能自动完成的说明。</summary>
    public static string? Recover(string appRoot) => Recover(appRoot, SystemProxy.Read, SystemProxy.Write);

    internal static string? Recover(string appRoot, Func<ProxySettings> readProxy, Action<ProxySettings> writeProxy)
    {
        var path = PathFor(appRoot);
        if (!File.Exists(path)) return null;
        try
        {
            var entry = Read(path);
            const string prefix = "https=127.0.0.1:";
            if (entry?.Original is not { Server: not null, Bypass: not null, AutoConfigUrl: not null } ||
                entry.AppliedServer is null || !entry.AppliedServer.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(entry.AppliedServer[prefix.Length..], out var port) || port is < 1 or > 65535)
                return "代理恢复记录损坏，已保留记录；请检查系统代理设置。";
            var current = readProxy();
            var restored = current.RestoreOriginal(entry.Original, ProxySettings.ForCapture(port, entry.Original.Bypass));
            if (restored != current) writeProxy(restored);
            Delete(path);
            return null;
        }
        catch (Exception ex) { return "恢复代理失败：" + ex.Message; }
    }
}
