using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using PyPackager.Models;

namespace PyPackager.Services;

/// <summary>
/// 打包历史持久化。以 JSON 存于 %LOCALAPPDATA%\PyPackager\history.json，
/// 最新的记录排在最前。
/// </summary>
public sealed class HistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private List<BuildRecord> _records = new();

    public IReadOnlyList<BuildRecord> Records
    {
        get { lock (_lock) return _records.ToList(); }
    }

    public HistoryService()
    {
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(AppPaths.HistoryFile))
                {
                    var json = File.ReadAllText(AppPaths.HistoryFile);
                    _records = JsonSerializer.Deserialize<List<BuildRecord>>(json) ?? new List<BuildRecord>();
                }
                else
                {
                    _records = new List<BuildRecord>();
                }
            }
            catch
            {
                _records = new List<BuildRecord>();
            }
        }
    }

    public void Add(BuildRecord record)
    {
        lock (_lock)
        {
            _records.Insert(0, record);
            // 只保留最近 200 条
            if (_records.Count > 200)
            {
                _records = _records.Take(200).ToList();
            }
        }
        Save();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _records.Clear();
        }
        Save();
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureRoot();
            List<BuildRecord> snapshot;
            lock (_lock) snapshot = _records.ToList();
            File.WriteAllText(AppPaths.HistoryFile, JsonSerializer.Serialize(snapshot, JsonOpts));
        }
        catch
        {
            // 历史写入失败不应影响打包主流程
        }
    }
}
