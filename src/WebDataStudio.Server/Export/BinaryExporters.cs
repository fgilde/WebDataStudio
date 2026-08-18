using System.Threading.Channels;
using MiniExcelLibs;
using Parquet;
using Parquet.Schema;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Export;

public sealed class ExcelExporter : IResultExporter
{
    // Excel refuses a cell longer than this and produces a corrupt file if you push past it.
    private const int MaxCellLength = 32767;
    private const int Buffered = 1000;

    public string Format => "xlsx";
    public string Label => "Excel";
    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public string FileExtension => "xlsx";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        // MiniExcel consumes a synchronous sequence. A bounded channel bridges the async chunk
        // stream to it while keeping at most `Buffered` rows in memory.
        var channel = Channel.CreateBounded<IDictionary<string, object?>>(Buffered);

        var pump = Task.Run(async () =>
        {
            try
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
                                var record = new Dictionary<string, object?>();
                                for (var i = 0; i < row.Length; i++)
                                {
                                    var name = i < columns.Count ? columns[i].Name : $"column{i + 1}";
                                    record[name] = Cell(row[i], options);
                                }
                                await channel.Writer.WriteAsync(record, ct);
                            }
                            break;

                        case ResultChunk.Error error:
                            throw new ExportFailedException(error.Text);
                    }
                }
                channel.Writer.Complete();
            }
            catch (Exception e)
            {
                channel.Writer.Complete(e);
            }
        }, ct);

        var sheet = SheetName(options.TableName);
        await MiniExcel.SaveAsAsync(target, channel.Reader.ReadAllAsync(ct).ToBlockingEnumerable(ct),
            sheetName: sheet, cancellationToken: ct);
        await pump;
    }

    private static object? Cell(object? value, ExportOptions options) => value switch
    {
        null => null,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong
             or float or double or decimal or DateTime => value,
        _ => Truncate(ExportValue.ToText(value, options)),
    };

    private static string Truncate(string text) =>
        text.Length <= MaxCellLength ? text : text[..(MaxCellLength - 1)] + "…";

    /// Excel forbids these characters in a sheet name and caps it at 31 characters.
    private static string SheetName(string? name)
    {
        var cleaned = new string((name ?? "Result").Select(c => "[]:*?/\\".Contains(c) ? '_' : c).ToArray());
        return cleaned.Length is 0 ? "Result" : cleaned[..Math.Min(31, cleaned.Length)];
    }
}

public sealed class ParquetExporter : IResultExporter
{
    // ponytail: one row group at a time is buffered — Parquet is columnar and cannot be written
    // strictly row by row. Lower the group size if memory ever matters more than file size.
    private const int RowGroupSize = 50_000;

    public string Format => "parquet";
    public string Label => "Parquet";
    public string ContentType => "application/vnd.apache.parquet";
    public string FileExtension => "parquet";

    public async Task WriteAsync(Stream target, IAsyncEnumerable<ResultChunk> chunks,
        ExportOptions options, CancellationToken ct)
    {
        IReadOnlyList<ColumnMeta> columns = [];
        var buffer = new List<object?[]>();
        ParquetWriter? writer = null;
        ParquetSchema? schema = null;

        try
        {
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                switch (chunk)
                {
                    case ResultChunk.Columns c:
                        columns = c.Items;
                        break;

                    case ResultChunk.Rows rows:
                        buffer.AddRange(rows.Items);
                        if (buffer.Count >= RowGroupSize)
                        {
                            schema ??= BuildSchema(columns, buffer, options);
                            writer ??= await ParquetWriter.CreateAsync(schema, target, cancellationToken: ct);
                            await WriteGroupAsync(writer, schema, buffer, ct);
                            buffer.Clear();
                        }
                        break;

                    case ResultChunk.Error error:
                        throw new ExportFailedException(error.Text);
                }
            }

            schema ??= BuildSchema(columns, buffer, options);
            writer ??= await ParquetWriter.CreateAsync(schema, target, cancellationToken: ct);
            if (buffer.Count > 0) await WriteGroupAsync(writer, schema, buffer, ct);
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
        }
    }

    /// Column types come from the first non-null value seen; an all-null column becomes a nullable
    /// string, which every reader accepts.
    private static ParquetSchema BuildSchema(IReadOnlyList<ColumnMeta> columns,
        IReadOnlyList<object?[]> sample, ExportOptions options)
    {
        var fields = new List<Field>();

        for (var i = 0; i < Math.Max(columns.Count, 1); i++)
        {
            var name = i < columns.Count ? columns[i].Name : $"column{i + 1}";
            var first = sample.Select(r => i < r.Length ? r[i] : null).FirstOrDefault(v => v is not null);

            fields.Add(first switch
            {
                bool => new DataField<bool?>(name),
                byte or sbyte or short or ushort or int => new DataField<int?>(name),
                uint or long or ulong => new DataField<long?>(name),
                float => new DataField<float?>(name),
                double => new DataField<double?>(name),
                decimal => new DataField<decimal?>(name),
                DateTime => new DataField<DateTime?>(name),
                _ => new DataField<string?>(name),
            });
        }

        _ = options;
        return new ParquetSchema(fields);
    }

    // Parquet.Net 6 writes typed columns through a generic overload, so each supported CLR type
    // needs its own call. Anything unexpected goes out as text rather than failing the export.
    private static async Task WriteGroupAsync(ParquetWriter writer, ParquetSchema schema,
        IReadOnlyList<object?[]> rows, CancellationToken ct)
    {
        using var group = writer.CreateRowGroup();

        for (var i = 0; i < schema.DataFields.Length; i++)
        {
            var field = schema.DataFields[i];
            var index = i;

            Task write = field.ClrType switch
            {
                var t when t == typeof(bool) => group.WriteAsync(field, Column<bool>(rows, index), null, null, ct),
                var t when t == typeof(int) => group.WriteAsync(field, Column<int>(rows, index), null, null, ct),
                var t when t == typeof(long) => group.WriteAsync(field, Column<long>(rows, index), null, null, ct),
                var t when t == typeof(float) => group.WriteAsync(field, Column<float>(rows, index), null, null, ct),
                var t when t == typeof(double) => group.WriteAsync(field, Column<double>(rows, index), null, null, ct),
                var t when t == typeof(decimal) => group.WriteAsync(field, Column<decimal>(rows, index), null, null, ct),
                var t when t == typeof(DateTime) => group.WriteAsync(field, Column<DateTime>(rows, index), null, null, ct),
                _ => group.WriteAsync(field, TextColumn(rows, index), null),
            };

            await write;
        }
    }

    private static ReadOnlyMemory<T?> Column<T>(IReadOnlyList<object?[]> rows, int index) where T : struct
    {
        var values = new T?[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var value = index < rows[r].Length ? rows[r][index] : null;
            values[r] = value is null ? null : Cast<T>(value);
        }
        return values;
    }

    private static T? Cast<T>(object value) where T : struct
    {
        try { return (T)System.Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
        // A value that does not fit its inferred type is better exported as null than as a crash.
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException) { return null; }
    }

    private static string[] TextColumn(IReadOnlyList<object?[]> rows, int index)
    {
        var values = new string[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var value = index < rows[r].Length ? rows[r][index] : null;
            values[r] = value?.ToString() ?? "";
        }
        return values;
    }
}
