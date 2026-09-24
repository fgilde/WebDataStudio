using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Drivers.SqlServer;

/// SQL Server's Showplan XML, read whole: every statement of the batch, every operator, and every
/// attribute and element the server wrote about them, as the tree SSMS's property grid shows.
///
/// Generic on purpose. The schema has a few hundred element types and grows with every release; a
/// parser that knew them by name would drop whatever the next version adds. Only the handful that
/// read badly as a raw tree get a rendering of their own: column references, scalar operators,
/// objects, per-thread counters and the output list.
public static partial class ShowplanParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static bool IsShowplan(string text)
    {
        // The root element is enough, and cheaper than parsing six megabytes to find out.
        var head = text.Length > 4096 ? text[..4096] : text;
        return head.Contains("<ShowPlanXML", StringComparison.Ordinal)
               && head.Contains(Ns.NamespaceName, StringComparison.Ordinal);
    }

    public static PlanDocument Parse(string xml)
    {
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (XmlException e) { throw new FormatException($"this is not readable XML: {e.Message}", e); }

        var statements = document.Descendants()
            .Where(e => e.Name.Namespace == Ns && e.Name.LocalName.StartsWith("Stmt", StringComparison.Ordinal))
            .Select(Statement)
            .OfType<PlanStatement>()
            .ToList();

        return new PlanDocument("sqlserver", xml, "sqlplan", statements);
    }

    private static PlanStatement? Statement(XElement statement)
    {
        // A conditional keeps its plan under Condition; a DECLARE or SET has none and is not shown.
        var plan = statement.Element(Ns + "QueryPlan")
                   ?? statement.Element(Ns + "Condition")?.Element(Ns + "QueryPlan");
        if (plan is null) return null;

        var properties = Attributes(statement, "StatementText")
            .Concat(Attributes(plan))
            .Concat(plan.Elements().Where(e => e.Name != Ns + "RelOp").Select(Property))
            .ToList();

        return new PlanStatement(
            (string?)statement.Attribute("StatementText"),
            (string?)statement.Attribute("StatementType"),
            Number(statement.Attribute("StatementSubTreeCost")),
            properties,
            Warnings(plan.Element(Ns + "Warnings")),
            MissingIndexes(plan),
            plan.Element(Ns + "RelOp") is { } root ? Node(root) : null);
    }

    private static PlanNode Node(XElement relOp)
    {
        var threads = relOp.Element(Ns + "RunTimeInformation")?.Elements(Ns + "RunTimeCountersPerThread").ToList() ?? [];
        var operation = (string?)relOp.Attribute("PhysicalOp") ?? "RelOp";
        var estimatedRows = Number(relOp.Attribute("EstimateRows"));

        var warnings = Warnings(relOp.Element(Ns + "Warnings")).ToList();
        if (operation.Contains("Scan", StringComparison.OrdinalIgnoreCase) && estimatedRows > 1000)
            warnings.Add("scan over many rows");

        return new PlanNode(
            operation,
            (string?)relOp.Attribute("LogicalOp"),
            Number(relOp.Attribute("EstimatedTotalSubtreeCost")),
            estimatedRows,
            // Threads each count their own rows, and run side by side: rows add up, time does not.
            threads.Count == 0 ? null : threads.Sum(t => Number(t.Attribute("ActualRows")) ?? 0),
            threads.Count == 0 ? null : threads.Max(t => Number(t.Attribute("ActualElapsedms"))),
            ChildOperators(relOp).Select(Node).ToList(),
            warnings,
            (int?)relOp.Attribute("NodeId"),
            ObjectOf(relOp),
            Attributes(relOp).Concat(relOp.Elements().Where(e => e.Name != Ns + "RelOp").Select(Property)).ToList());
    }

    /// The operators directly below this one: under its operator element, and under a subquery in
    /// one of its predicates, which SSMS draws as a child too.
    private static IEnumerable<XElement> ChildOperators(XElement relOp)
    {
        foreach (var element in relOp.Elements())
            foreach (var child in Below(element))
                yield return child;

        static IEnumerable<XElement> Below(XElement element)
        {
            if (element.Name == Ns + "RelOp") { yield return element; yield break; }
            foreach (var inner in element.Elements())
                foreach (var found in Below(inner))
                    yield return found;
        }
    }

    private static string? ObjectOf(XElement relOp)
    {
        // The operator element (IndexScan, TableScan, Update, …) holds the Object it touches.
        var target = relOp.Elements().Where(e => e.Name != Ns + "RelOp")
            .Select(e => e.Element(Ns + "Object")).FirstOrDefault(o => o is not null);
        return target is null ? null : ObjectText(target);
    }

    private static string ObjectText(XElement o)
    {
        var name = string.Join(".", new[] { "Schema", "Table", "Index" }
            .Select(a => (string?)o.Attribute(a)).Where(v => !string.IsNullOrEmpty(v)));
        return o.Attribute("Alias") is { } alias ? $"{name} {alias.Value}" : name;
    }

    private static PlanProperty Property(XElement element)
    {
        var local = element.Name.LocalName;

        switch (local)
        {
            case "ColumnReference":
                return new PlanProperty("Column", ColumnText(element), Attributes(element).ToList());
            case "Object":
                return new PlanProperty("Object", ObjectText(element), Attributes(element).ToList());
            case "ScalarOperator" when element.Attribute("ScalarString") is { } scalar:
                return new PlanProperty("Scalar Operator", scalar.Value,
                    element.Elements().Where(e => e.Name != Ns + "RelOp").Select(Property).ToList());
            case "RunTimeCountersPerThread":
                return new PlanProperty($"Thread {(string?)element.Attribute("Thread") ?? "?"}", null,
                    Attributes(element, "Thread").ToList());
        }

        var children = Attributes(element)
            .Concat(element.Elements().Where(e => e.Name != Ns + "RelOp").Select(Property))
            .ToList();

        // A list of columns (OutputList, OrderBy, GroupBy, …) reads as the columns themselves.
        var columns = element.Elements(Ns + "ColumnReference").ToList();
        if (columns.Count > 0 && columns.Count == element.Elements().Count() && !element.HasAttributes)
            return new PlanProperty(Humanize(local), string.Join(", ", columns.Select(ColumnText)), children);

        // A wrapper around one value (Predicate > ScalarOperator) reads as that value.
        if (!element.HasAttributes && children.Count == 1 && children[0].Value is { } only)
            return new PlanProperty(Humanize(local), only, children[0].Children);

        return new PlanProperty(Humanize(local), null, children);
    }

    private static string ColumnText(XElement column)
    {
        var owner = (string?)column.Attribute("Alias") ?? (string?)column.Attribute("Table");
        var name = (string?)column.Attribute("Column") ?? "?";
        if (!name.StartsWith('[')) name = $"[{name}]";
        return owner is null ? name : $"{owner}.{name}";
    }

    private static IEnumerable<PlanProperty> Attributes(XElement element, params string[] except) =>
        element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration && !except.Contains(a.Name.LocalName))
            .Select(a => new PlanProperty(Humanize(a.Name.LocalName), a.Value, []));

    private static IReadOnlyList<string> Warnings(XElement? warnings)
    {
        if (warnings is null) return [];

        // Flags on the element itself (NoJoinPredicate="true"), and one child per richer warning.
        var flags = warnings.Attributes()
            .Where(a => a.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
            .Select(a => Humanize(a.Name.LocalName));
        var details = warnings.Elements().Select(w =>
        {
            var parts = w.Attributes().Select(a => $"{Humanize(a.Name.LocalName)}={a.Value}").ToList();
            return parts.Count == 0 ? Humanize(w.Name.LocalName) : $"{Humanize(w.Name.LocalName)}: {string.Join(", ", parts)}";
        });
        return flags.Concat(details).ToList();
    }

    private static IReadOnlyList<string> MissingIndexes(XElement plan) =>
        plan.Element(Ns + "MissingIndexes")?.Elements(Ns + "MissingIndexGroup").SelectMany(group =>
            group.Elements(Ns + "MissingIndex").Select(index =>
            {
                string[] Columns(string usage) => index.Elements(Ns + "ColumnGroup")
                    .Where(g => (string?)g.Attribute("Usage") == usage)
                    .SelectMany(g => g.Elements(Ns + "Column"))
                    .Select(c => (string?)c.Attribute("Name") ?? "").ToArray();

                var keys = Columns("EQUALITY").Concat(Columns("INEQUALITY")).ToArray();
                var include = Columns("INCLUDE");
                var table = (string?)index.Attribute("Table") ?? "[?]";
                var name = $"[IX_{table.Trim('[', ']')}_{string.Join("_", keys.Select(k => k.Trim('[', ']')))}]";

                var ddl = new StringBuilder();
                if ((string?)group.Attribute("Impact") is { } impact)
                    ddl.Append("-- estimated impact ").Append(impact).Append("%\n");
                ddl.Append($"CREATE INDEX {name} ON {(string?)index.Attribute("Schema")}.{table} ({string.Join(", ", keys)})");
                if (include.Length > 0) ddl.Append($" INCLUDE ({string.Join(", ", include)})");
                return ddl.Append(';').ToString();
            })).ToList() ?? [];

    private static double? Number(XAttribute? attribute) =>
        attribute is not null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// EstimatedTotalSubtreeCost → Estimated Total Subtree Cost; CPU and IO stay one word.
    private static string Humanize(string name) => CamelBreak().Replace(name, " ");

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBreak();
}
