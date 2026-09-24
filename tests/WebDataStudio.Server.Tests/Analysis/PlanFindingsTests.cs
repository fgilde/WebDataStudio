using WebDataStudio.Server.Analysis;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Tests.Analysis;

public class PlanFindingsTests
{
    [Fact]
    public void Missing_indexes_and_plan_warnings_become_findings()
    {
        var root = new PlanNode("Table Scan", null, 1, 50_000, null, null, [], [], Object: "[dbo].[Lines]");
        var document = new PlanDocument("sqlserver", "<x/>", "sqlplan",
        [
            new PlanStatement("SELECT 1", "SELECT", 1, [], ["Plan Affecting Convert: Expression=CONVERT(int,[x])"],
                ["-- estimated impact 90%\nCREATE INDEX [IX_Lines_A] ON [dbo].[Lines] ([A]);"], root),
        ]);

        var findings = PlanFindings.From(document).ToList();

        Assert.Contains(findings, f => f.Category == "missing-index" && f.Statement?.Contains("CREATE INDEX [IX_Lines_A]") == true);
        Assert.Contains(findings, f => f.Category == "plan" && f.Detail.Contains("CONVERT(int,[x])"));
    }

    [Fact]
    public void A_document_without_statements_has_no_findings()
    {
        Assert.Empty(PlanFindings.From(new PlanDocument("sqlserver", null, null, [])));
    }
}
