using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kscan.Core.Imaging;
using Kscan.Core.Model;
using Kscan.Core.Pipeline;
using Kscan.Platform;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        kscan — 跨平台二维码/条码扫描与生成（命令行）

        用法:
          kscan decode <图片>... [--single] [--json] [--verbose]   识别图片中的码（"-" = 从 stdin 读）
          kscan gen <文本> [输出路径] [生成选项]                    生成条码（见下方选项）
          kscan build <类型> [字段...] [生成选项]                   用模板拼内容再生成（见下方模板）
          kscan formats                                           列出可生成的码制
          kscan capture [--region|--desktop] [--scan] [-o 文件]     截图（Linux 走 grim+slurp）
          kscan toggle [--capture]                                把命令交给已运行的 GUI（无实例时退出码 3）
          kscan version
          kscan doctor                                            显示配置、单实例端点与平台能力（JSON）
          kscan help

        管道用法（Linux 截图工具可以直接接进来）:
          flameshot gui -r | kscan decode -
          kscan capture --region --scan
          kscan gen "https://example.com" -o - | wl-copy

        截图（kscan capture）:
          kscan capture --region --scan             框选后直接识别并打印结果
          kscan capture --region -o shot.png        框选后存成文件
          kscan capture --desktop -o screen.png     整个虚拟桌面存成文件
          kscan capture --region --scan --notify    识别后发系统通知（Linux/notify-send）
          Windows 命令行下没有框选通道，--region 请改用 GUI；Linux 需要 grim + slurp。

        生成选项:
          -f, --format <码制>    默认 QR_CODE（kscan formats 可查看全部）
          -s, --size <像素>      宽度，默认 320（2D 码同时作为高度基准）
              --height <像素>    高度，默认 0 = 自动（2D 方形，1D 取 120）
          -m, --margin <模块数>  静区，默认 4
              --ecc <L|M|Q|H>    QR 纠错级别，默认 M
              --fg <颜色>        前景色，默认 #000000
              --bg <颜色>        背景色，默认 #FFFFFF
          -o, --output <路径>    输出路径；写 "-" 则输出到 stdout（--ascii 时可省略）
              --svg              输出 SVG 矢量图
              --logo <图片>      在中心叠加 Logo（仅 PNG；建议配 --ecc H）
              --logo-scale <比例> Logo 宽度占比，默认 0.22
          -a, --ascii            输出等宽字符画（终端可直接扫）
              --ascii-style <half|blocks|hash|chars>   字符画风格，默认 half
              --chars <深色字符> 配合 --ascii-style chars
              --ascii-rows <行数> 字符画每行重复次数（1D 码默认 8 行，才够高可扫）
              --invert           字符画反色（浅色终端）

        内容模板（kscan build <类型>）:
          wifi   --ssid <名> [--password <密码>] [--security WPA|WEP|nopass] [--hidden]
          vcard  --name <姓名> [--org <单位>] [--title <职务>] [--tel <电话>] [--email <邮箱>] [--url <网址>] [--note <备注>]
          url    <网址>
          email  --to <地址> [--subject <主题>] [--body <正文>]
          tel    <号码>
          sms    --to <号码> [--message <内容>]
          geo    <纬度> <经度> [--label <标签>]

          示例:
            kscan build wifi --ssid MyWiFi --password 12345678 -o wifi.png
            kscan build vcard --name 张三 --tel +8613800138000 -o zhang.png
            kscan build url example.com -o site.svg --svg
            kscan build geo 39.9042 116.4074 --label 天安门 -o map.png

        退出码: 0 = 成功，1 = 未识别到码，2 = 参数错误或读写失败
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string[] args)
    {
        TryEnableUtf8Console();

        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 2;
        }

        return args[0] switch
        {
            "decode" or "d" => Decode(args[1..]),
            "gen" or "g" => Gen(args[1..]),
            "build" or "b" => Build(args[1..]),
            "capture" or "c" => Capture(args[1..]),
            "toggle" or "t" => Toggle(args[1..]),
            "formats" => Formats(),
            "version" or "--version" or "-v" => Version(),
            "doctor" => Doctor(),
            "help" or "--help" or "-h" => Help(),
            _ => Unknown(args[0]),
        };
    }

    /// <summary>
    /// 把命令交给"已经跑着的 GUI"（单实例 IPC）。没有实例时<b>不</b>自己去启动 ——
    /// CLI 猜不出 GUI 装在哪，交给用户/合成器决定更靠谱。
    /// </summary>
    private static int Toggle(string[] args)
    {
        var capture = args.Any(a => a is "--capture" or "-c");
        var command = capture ? "capture-region" : "activate";

        var sent = PlatformServices.SingleInstance.SendAsync(command).GetAwaiter().GetResult();

        if (sent)
        {
            Console.WriteLine(capture
                ? "已交给正在运行的 Kscan 去框选截图。"
                : "已激活正在运行的 Kscan 窗口。");
            return 0;
        }

        Console.Error.WriteLine("没有正在运行的 Kscan 实例：先启动 GUI（或在 niri 里把 binds 指向它）。");
        return 3;
    }

    private static int Doctor()
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            portable = PlatformPaths.IsPortable,
            configFile = PlatformPaths.SettingsFile,
            ipcEndpoint = PlatformServices.SingleInstance.Endpoint,
            capabilities = PlatformServices.Detect(),
        }, JsonOptions));
        return 0;
    }

    private static void TryEnableUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 非交互或已重定向时忽略
        }
    }

    private static int Help()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    private static int Version()
    {
        // 优先用 InformationalVersion（形如 0.1.0，与 pacman 包的 pkgver 一致）；
        // 它可能带 "+<commit>" 后缀（CI 里会加），只取版本号部分。
        var assembly = typeof(Cli).Assembly;
        var informational = assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Console.WriteLine(informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString() ?? "0.0.0");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static int Formats()
    {
        Console.WriteLine("可生成的码制（读取端支持更多）：");
        Console.WriteLine();
        Console.WriteLine("  二维：");
        foreach (var format in BarcodeFormats.TwoDimensional)
        {
            Console.WriteLine($"    {format,-12} {BarcodeFormats.DisplayName(format)}");
        }

        Console.WriteLine();
        Console.WriteLine("  一维：");
        foreach (var format in BarcodeFormats.OneDimensional)
        {
            Console.WriteLine($"    {format,-12} {BarcodeFormats.DisplayName(format)}");
        }

        return 0;
    }

    /// <summary>
    /// 截图。Linux 上委托外部命令（默认 <c>grim -g "$(slurp)" -</c>）；Windows 命令行下只有整屏 BitBlt，
    /// 框选留给 GUI。加 <c>--scan</c> 就是"截图 → 识别 → 打印（可选通知）"一条龙。
    /// </summary>
    private static int Capture(string[] args)
    {
        var region = false;
        var desktop = false;
        string? output = null;
        var scan = false;
        var notify = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--region" or "-r":
                    region = true;
                    break;
                case "--desktop":
                    desktop = true;
                    break;
                case "--scan":
                    scan = true;
                    break;
                case "--notify":
                    notify = true;
                    break;
                case "-o" or "--output":
                    output = RequireValue(args, ref i, args[i]);
                    break;
                default:
                    Console.Error.WriteLine($"未知参数：{args[i]}");
                    return 2;
            }
        }

          if (region && desktop)
          {
              Console.Error.WriteLine("--region 与 --desktop 不能同时指定。");
              return 2;
          }
          if (!region && !desktop)
        {
            region = true;   // 默认框选
        }

        var capture = PlatformServices.ScreenCapture;
        if (!capture.IsSupported)
        {
            Console.Error.WriteLine($"当前平台不支持截图：{capture.Description}");
            return 2;
        }

        ImageFrame? frame;
        try
        {
            frame = region
                ? capture.CaptureRegionAsync().GetAwaiter().GetResult()
                : capture.CaptureDesktopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"截图出错：{ex.Message}");
            return 2;
        }

        if (frame is null)
        {
            if (region && OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Windows 命令行下没有框选通道：请改用 --desktop，或在 GUI 里框选。");
            }
            else
            {
                  Console.Error.WriteLine(capture.LastError ?? $"截图失败或被取消。（{capture.Description}）");
            }

            return 2;
        }

        if (!scan)
        {
            var png = SkiaImageCodec.EncodePng(frame);

            if (output is null || output == "-")
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(png);
            }
            else
            {
                File.WriteAllBytes(output, png);
                Console.Error.WriteLine($"已写入 {output}（{frame.Width}x{frame.Height}）");
            }

            return 0;
        }

        var outcome = ScanPipeline.CreateDefault().Scan(frame);
        if (!outcome.Success)
        {
            Console.Error.WriteLine("截图里没有识别到码。");
            return 1;
        }

        var text = outcome.FirstText!;
        Console.WriteLine(text);

        if (notify)
        {
            var content = ResultContent.Parse(text);
            PlatformServices.Notification
                .ShowAsync($"识别到{content.KindLabel}", content.Title ?? text)
                .GetAwaiter().GetResult();
        }

        return 0;
    }

    private static int Decode(string[] args)
    {
        var files = new List<string>();
        var multiCode = true;
        var json = false;
        var verbose = false;

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--single":
                    multiCode = false;
                    break;
                case "--multi":
                    multiCode = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "-":
                    files.Add(arg);   // 从 stdin 读
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"未知参数：{arg}");
                        return 2;
                    }

                    files.Add(arg);
                    break;
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("用法：kscan decode <图片>... [--single] [--json] [--verbose]（\"-\" = 从 stdin 读）");
            return 2;
        }

        var pipeline = ScanPipeline.CreateDefault();
        var options = new ScanOptions { MultiCode = multiCode };
        var exitCode = 1;

        foreach (var file in files)
        {
            var fromStdin = file == "-";
            if (!fromStdin && !File.Exists(file))
            {
                Console.Error.WriteLine($"文件不存在：{file}");
                exitCode = 2;
                continue;
            }

            var source = fromStdin ? "<stdin>" : file;

            ImageFrame frame;
            try
            {
                // stdin 直接读原始 PNG/JPEG 字节：flameshot -r、grim、spectacle 等都能管道进来
                frame = fromStdin
                    ? SkiaImageCodec.FromEncoded(Console.OpenStandardInput())
                    : SkiaImageCodec.Load(file);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"读取失败：{source}：{ex.Message}");
                exitCode = 2;
                continue;
            }

            var outcome = pipeline.Scan(frame, options);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    file = source,
                    width = frame.Width,
                    height = frame.Height,
                    success = outcome.Success,
                    totalMs = outcome.TotalMs,
                    results = outcome.Results.Select(r => new
                    {
                        text = r.Text,
                        format = r.Format,
                        engine = r.EngineId,
                        center = r.Center is { } c ? new { x = Math.Round(c.X, 2), y = Math.Round(c.Y, 2) } : null,
                    }),
                    attempts = outcome.Attempts.Select(a => new
                    {
                        engine = a.EngineId,
                        succeeded = a.Succeeded,
                        elapsedMs = a.ElapsedMs,
                        error = a.Error,
                    }),
                }, JsonOptions));
            }
            else if (outcome.Success)
            {
                foreach (var result in outcome.Results)
                {
                    Console.WriteLine(result.Text);
                }
            }
            else
            {
                Console.Error.WriteLine($"未识别到码：{source}");
            }

            if (verbose)
            {
                Console.Error.WriteLine(
                    $"[verbose] {source}: {frame.Width}x{frame.Height}, 用时 {outcome.TotalMs}ms, "
                    + string.Join(", ", outcome.Attempts.Select(a => $"{a.EngineId}={(a.Succeeded ? "命中" : "未命中")}/{a.ElapsedMs}ms")));
            }

            if (outcome.Success)
            {
                exitCode = 0;
            }
        }

        return exitCode;
    }

    /// <summary>
    /// 内容模板：先用 <see cref="BarcodeContent"/> 把字段拼成标准 payload，再把"剩下的参数"
    /// 原样交给 <see cref="Gen"/> —— 于是 <c>-o</c> / <c>-f</c> / <c>--svg</c> / 输出路径这些
    /// 生成选项不必重复实现一遍。
    /// </summary>
    private static int Build(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：kscan build <wifi|vcard|url|email|tel|sms|geo> [字段...] [生成选项]");
            Console.Error.WriteLine("（kscan help 里有完整示例）");
            return 2;
        }

        var kind = args[0].ToLowerInvariant();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var positional = new List<string>();
        var passthrough = new List<string>();

        // 只用长选项：短选项（-s / -m / -p / -t）在 gen 里另有含义，混用会产生歧义
        string[] knownValues = kind switch
        {
            "wifi" => ["--ssid", "--password", "--security"],
            "vcard" or "contact" => ["--name", "--org", "--title", "--tel", "--email", "--url", "--note"],
            "email" or "mail" => ["--to", "--subject", "--body"],
            "sms" => ["--to", "--message"],
            "geo" => ["--label"],
            "url" or "link" => ["--url"],
            "tel" or "phone" => ["--number"],
            _ => [],
        };
        string[] knownFlags = kind switch
        {
            "wifi" => ["--hidden"],
            _ => [],
        };
        var positionalNeeded = kind switch
        {
            "url" or "link" or "tel" or "phone" => 1,
            "geo" => 2,
            _ => 0,
        };

        try
        {
            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                if (knownValues.Contains(arg))
                {
                    values[arg] = RequireValue(args, ref i, arg);
                }
                else if (knownFlags.Contains(arg))
                {
                    flags.Add(arg);
                }
                else if (!arg.StartsWith('-') && positional.Count < positionalNeeded)
                {
                    positional.Add(arg);
                }
                else
                {
                    passthrough.Add(arg);
                }
            }

            var payload = kind switch
            {
                "wifi" => BarcodeContent.Wifi(new WifiCredentials(
                    Get("--ssid") ?? throw new ArgumentException("缺少 --ssid"),
                    Get("--password"),
                    Get("--security") ?? "WPA",
                    flags.Contains("--hidden"))),

                "vcard" or "contact" => BarcodeContent.VCard(new VCardFields(
                    Get("--name") ?? throw new ArgumentException("缺少 --name"),
                    Get("--org"),
                    Get("--title"),
                    Get("--tel"),
                    Get("--email"),
                    Get("--url"),
                    Get("--note"))),

                "url" or "link" => BarcodeContent.Url(
                    positional.FirstOrDefault() ?? Get("--url") ?? throw new ArgumentException("缺少 URL")),

                "email" or "mail" => BarcodeContent.Email(new EmailFields(
                    Get("--to") ?? throw new ArgumentException("缺少 --to"),
                    Get("--subject"),
                    Get("--body"))),

                "tel" or "phone" => BarcodeContent.Phone(
                    positional.FirstOrDefault() ?? Get("--number") ?? throw new ArgumentException("缺少号码")),

                "sms" => BarcodeContent.Sms(new SmsFields(
                    Get("--to") ?? throw new ArgumentException("缺少 --to"),
                    Get("--message"))),

                "geo" => BarcodeContent.Geo(new GeoPoint(
                    ParseCoordinate(positional.ElementAtOrDefault(0), "纬度", 90),
                    ParseCoordinate(positional.ElementAtOrDefault(1), "经度", 180),
                    Get("--label"))),

                _ => throw new ArgumentException($"未知的内容类型：{kind}（可选 wifi / vcard / url / email / tel / sms / geo）"),
            };

            // 没给输出选项时只打印 payload：方便先看一眼或直接复制；要出图就加 -o / --ascii
            var wantsOutput = passthrough.Any(a => a is "-o" or "--output" or "-a" or "--ascii");
            if (!wantsOutput)
            {
                Console.WriteLine(payload);
                return 0;
            }

            return Gen([payload, .. passthrough]);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or ArgumentOutOfRangeException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        string? Get(string key) => values.TryGetValue(key, out var value) ? value : null;
    }

    private static double ParseCoordinate(string? text, string name, double limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException($"缺少{name}（用法：kscan build geo <纬度> <经度>）");
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"{name}不是有效数字：{text}");
        }

        if (Math.Abs(value) > limit)
        {
            throw new ArgumentOutOfRangeException(name, $"{name}必须在 -{limit} 到 {limit} 之间。");
        }

        return value;
    }

    private static int Gen(string[] args)
    {
        var positional = new List<string>();
        string? output = null;
        string? logoPath = null;
        var logoScale = LogoOverlay.DefaultRatio;
        var format = "QR_CODE";
        var width = 320;
        var height = 0;
        var margin = 4;
        var errorCorrection = "M";
        var foreground = Colors.Black;
        var background = Colors.White;
        var svg = false;
        var ascii = false;
        var asciiStyle = AsciiBarcodeStyle.HalfBlocks;
        var onChar = '\u2588';
        var asciiRows = 0;   // 0 = 自动（2D 取 1，1D 取 8）
        var invert = false;

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "-f" or "--format":
                        format = RequireValue(args, ref i, arg);
                        break;
                    case "-s" or "--size":
                        width = ParseInt(RequireValue(args, ref i, arg), arg, 16, 4096);
                        break;
                    case "--height":
                        height = ParseInt(RequireValue(args, ref i, arg), arg, 0, 4096);
                        break;
                    case "-m" or "--margin":
                        margin = ParseInt(RequireValue(args, ref i, arg), arg, 0, 64);
                        break;
                    case "--ecc":
                        errorCorrection = RequireValue(args, ref i, arg);
                        break;
                    case "--fg":
                        foreground = Colors.Parse(RequireValue(args, ref i, arg));
                        break;
                    case "--bg":
                        background = Colors.Parse(RequireValue(args, ref i, arg));
                        break;
                    case "-o" or "--output":
                        output = RequireValue(args, ref i, arg);
                        break;
                    case "--svg":
                        svg = true;
                        break;
                    case "--logo":
                        logoPath = RequireValue(args, ref i, arg);
                        break;
                    case "--logo-scale":
                        logoScale = double.Parse(RequireValue(args, ref i, arg), NumberStyles.Float, CultureInfo.InvariantCulture);
                        if (logoScale is < 0.02 or > 0.6)
                        {
                            throw new ArgumentException("--logo-scale 需要在 0.02-0.6 之间");
                        }

                        break;
                    case "-a" or "--ascii":
                        ascii = true;
                        break;
                    case "--ascii-style":
                        asciiStyle = ParseAsciiStyle(RequireValue(args, ref i, arg));
                        break;
                    case "--ascii-rows":
                        asciiRows = ParseInt(RequireValue(args, ref i, arg), arg, 1, 64);
                        break;
                    case "--chars":
                        var chars = RequireValue(args, ref i, arg);
                        if (chars.Length == 0)
                        {
                            throw new ArgumentException("--chars 需要至少一个字符");
                        }

                        onChar = chars[0];
                        asciiStyle = AsciiBarcodeStyle.Custom;
                        break;
                    case "--invert":
                        invert = true;
                        break;
                    default:
                        if (arg.StartsWith('-'))
                        {
                            Console.Error.WriteLine($"未知参数：{arg}");
                            return 2;
                        }

                        positional.Add(arg);
                        break;
                }
            }

            var text = positional.Count > 0 ? positional[0] : null;
            if (output is null && positional.Count > 1)
            {
                output = positional[1];
            }

            if (string.IsNullOrEmpty(text))
            {
                Console.Error.WriteLine("用法：kscan gen <文本> [输出路径] [--format QR_CODE] [--svg] [--ascii]");
                return 2;
            }

            if (!BarcodeEncoder.TryParseFormat(format, out _))
            {
                Console.Error.WriteLine($"不支持的码制：{format}。可用：{string.Join(", ", BarcodeFormats.Writable)}");
                return 2;
            }

            if (BarcodeEncoder.ValidateContent(format, text) is { } problem)
            {
                Console.Error.WriteLine(problem);
                return 2;
            }

            if (logoPath is not null && svg)
            {
                Console.Error.WriteLine("--logo 只能叠加到 PNG（SVG 是矢量输出），请去掉 --svg 或改用 PNG。");
                return 2;
            }

            var request = new BarcodeGenerationRequest
            {
                Text = text,
                Format = format,
                Width = width,
                Height = height,
                Margin = margin,
                ErrorCorrection = errorCorrection,
                ForegroundArgb = foreground,
                BackgroundArgb = background,
            };

            if (ascii)
            {
                if (logoPath is not null)
                {
                    Console.Error.WriteLine("提示：--logo 对字符画无效，已忽略。");
                }

                // 字符画必须用模块级矩阵：Encode 返回的是按像素尺寸渲染过的矩阵，
                // 直接画字符画会得到"一屏放不下"的结果
                var rows = asciiRows > 0
                    ? asciiRows
                    : BarcodeFormats.IsTwoDimensional(format) ? 1 : 8;   // 1D 码只有 1 行模块，重复才够高

                var art = BarcodeRenderer.ToAscii(BarcodeEncoder.EncodeModules(request), new AsciiRenderOptions
                {
                    Style = asciiStyle,
                    Margin = 0,
                    OnChar = onChar,
                    Invert = invert,
                    RowRepeat = rows,
                });

                if (output is null || output == "-")
                {
                    Console.Write(art);
                }
                else
                {
                    File.WriteAllText(output, art);
                    Console.Error.WriteLine($"已写入 {output}");
                }

                return 0;
            }

            var matrix = BarcodeEncoder.Encode(request);

            // Logo 叠加：先渲染成像素帧，再把 logo 合成到中心
            ImageFrame? composed = null;
            if (logoPath is not null)
            {
                var logo = SkiaImageCodec.Load(logoPath);
                composed = LogoOverlay.Apply(BarcodeRenderer.ToImageFrame(matrix, request), logo, logoScale);
            }

            // "-o -" 把二进制写到 stdout，便于 kscan gen ... -o - | wl-copy 这类管道
            if (output is null)
            {
                Console.Error.WriteLine("需要输出路径（或用 --ascii 直接打印字符画）");
                return 2;
            }

            if (output == "-")
            {
                if (svg)
                {
                    Console.Out.Write(BarcodeRenderer.ToSvg(matrix, request));
                }
                else
                {
                    using var stdout = Console.OpenStandardOutput();
                    stdout.Write(composed is null ? BarcodeRenderer.ToPng(matrix, request) : SkiaImageCodec.EncodePng(composed));
                }

                return 0;
            }

            if (svg || output.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(output, BarcodeRenderer.ToSvg(matrix, request));
            }
            else if (composed is not null)
            {
                File.WriteAllBytes(output, SkiaImageCodec.EncodePng(composed));
            }
            else
            {
                BarcodeRenderer.SavePng(matrix, request, output);
            }

            var extra = logoPath is null ? "" : "，已叠加 Logo";
            Console.WriteLine($"已写入 {output}（{BarcodeFormats.DisplayName(format)}，{matrix.Width}x{matrix.Height} 模块{extra}）");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} 需要一个值");
        }

        return args[++index];
    }

    private static int ParseInt(string value, string option, int min, int max)
    {
        if (!int.TryParse(value, out var parsed) || parsed < min || parsed > max)
        {
            throw new ArgumentException($"{option} 需要 {min}-{max} 之间的整数，实际给出：{value}");
        }

        return parsed;
    }

    private static AsciiBarcodeStyle ParseAsciiStyle(string value) => value.ToLowerInvariant() switch
    {
        "half" or "halfblocks" or "half-blocks" => AsciiBarcodeStyle.HalfBlocks,
        "blocks" or "block" => AsciiBarcodeStyle.Blocks,
        "hash" => AsciiBarcodeStyle.Hash,
        "chars" or "custom" => AsciiBarcodeStyle.Custom,
        _ => throw new ArgumentException($"未知字符画风格：{value}（可选 half/blocks/hash/chars）"),
    };
}
