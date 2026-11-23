using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Connector.Core.Cli;
using Connector.Core.Output;
using Wisdom.Excel;
using Wisdom.Markdown;
using Wisdom.Office;
using Wisdom.Office.Core;
using Wisdom.Word;

return CliHost.Run(() => OfficeCli.Run(args));

internal static class OfficeCli
{
    private const string Usage =
        "用法: file-office info    <文件>                         格式 / 元数据 / 页表数\n" +
        "      file-office text    <文件> [-o out.txt]             纯文本\n" +
        "      file-office md      <文件> [-o out.md] [--keep-images]\n" +
        "      file-office convert <输入> <输出>                   按输出扩展名转换\n" +
        "      file-office fill    <模板 docx|xlsx> <vars.json> -o <输出>\n" +
        "\n" +
        "支持: docx / xlsx / pptx / pdf / md（读写）；doc / xls（只读）\n" +
        "选项:\n" +
        "  -o, --output <file>   写文件（text / md / fill 默认 stdout，convert 必须给输出）\n" +
        "      --keep-images     md 保留内联 base64 图片（默认替换成 ![image]）\n" +
        "      --notes           PPT 转文档时带上演讲者备注";

    private static readonly OptionDef[] Options =
    [
        OptionDef.Value("-o", "--output"),
        OptionDef.Flag("--keep-images"),
        OptionDef.Flag("--notes"),
    ];

    private static readonly string[] ImagePattern = [@"!\[[^\]]*\]\(data:image/[^)]+\)"];

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            ConsoleOut.Line(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var rest = args[1..];

        return args[0] switch
        {
            "info" => Info(rest),
            "text" => Text(rest),
            "md" => MarkdownCommand(rest),
            "convert" => Convert(rest),
            "fill" => Fill(rest),
            _ => Unknown(args[0]),
        };
    }

    private static int Unknown(string command)
    {
        ConsoleOut.Error($"未知子命令: {command}");
        ConsoleOut.Error(Usage);
        return 1;
    }

    private static int Info(string[] args)
    {
        if (!TryParse(args, out var parsed) || parsed.Positionals.Count < 1)
        {
            return 1;
        }

        return Guard(() =>
        {
            var path = parsed.Positionals[0];
            var file = LoadFile(path);
            var text = file.ExtractText();

            var info = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["file"] = Path.GetFileName(path),
                ["format"] = file.Format.ToString(),
                ["textLength"] = text.Length,
            };

            PutIf(info, "title", file.Metadata.Title);
            PutIf(info, "author", file.Metadata.Author);
            PutIf(info, "creator", file.Metadata.Creator);

            if (file.Pdf is { } pdf)
            {
                info["pages"] = pdf.PageCount;
                info["encrypted"] = pdf.IsEncrypted;
            }

            if (file.Workbook is { } workbook)
            {
                info["sheets"] = workbook.Worksheets.Count;
                info["sheetNames"] = workbook.Worksheets.Select(s => s.Name).ToList();
                info["lastRow"] = workbook.Worksheets.Max(s => s.LastRow);
            }

            if (file.Presentation is { } presentation)
            {
                info["slides"] = presentation.Slides.Count;
                info["slideWidth"] = presentation.SlideWidth;
                info["slideHeight"] = presentation.SlideHeight;
            }

            if (file.Document is { } document)
            {
                info["sections"] = document.Sections.Count;
                info["blocks"] = document.Sections.Sum(s => s.Blocks.Count);
            }

            ConsoleOut.Block(JsonText.Value(info));
            return 0;
        });
    }

    private static int Text(string[] args)
    {
        if (!TryParse(args, out var parsed) || parsed.Positionals.Count < 1)
        {
            return 1;
        }

        return Guard(() =>
        {
            var text = LoadFile(parsed.Positionals[0]).ExtractText();
            return Emit(text, parsed.Get("-o"));
        });
    }

    private static int MarkdownCommand(string[] args)
    {
        if (!TryParse(args, out var parsed) || parsed.Positionals.Count < 1)
        {
            return 1;
        }

        return Guard(() =>
        {
            var file = LoadFile(parsed.Positionals[0]);
            var markdown = ToMarkdownText(file, parsed.Has("--notes"));

            if (!parsed.Has("--keep-images"))
            {
                markdown = Regex.Replace(markdown, ImagePattern[0], "![image]");
            }

            return Emit(markdown, parsed.Get("-o"));
        });
    }

    private static int Convert(string[] args)
    {
        if (!TryParse(args, out var parsed) || parsed.Positionals.Count < 2)
        {
            ConsoleOut.Error(Usage);
            return 1;
        }

        return Guard(() =>
        {
            var sourcePath = parsed.Positionals[0];
            var targetPath = parsed.Positionals[1];
            var targetFormat = FormatOf(targetPath)
                ?? throw new UserErrorException($"不支持的输出扩展名: {Path.GetExtension(targetPath)}");

            var targetDir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                throw new UserErrorException($"输出目录不存在: {targetDir}");
            }

            ConsoleOut.Info($"读取 {Path.GetFileName(sourcePath)} ...");
            var file = LoadFile(sourcePath);

            if (targetFormat == OfficeFileFormat.Markdown)
            {
                var text = ToMarkdownText(file, parsed.Has("--notes"));

                if (!parsed.Has("--keep-images"))
                {
                    text = Regex.Replace(text, ImagePattern[0], "![image]");
                }

                File.WriteAllText(targetPath, text);
            }
            else if (targetFormat == OfficeFileFormat.Pdf)
            {
                if (file.Workbook is { } workbook)
                {
                    OfficeConverter.WorkbookToPdf(workbook, targetPath);
                }
                else if (file.Presentation is { } presentation)
                {
                    OfficeConverter.PresentationToPdf(presentation, targetPath);
                }
                else
                {
                    OfficeConverter.ToPdf(ToDocument(file, parsed.Has("--notes")), targetPath);
                }
            }
            else if (targetFormat == OfficeFileFormat.Docx)
            {
                if (file.Workbook is { } workbook)
                {
                    OfficeConverter.ToWord(WorkbookToDocumentConverter.Convert(workbook), targetPath);
                }
                else if (file.Presentation is { } presentation)
                {
                    OfficeConverter.ToWord(
                        PresentationToDocumentConverter.Convert(presentation, parsed.Has("--notes")),
                        targetPath);

                    ConsoleOut.Info("提示: PPT → Word 只保留文本结构，版式与图表不保留");
                }
                else
                {
                    OfficeConverter.ToWord(ToDocument(file, parsed.Has("--notes")), targetPath);
                }
            }
            else if (targetFormat == OfficeFileFormat.Pptx)
            {
                OfficeConverter.ToPptx(DocumentToPresentationConverter.Convert(ToDocument(file, parsed.Has("--notes"))), targetPath);
            }
            else if (targetFormat == OfficeFileFormat.Xlsx)
            {
                if (file.Workbook is null)
                {
                    throw new UserErrorException("只有电子表格可以输出为 xlsx（Word / PDF → Excel 不支持）");
                }

                file.Save(targetPath, OfficeFileFormat.Xlsx);
            }
            else if (targetFormat == file.Format)
            {
                file.Save(targetPath);
            }
            else
            {
                throw new UserErrorException($"{file.Format} → {targetFormat} 不支持");
            }

            ConsoleOut.Info($"✓ 已写出 {targetPath}（{new FileInfo(targetPath).Length / 1024} KB）");
            return 0;
        });
    }

    private static int Fill(string[] args)
    {
        if (!TryParse(args, out var parsed) || parsed.Positionals.Count < 2)
        {
            ConsoleOut.Error("用法: file-office fill <模板 docx|xlsx> <vars.json> -o <输出>");
            return 1;
        }

        return Guard(() =>
        {
            var templatePath = parsed.Positionals[0];
            var valuesPath = parsed.Positionals[1];
            var outputPath = parsed.Get("-o")
                ?? throw new UsageException("fill 需要 -o <输出>");

            if (!File.Exists(templatePath))
            {
                throw new UserErrorException($"模板不存在: {templatePath}");
            }

            if (!File.Exists(valuesPath))
            {
                throw new UserErrorException($"变量文件不存在: {valuesPath}");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(valuesPath));
            var values = ToObject(document.RootElement) as Dictionary<string, object?>
                ?? throw new UserErrorException("vars.json 顶层必须是对象");

            var context = new TemplateContext();

            foreach (var pair in values)
            {
                context[pair.Key] = pair.Value;
            }

            var options = new TemplateOptions
            {
                LeaveUnresolvedTokens = false,
                HtmlEncodeValues = false,
                Culture = CultureInfo.InvariantCulture,
            };

            switch (Path.GetExtension(templatePath).ToLowerInvariant())
            {
                case ".docx":
                    DocxTemplate.Fill(templatePath, outputPath, context, options);
                    break;
                case ".xlsx":
                    XlsxTemplate.Fill(templatePath, outputPath, context, options);
                    break;
                default:
                    throw new UserErrorException("模板只支持 .docx / .xlsx");
            }

            ConsoleOut.Info($"✓ 已写出 {outputPath}（{new FileInfo(outputPath).Length / 1024} KB）");
            return 0;
        });
    }

    private static OfficeFile LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new UserErrorException($"文件不存在: {path}");
        }

        var format = OfficeFileFormats.Detect(path);

        if (format == OfficeFileFormat.Unknown)
        {
            // 内容嗅探不认识纯文本类 Markdown，按扩展名兜底
            if (Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown")
            {
                return OfficeFile.FromDocument(Markdown.LoadFile(path));
            }

            throw new UserErrorException($"无法识别的文件格式: {path}");
        }

        return format == OfficeFileFormat.Markdown
            ? OfficeFile.FromDocument(Markdown.LoadFile(path))
            : OfficeFile.Load(path);
    }

    private static OfficeDocument ToDocument(OfficeFile file, bool includeNotes)
    {
        if (file.Document is { } document)
        {
            return document;
        }

        if (file.Workbook is { } workbook)
        {
            return file.AsDocument();
        }

        if (file.Pdf is { } pdf)
        {
            return pdf.ToOfficeDocument();
        }

        if (file.Presentation is { } presentation)
        {
            return PresentationToDocumentConverter.Convert(presentation, includeNotes);
        }

        throw new UserErrorException($"格式 {file.Format} 无法转换为文档模型");
    }

    private static string ToMarkdownText(OfficeFile file, bool includeNotes)
    {
        if (file.Document is { } document)
        {
            return OfficeConverter.ToMarkdown(document);
        }

        if (file.Workbook is { } workbook)
        {
            return OfficeConverter.ToMarkdown(WorkbookToDocumentConverter.Convert(workbook));
        }

        return OfficeConverter.ToMarkdown(ToDocument(file, includeNotes));
    }

    private static OfficeFileFormat? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => OfficeFileFormat.Pdf,
        ".docx" => OfficeFileFormat.Docx,
        ".xlsx" => OfficeFileFormat.Xlsx,
        ".pptx" => OfficeFileFormat.Pptx,
        ".md" or ".markdown" => OfficeFileFormat.Markdown,
        _ => null,
    };

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ToObject(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.ToString(),
    };

    private static void PutIf(Dictionary<string, object?> map, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            map[key] = value;
        }
    }

    private static int Emit(string text, string? outputPath)
    {
        if (outputPath is null)
        {
            ConsoleOut.Block(text.TrimEnd('\n'));
            return 0;
        }

        File.WriteAllText(outputPath, text);
        ConsoleOut.Info($"✓ 已写出 {outputPath}（{text.Length} 字符）");
        return 0;
    }

    private static bool TryParse(string[] args, out CliArgs parsed)
    {
        try
        {
            parsed = CliArgs.Parse(args, Options);
            return true;
        }
        catch (UsageException error)
        {
            ConsoleOut.Error(error.Message);
            ConsoleOut.Error(Usage);
            parsed = null!;
            return false;
        }
    }

    private static int Guard(Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception error)
        {
            ConsoleOut.Error(CliHost.Describe(error));
            return 1;
        }
    }
}
