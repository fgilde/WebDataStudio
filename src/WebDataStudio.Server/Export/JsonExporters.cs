using System.Text.Json;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Export;

/// A JSON array of objects, written element by element so a large result never sits in memory.
public sealed class JsonExporter : IResultExporter
{
    public string Format => "json";
    public string Label => "JSON";
    public string ContentType => "application/json";
    public string FileExtension => "json";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        await using var writer = new Utf8JsonWriter(target, new JsonWriterOptions { Indented = false });
        writer.WriteStartArray();

        IReadOnlyList<ColumnMeta> columns = [];

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c:
                    columns = c.Items;
                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items) JsonRow.Write(writer, columns, row, options);
                    await writer.FlushAsync(ct);
                    break;

                case ResultChunk.Error error:
                    throw new ExportFailedException(error.Text);
            }
        }

        writer.WriteEndArray();
        await writer.FlushAsync(ct);
    }
}

/// One JSON object per line — the format every streaming consumer accepts.
public sealed class NdJsonExporter : IResultExporter
{
    public string Format => "ndjson";
    public string Label => "NDJSON";
    public string ContentType => "application/x-ndjson";
    public string FileExtension => "ndjson";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        IReadOnlyList<ColumnMeta> columns = [];

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            switch (chunk)
            {
                case ResultChunk.Columns c:
                    columns = c.Items;
                    break;

                case ResultChunk.Rows rows:
                    foreach (var row in rows.Items)
                    {
                        await using var writer = new Utf8JsonWriter(target);
                        JsonRow.Write(writer, columns, row, options);
                        await writer.FlushAsync(ct);
                        target.WriteByte((byte)'\n');
                    }
                    await target.FlushAsync(ct);
                    break;

                case ResultChunk.Error error:
                    throw new ExportFailedException(error.Text);
            }
        }
    }
}

internal static class JsonRow
{
    public static void Write(Utf8JsonWriter writer, IReadOnlyList<ColumnMeta> columns,
        object?[] row, ExportOptions options)
    {
        writer.WriteStartObject();
        for (var i = 0; i < row.Length; i++)
        {
            var name = i < columns.Count ? columns[i].Name : $"column{i + 1}";
            writer.WritePropertyName(name);

            switch (row[i])
            {
                // NULL stays a real JSON null: the caller can tell it apart from an empty string.
                case null: writer.WriteNullValue(); break;
                case bool b: writer.WriteBooleanValue(b); break;
                case byte or sbyte or short or ushort or int or uint or long or ulong:
                    writer.WriteNumberValue(Convert.ToInt64(row[i])); break;
                case float or double or decimal:
                    writer.WriteNumberValue(Convert.ToDecimal(row[i])); break;
                default: writer.WriteStringValue(ExportValue.ToText(row[i], options)); break;
            }
        }
        writer.WriteEndObject();
    }
}
