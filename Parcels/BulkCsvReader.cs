using System.Globalization;
using System.IO.Compression;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace IsSeattleFull.Parcels;

internal sealed class BulkCsvRow(CsvReader reader, Dictionary<string, int> indexes, string member)
{
    public string? Text(string field)
    {
        var value = reader.GetField(indexes[field])?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
    public decimal? Number(string field)
    {
        var text = Text(field);
        if (text is null) return null;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"{member}: invalid numeric {field} at CSV row {reader.Parser.Row}.");
        return value;
    }
    public int? Integer(string field)
    {
        var value = Number(field);
        if (value is null) return null;
        if (value != decimal.Truncate(value.Value) || value < int.MinValue || value > int.MaxValue)
            throw new InvalidDataException($"{member}: invalid integer {field} at CSV row {reader.Parser.Row}.");
        return (int)value.Value;
    }
    public DateTime? Date(string field)
    {
        var text = Text(field);
        if (text is null) return null;
        var dot = text.LastIndexOf('.');
        if (dot >= 0 && text[(dot + 1)..].All(char.IsAsciiDigit))
            text = text.TrimEnd('0').TrimEnd('.');
        if (!DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
            "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd", "M/d/yyyy", "M/d/yyyy HH:mm:ss"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new InvalidDataException($"{member}: invalid date {field} at CSV row {reader.Parser.Row}.");
        return value;
    }
    public string Pin => (Text("Major") ?? "").PadLeft(6, '0') + (Text("Minor") ?? "").PadLeft(4, '0');
    public string Major => (Text("Major") ?? "").PadLeft(6, '0');
}

internal static class BulkCsvReader
{
    public static long Read(
        string path, string member, string[] fields, IReadOnlySet<string>? scope,
        Action<BulkCsvRow> consume, CancellationToken cancellationToken, bool majorOnly = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(member) ?? throw new InvalidDataException($"Missing {member} in {Path.GetFileName(path)}.");
        var encoding = DetectEncoding(entry);
        using var stream = entry.Open();
        using var text = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 65536);
        using var reader = new CsvReader(text, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BufferSize = 65536,
            DetectColumnCountChanges = true,
            ExceptionMessagesContainRawData = false
        });
        try
        {
            if (!reader.Read()) throw new InvalidDataException($"Missing CSV header in {member}.");
            reader.ReadHeader();
            var header = reader.HeaderRecord ?? throw new InvalidDataException($"Missing CSV header in {member}.");
            if (header.Distinct(StringComparer.Ordinal).Count() != header.Length)
                throw new InvalidDataException($"Duplicate CSV headers in {member}.");
            var indexes = fields.Distinct().ToDictionary(field => field,
                field => Array.IndexOf(header, field), StringComparer.Ordinal);
            if (indexes.Any(index => index.Value < 0))
                throw new InvalidDataException($"Missing required fields in {member}: {string.Join(", ", indexes.Where(index => index.Value < 0).Select(index => index.Key))}.");
            var row = new BulkCsvRow(reader, indexes, member);
            long count = 0;
            while (reader.Read())
            {
                if (++count % 8192 == 0) cancellationToken.ThrowIfCancellationRequested();
                if (scope is not null)
                {
                    var pin = majorOnly ? row.Major + "0000" : row.Pin;
                    if (!scope.Contains(pin)) continue;
                }
                consume(row);
            }
            return count;
        }
        catch (CsvHelperException)
        {
            throw new InvalidDataException($"CSV parse error in {member} at row {reader.Parser.Row}; raw rows are omitted to protect contact information.");
        }
    }

    private static Encoding DetectEncoding(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var sample = new byte[Math.Min(entry.Length, 1048576)];
        var length = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
        if (length >= 2 && sample[0] == 0xff && sample[1] == 0xfe) return Encoding.Unicode;
        if (length >= 2 && sample[0] == 0xfe && sample[1] == 0xff) return Encoding.BigEndianUnicode;
        if (length >= 3 && sample[0] == 0xef && sample[1] == 0xbb && sample[2] == 0xbf)
            return new UTF8Encoding(false, true);
        if (sample.AsSpan(0, length).ContainsAnyExceptInRange((byte)0, (byte)127))
        {
            try
            {
                new UTF8Encoding(false, true).GetDecoder().GetCharCount(sample, 0, length, flush: false);
                return new UTF8Encoding(false, true);
            }
            catch (DecoderFallbackException) { }
        }
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
