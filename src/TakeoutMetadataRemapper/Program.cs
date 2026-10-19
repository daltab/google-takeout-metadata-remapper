using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

const string AppName = "Google Takeout Metadata Remapper";

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    PrintHelp();
    return 0;
}

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
    return SelfTest.Run();

var options = Options.Parse(args);
if (options.Error is not null)
{
    Console.Error.WriteLine($"Error: {options.Error}");
    PrintHelp();
    return 2;
}

if (!Directory.Exists(options.Source))
{
    Console.Error.WriteLine($"Source folder does not exist: {options.Source}");
    return 2;
}

var exifTool = ExifToolClient.ResolveExecutable(options.ExifTool);
if (exifTool is null)
{
    Console.Error.WriteLine("ExifTool was not found. Install it and pass --exiftool <path>, or add it to PATH.");
    return 2;
}

if (options.Apply && string.IsNullOrWhiteSpace(options.TimeZoneId))
{
    Console.Error.WriteLine("--apply requires --time-zone so the UTC Takeout timestamp is converted explicitly.");
    return 2;
}

var scanner = new TakeoutScanner(options);
var scan = scanner.Scan();
Console.WriteLine($"Found {scan.MetadataCount:N0} metadata files; {scan.Paired.Count:N0} paired media items; {scan.Ambiguous.Count:N0} ambiguous; {scan.Missing.Count:N0} missing media; {scan.Excluded.Count:N0} excluded.");

var selected = scan.Paired
    .OrderBy(x => x.MetadataPath, StringComparer.OrdinalIgnoreCase)
    .Take(options.Limit ?? int.MaxValue)
    .ToList();

if (selected.Count == 0)
{
    Console.Error.WriteLine("No eligible paired media items were found.");
    return 1;
}

if (!options.Apply)
    Console.WriteLine("DRY RUN: no output files will be changed. Add --apply to write staged copies.");

Directory.CreateDirectory(options.Output);
var report = new RunReport
{
    App = AppName,
    GeneratedUtc = DateTimeOffset.UtcNow,
    Source = options.Source,
    Output = options.Output,
    Apply = options.Apply,
    Limit = options.Limit,
    TimeZoneId = options.TimeZoneId,
    ExifTool = exifTool,
    Scan = new ScanSummary
    {
        MetadataCount = scan.MetadataCount,
        PairedCount = scan.Paired.Count,
        AmbiguousCount = scan.Ambiguous.Count,
        MissingCount = scan.Missing.Count,
        ExcludedCount = scan.Excluded.Count
    }
};

var writer = new Remapper(exifTool, options);
foreach (var item in selected)
{
    var result = await writer.ProcessAsync(item);
    report.Items.Add(result);
    Console.WriteLine($"{result.Status,-12} {Path.GetFileName(item.MediaPath)} - {result.Message}");
}

report.Summary = new RunSummary
{
    Selected = report.Items.Count,
    Applied = report.Items.Count(x => x.Status == "Applied"),
    DryRun = report.Items.Count(x => x.Status == "DryRun"),
    Skipped = report.Items.Count(x => x.Status == "Skipped"),
    Failed = report.Items.Count(x => x.Status == "Failed"),
    Verified = report.Items.Count(x => x.Verification?.Status == "Verified")
};

var reportPath = options.ReportPath ?? Path.Combine(options.Output, "remap-report.json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
}));
Console.WriteLine($"Report: {reportPath}");
return report.Summary.Failed == 0 ? 0 : 1;

static void PrintHelp()
{
    Console.WriteLine("""
Google Takeout Metadata Remapper

Reads Google Photos .supplemental-metadata.json files and creates staged media copies
with standard EXIF/XMP date and GPS metadata. Originals are never modified.

Usage:
  remapper --source <Takeout\Google Photos> --output <staging> [options]

Options:
  --limit <n>              Process at most n paired media items.
  --apply                  Copy files and write metadata. Without this, dry-run only.
  --time-zone <Windows ID> Required with --apply, e.g. "Eastern Standard Time" or UTC.
  --include-excluded       Include Instagram and Podcast Sharing paths.
  --exiftool <path>        Explicit ExifTool executable path.
  --report <path>          JSON report path; defaults to <output>\remap-report.json.
  --self-test              Run built-in parser and matching tests.
  --help                   Show this help.

Safety:
  The tool never writes to the source tree, never uploads, never deletes, and never
  copies companion JSON files into the output. Review the report before uploading.
""");
}

sealed class Options
{
    public required string Source { get; init; }
    public required string Output { get; init; }
    public int? Limit { get; init; }
    public bool Apply { get; init; }
    public bool IncludeExcluded { get; init; }
    public string? TimeZoneId { get; init; }
    public string? ExifTool { get; init; }
    public string? ReportPath { get; init; }
    public string? Error { get; private init; }

    public static Options Parse(string[] args)
    {
        string? source = null, output = null, timeZone = null, exifTool = null, report = null, error = null;
        int? limit = null;
        var apply = false;
        var includeExcluded = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {arg}");
                return args[++i];
            }

            try
            {
                switch (arg.ToLowerInvariant())
                {
                    case "--source": source = Next(); break;
                    case "--output": output = Next(); break;
                    case "--limit": limit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--apply": apply = true; break;
                    case "--include-excluded": includeExcluded = true; break;
                    case "--time-zone": timeZone = Next(); break;
                    case "--exiftool": exifTool = Next(); break;
                    case "--report": report = Next(); break;
                    default: throw new ArgumentException($"Unknown option: {arg}");
                }
            }
            catch (Exception ex) { error = ex.Message; }
        }

        if (string.IsNullOrWhiteSpace(source)) error ??= "--source is required";
        if (string.IsNullOrWhiteSpace(output)) error ??= "--output is required";
        if (limit is <= 0) error ??= "--limit must be positive";
        return new Options
        {
            Source = Path.GetFullPath(source ?? "."),
            Output = Path.GetFullPath(output ?? "."),
            Limit = limit,
            Apply = apply,
            IncludeExcluded = includeExcluded,
            TimeZoneId = timeZone,
            ExifTool = exifTool,
            ReportPath = report,
            Error = error
        };
    }
}

sealed class TakeoutScanner(Options options)
{
    private static readonly Regex ExcludedPath = new("(^|[\\/])(Instagram|Podcast Sharing)([\\/]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".png", ".webp", ".tif", ".tiff"
    };

    public ScanResult Scan()
    {
        var allMedia = Directory.EnumerateFiles(options.Source, "*", SearchOption.AllDirectories)
            .Where(p => MediaExtensions.Contains(Path.GetExtension(p)))
            .ToList();
        var byRelative = allMedia.ToLookup(p => Path.GetRelativePath(options.Source, p), StringComparer.OrdinalIgnoreCase);
        var byName = allMedia.ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var paired = new List<PairedItem>();
        var ambiguous = new List<string>();
        var missing = new List<string>();
        var excluded = new List<string>();
        var metadataPaths = Directory.EnumerateFiles(options.Source, "*.supplemental-metadata.json", SearchOption.AllDirectories).ToList();

        foreach (var metadataPath in metadataPaths)
        {
            if (!options.IncludeExcluded && ExcludedPath.IsMatch(Path.GetRelativePath(options.Source, metadataPath)))
            {
                excluded.Add(metadataPath);
                continue;
            }

            TakeoutMetadata metadata;
            try { metadata = TakeoutMetadata.Parse(metadataPath); }
            catch { missing.Add(metadataPath); continue; }

            var relativeCandidate = Path.Combine(Path.GetRelativePath(options.Source, Path.GetDirectoryName(metadataPath)!), metadata.Title);
            var sameDirectory = byRelative[relativeCandidate].ToList();
            var candidates = sameDirectory.Count > 0 ? sameDirectory : byName[metadata.Title].ToList();
            if (candidates.Count == 1)
            {
                paired.Add(new PairedItem(metadataPath, candidates[0], metadata));
            }
            else if (candidates.Count == 0)
            {
                missing.Add(metadataPath);
            }
            else
            {
                ambiguous.Add(metadataPath);
            }
        }

        return new ScanResult(metadataPaths.Count, paired, ambiguous, missing, excluded);
    }
}

sealed record ScanResult(int MetadataCount, List<PairedItem> Paired, List<string> Ambiguous, List<string> Missing, List<string> Excluded);
sealed record PairedItem(string MetadataPath, string MediaPath, TakeoutMetadata Metadata);

sealed class TakeoutMetadata
{
    public required string Title { get; init; }
    public required DateTimeOffset TakenUtc { get; init; }
    public required string TimeSource { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? Altitude { get; init; }

    public static TakeoutMetadata Parse(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var title = root.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("Metadata title is missing");
        var (taken, source) = ReadTimestamp(root, "photoTakenTime") ?? ReadTimestamp(root, "creationTime")
            ?? throw new InvalidDataException("No photoTakenTime or creationTime timestamp");
        var geo = root.TryGetProperty("geoData", out var geoValue) ? geoValue : default;
        double? ReadGeo(string name)
        {
            if (geo.ValueKind != JsonValueKind.Object || !geo.TryGetProperty(name, out var value)) return null;
            var number = value.GetDouble();
            return Math.Abs(number) < 0.0000001 ? null : number;
        }
        return new TakeoutMetadata
        {
            Title = title,
            TakenUtc = taken,
            TimeSource = source,
            Latitude = ReadGeo("latitude"),
            Longitude = ReadGeo("longitude"),
            Altitude = ReadGeo("altitude")
        };
    }

    private static (DateTimeOffset Value, string Source)? ReadTimestamp(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || !value.TryGetProperty("timestamp", out var timestamp)) return null;
        if (!long.TryParse(timestamp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) return null;
        return (DateTimeOffset.FromUnixTimeSeconds(seconds), property);
    }
}

sealed class Remapper(string exifTool, Options options)
{
    public async Task<ItemResult> ProcessAsync(PairedItem item)
    {
        try
        {
            var destination = Path.Combine(options.Output, Path.GetRelativePath(options.Source, item.MediaPath));
            var beforeHash = await FileHash.Sha256Async(item.MediaPath);
            var local = ConvertToTimeZone(item.Metadata.TakenUtc, options.TimeZoneId);
            var writePlan = MetadataWritePlan.From(item.Metadata, local, Path.GetExtension(item.MediaPath));
            if (!writePlan.Supported)
                return new ItemResult("Skipped", "Unsupported media extension", item.MetadataPath, item.MediaPath, destination, beforeHash, null, null);

            if (!options.Apply)
                return new ItemResult("DryRun", writePlan.Description, item.MetadataPath, item.MediaPath, destination, beforeHash, null, writePlan);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(item.MediaPath, destination, overwrite: false);
            await ExifToolClient.WriteAsync(exifTool, destination, writePlan);
            var afterHash = await FileHash.Sha256Async(destination);
            var verification = await ExifToolClient.VerifyAsync(exifTool, destination, writePlan);
            return new ItemResult("Applied", writePlan.Description, item.MetadataPath, item.MediaPath, destination, beforeHash, afterHash, writePlan, verification);
        }
        catch (Exception ex)
        {
            return new ItemResult("Failed", $"{ex.GetType().Name}: {ex.Message}", item.MetadataPath, item.MediaPath, null, null, null, null);
        }
    }

    private static DateTimeOffset ConvertToTimeZone(DateTimeOffset utc, string? timeZoneId)
    {
        var zone = string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return TimeZoneInfo.ConvertTime(utc, zone);
    }
}

sealed record MetadataWritePlan(bool Supported, string Description, string ExifDate, string XmpDate, double? Latitude, double? Longitude, double? Altitude)
{
    public static MetadataWritePlan From(TakeoutMetadata metadata, DateTimeOffset local, string extension)
    {
        var supported = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
        return new MetadataWritePlan(
            supported,
            $"{metadata.TimeSource}; local {local:yyyy-MM-dd HH:mm:ss zzz}; GPS {(metadata.Latitude.HasValue && metadata.Longitude.HasValue ? "present" : "absent")}",
            local.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture),
            local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            metadata.Latitude, metadata.Longitude, metadata.Altitude);
    }
}

static class ExifToolClient
{
    public static string? ResolveExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static async Task WriteAsync(string executable, string file, MetadataWritePlan plan)
    {
        var toolFile = await PrepareToolFileAsync(executable, file);
        try
        {
            await WriteToFileAsync(executable, toolFile, plan);
        }
        finally
        {
            await RestoreToolFileAsync(toolFile, file);
        }
    }

    private static async Task WriteToFileAsync(string executable, string file, MetadataWritePlan plan)
    {
        var args = new List<string>
        {
            "-overwrite_original", "-P", "-m", "-n",
            $"-EXIF:DateTimeOriginal={plan.ExifDate}",
            $"-EXIF:CreateDate={plan.ExifDate}",
            $"-XMP:DateTimeOriginal={plan.XmpDate}",
            $"-XMP:CreateDate={plan.XmpDate}"
        };
        if (plan.Latitude.HasValue && plan.Longitude.HasValue)
        {
            var latitude = plan.Latitude.Value;
            var longitude = plan.Longitude.Value;
            args.Add($"-EXIF:GPSLatitude={Math.Abs(latitude).ToString(CultureInfo.InvariantCulture)}");
            args.Add($"-EXIF:GPSLatitudeRef={(latitude < 0 ? "S" : "N")}");
            args.Add($"-EXIF:GPSLongitude={Math.Abs(longitude).ToString(CultureInfo.InvariantCulture)}");
            args.Add($"-EXIF:GPSLongitudeRef={(longitude < 0 ? "W" : "E")}");
            args.Add($"-XMP:GPSLatitude={plan.Latitude.Value.ToString(CultureInfo.InvariantCulture)}");
            args.Add($"-XMP:GPSLongitude={plan.Longitude.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        if (plan.Altitude.HasValue)
            args.Add($"-EXIF:GPSAltitude={plan.Altitude.Value.ToString(CultureInfo.InvariantCulture)}");
        args.Add(file);
        var result = await RunAsync(executable, args);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError.Trim());
    }

    public static async Task<VerificationResult> VerifyAsync(string executable, string file, MetadataWritePlan plan)
    {
        var toolFile = await PrepareToolFileAsync(executable, file);
        ProcessResult result;
        try
        {
            result = await RunAsync(executable, new[] { "-j", "-n", "-DateTimeOriginal", "-CreateDate", "-XMP:DateTimeOriginal", "-GPSLatitude", "-GPSLongitude", toolFile });
        }
        finally
        {
            await RestoreToolFileAsync(toolFile, file);
        }
        if (result.ExitCode != 0) return new VerificationResult("Failed", result.StandardError.Trim());
        using var json = JsonDocument.Parse(result.StandardOutput);
        var record = json.RootElement.EnumerateArray().FirstOrDefault();
        var date = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("DateTimeOriginal", out var d) ? d.GetString() : null;
        var lat = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("GPSLatitude", out var la) ? la.GetDouble() : (double?)null;
        var lon = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("GPSLongitude", out var lo) ? lo.GetDouble() : (double?)null;
        var dateOk = date == plan.ExifDate;
        var gpsOk = !plan.Latitude.HasValue || (lat.HasValue && lon.HasValue && Math.Abs(lat.Value - plan.Latitude.Value) < 0.0001 && Math.Abs(lon.Value - plan.Longitude!.Value) < 0.0001);
        return new VerificationResult(dateOk && gpsOk ? "Verified" : "Mismatch", $"DateTimeOriginal={date ?? "missing"}; GPS={(lat.HasValue ? $"{lat},{lon}" : "missing")}");
    }

    private static async Task<string> PrepareToolFileAsync(string executable, string file)
    {
        var detected = (await RunAsync(executable, new[] { "-s3", "-FileType", file })).StandardOutput.Trim();
        var extension = detected switch
        {
            "JPEG" => ".jpg",
            "PNG" => ".png",
            "HEIC" => ".heic",
            "HEIF" => ".heif",
            "TIFF" => ".tiff",
            "WEBP" => ".webp",
            _ => null
        };
        if (extension is null || Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase))
            return file;
        var temporary = file + ".remapper" + extension;
        File.Move(file, temporary);
        return temporary;
    }

    private static Task RestoreToolFileAsync(string toolFile, string originalFile)
    {
        if (!toolFile.Equals(originalFile, StringComparison.OrdinalIgnoreCase))
            File.Move(toolFile, originalFile, overwrite: true);
        return Task.CompletedTask;
    }

    private static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ExifTool");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}

static class FileHash
{
    public static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
sealed record VerificationResult(string Status, string Details);

sealed class RunReport
{
    public required string App { get; init; }
    public DateTimeOffset GeneratedUtc { get; init; }
    public required string Source { get; init; }
    public required string Output { get; init; }
    public bool Apply { get; init; }
    public int? Limit { get; init; }
    public string? TimeZoneId { get; init; }
    public required string ExifTool { get; init; }
    public required ScanSummary Scan { get; init; }
    public RunSummary? Summary { get; set; }
    public List<ItemResult> Items { get; } = [];
}

sealed class ScanSummary
{
    public int MetadataCount { get; init; }
    public int PairedCount { get; init; }
    public int AmbiguousCount { get; init; }
    public int MissingCount { get; init; }
    public int ExcludedCount { get; init; }
}

sealed class RunSummary
{
    public int Selected { get; init; }
    public int Applied { get; init; }
    public int DryRun { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int Verified { get; init; }
}

sealed record ItemResult(string Status, string Message, string MetadataPath, string MediaPath, string? Destination, string? BeforeSha256, string? AfterSha256, MetadataWritePlan? WritePlan, VerificationResult? Verification = null);

static class SelfTest
{
    public static int Run()
    {
        var temp = Path.Combine(Path.GetTempPath(), "takeout-remapper-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var json = """{"title":"sample.jpg","photoTakenTime":{"timestamp":"1738279308"},"geoData":{"latitude":28.32,"longitude":-82.69}}""";
            var path = Path.Combine(temp, "sample.jpg.supplemental-metadata.json");
            File.WriteAllText(path, json);
            var parsed = TakeoutMetadata.Parse(path);
            if (parsed.Title != "sample.jpg" || parsed.Latitude != 28.32 || parsed.TakenUtc.ToUnixTimeSeconds() != 1738279308)
                throw new Exception("Takeout metadata parser failed");
            Console.WriteLine("Self-test passed: timestamp, title, and GPS parsing.");
            return 0;
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
