using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kscan.Core.Pipeline;

/// <summary>
/// <c>user/method/</c> 的读写：一套预处理步骤就是一个「方法」，可以存下来、也能从别人那里导入。
/// <para>
/// 格式（<c>methods.json</c>）：
/// <code>
/// { "version": 1, "methods": [ { "name": "去彩色+对比度", "steps": [ { "kind": "Decolor", ... } ] } ] }
/// </code>
/// 也接受直接是数组的文件（<c>[ { "name": ..., "steps": [...] } ]</c>），方便别人手写或从别处导出。
/// </para>
/// <para>
/// 目录里所有 <c>*.json</c> 都会被加载（默认文件名 <see cref="DefaultFileName"/>）；
/// 解析失败的文件<b>跳过</b>，不影响其它文件 —— 分享来的文件坏了不该让整个功能瘫掉。
/// </para>
/// </summary>
public static class MethodStore
{
    /// <summary>默认追加的文件名（用户拍板：优先全部追加到一个文件里）。</summary>
    public const string DefaultFileName = "methods.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>读出目录里所有方法（按文件名 + 文件内顺序），内容重复的只保留第一个。</summary>
    public static IReadOnlyList<PreprocessRecipe> LoadAll(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var results = new List<PreprocessRecipe>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var file in files)
        {
            foreach (var recipe in ReadFile(file))
            {
                if (seen.Add(recipe.Signature()))
                {
                    results.Add(recipe);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 把一个方法追加进 <c>&lt;directory&gt;/methods.json</c>（内容相同的已存在时不再重复写）。
    /// 返回文件里最终的方法总数；写盘失败返回 <c>-1</c>。
    /// </summary>
    public static int Save(string directory, PreprocessRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return -1;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, DefaultFileName);
            var existing = File.Exists(path) ? ReadFile(path).ToList() : [];

            // 已存在（含从别的文件导入的同名方法）就不重复写
            var signature = recipe.Signature();
            if (existing.Any(existingRecipe => existingRecipe.HasSameStepsAs(recipe)))
            {
                return existing.Count;
            }

            existing.Add(recipe);

            var payload = new MethodsFile { Version = 1, Methods = existing };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
            return existing.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return -1;
        }
    }

    /// <summary>只用作默认方法名：<c>方法 1</c>、<c>方法 2</c>……（取当前目录里已有的数量 +1）。</summary>
    public static string SuggestName(string directory, string format = "方法 {0}")
    {
        var count = LoadAll(directory).Count;
        return string.Format(format, count + 1);
    }

    /// <summary>
    /// 确保目录里有给用户看的分享说明（<c>README.md</c>）。
    /// 已存在就<b>不动</b> —— 用户可能自己改过；与 <c>user/themes/README.md</c> 同样的做法。
    /// </summary>
    public static void EnsureReadme(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "README.md");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, ShareGuide);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 目录不可写（只读部署等）：没有说明文件也不影响功能
        }
    }

    /// <summary>写进 <c>user/method/README.md</c> 的说明：格式、参数、怎么分享。</summary>
    private const string ShareGuide =
        """
        # Kscan 预处理方法（user/method/）

        这个文件夹里的每个 `*.json` 都会被 Kscan 自动加载（启动时，以及打开「手动处理图像」面板时）。
        **文件名随意，后缀必须是 `.json`**。

        ## 什么时候会有文件

        在识别页点「手动处理图像」，逐层调好步骤与参数、确实能读出码之后，点「保存这个方法」——
        默认全部追加到同目录的 `methods.json` 里。

        ## 文件长什么样

        ```json
        {
          "version": 1,
          "methods": [
            {
              "name": "救低对比度",
              "steps": [
                { "kind": "Median", "medianRadius": 2 },
                { "kind": "Contrast", "contrastTailPermille": 25 }
              ]
            }
          ]
        }
        ```

        也可以直接是一个数组（方便手写、也方便只分享一个方法）：

        ```json
        [
          { "name": "只用中值", "steps": [ { "kind": "Median", "medianRadius": 3 } ] }
        ]
        ```

        ## 字段

        - `kind`：`Decolor`（去彩色叠加）/ `Contrast`（对比度拉伸）/ `Median`（中值去噪）/
          `Adaptive`（局部阈值，消阴影）。
        - `steps` 是**有序**的：按数组顺序依次套用，前一步的结果是后一步的输入。
        - 每种 `kind` 只认自己的参数，其余字段可以省略（省略即用默认值）：
          - `Decolor`：`saturationThreshold`（默认 48，越小去掉得越多）
          - `Contrast`：`contrastTailPermille`（默认 5，两端各裁掉的千分比）
          - `Median`：`medianRadius`（默认 1；0 = 不处理）
          - `Adaptive`：`adaptiveRadiusDivisor`（默认 12）、`adaptiveBias`（默认 2）
        - 参数超出可用范围会被夹到边界，不会让程序崩。

        ## 分享与导入

        - 直接把这个 json 文件发给别人，对方放进自己的 `user/method/` 就能在面板里看到。
        - 默认方法都在 `methods.json`；想单独分享某一个，可以新建一个 `xxx.json` 只放那一个。
        - 内容完全相同的重复方法只会出现一次（按步骤与参数判定，与 `name` 无关）。
        - 写坏的 json 会被**跳过**，不影响其它文件 —— 手改出语法错误不会让整个功能失效。
        """;

    private static IEnumerable<PreprocessRecipe> ReadFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        // 先按 { methods: [...] } 读；不是对象就再按 [...] 读。
        // 两件事必须各自 try：对象反序列化遇到数组会抛 JsonException，不能因此放弃数组那条路。
        MethodsFile? file = null;
        List<PreprocessRecipe>? array = null;

        try
        {
            file = JsonSerializer.Deserialize<MethodsFile>(text, Options);
        }
        catch (JsonException)
        {
            // 不是对象，交给下面的数组分支
        }

        if (file?.Methods is null)
        {
            try
            {
                array = JsonSerializer.Deserialize<List<PreprocessRecipe>>(text, Options);
            }
            catch (JsonException)
            {
                yield break;   // 两种都不是：这个文件跳过
            }
        }

        var recipes = file?.Methods ?? array;
        if (recipes is null)
        {
            yield break;
        }

        foreach (var recipe in recipes)
        {
            // 没步骤的方法没有意义（等于"只看原图"），读进来只会污染列表
            if (recipe?.Steps is { Count: > 0 })
            {
                yield return recipe;
            }
        }
    }

    private sealed class MethodsFile
    {
        public int Version { get; set; } = 1;

        public List<PreprocessRecipe>? Methods { get; set; }
    }
}
