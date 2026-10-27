using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kscan.Core.Configuration;

/// <summary>配置读写。抽象出来是为了让单测能换成内存实现，也便于将来换存储格式。</summary>
public interface ISettingsStore
{
    /// <summary>配置文件路径（展示给用户看，便于排查）。</summary>
    string FilePath { get; }

    /// <summary>最近一次读取失败的原因；成功为 <c>null</c>。</summary>
    string? LastError { get; }

    /// <summary>读取配置：文件不存在、为空、损坏、无权限 —— 一律回退默认值，不抛异常。</summary>
    AppSettings Load();

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>把配置存成 JSON 文件（人可读、可手工改）。</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public JsonSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    public string? LastError { get; private set; }

    public AppSettings Load()
    {
        LastError = null;

        try
        {
            if (!File.Exists(FilePath))
            {
                return AppSettings.Default;
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return AppSettings.Default;
            }

            // 未知字段被忽略、缺失字段用记录类型的默认值 —— 两种情况都不需要额外处理。
            // 旧配置文件里的 4 个标量热键字段在这里一次性搬进 Hotkeys 列表（读出来就是新形态），
            // 版本迁移（补默认热键）也一并在这里完成。
            return (JsonSerializer.Deserialize<AppSettings>(json, Options) ?? AppSettings.Default).Migrate();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return AppSettings.Default;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写同目录的临时文件再替换：中途失败也不会留下半截 JSON，
        // 下次启动读到的要么是旧配置、要么是新配置（File.Move + overwrite 是原子替换）。
        var json = JsonSerializer.Serialize(settings, Options);
        var tempPath = FilePath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, FilePath, overwrite: true);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败无关紧要，不要盖住真正的异常
        }
    }
}
