using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

public class PlanOpenEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-plan").FullName;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    private const string Plan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT 1" StatementType="SELECT" StatementSubTreeCost="0.1">
              <QueryPlan><RelOp NodeId="0" PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimateRows="90000" EstimatedTotalSubtreeCost="0.1">
                <OutputList /><TableScan><Object Schema="[dbo]" Table="[Big]" /></TableScan>
              </RelOp></QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public async Task Opens_a_showplan_without_a_connection()
    {
        await using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/plans/open", new { text = Plan });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sqlplan", body.GetProperty("document").GetProperty("rawFormat").GetString());
        Assert.Equal("Table Scan", body.GetProperty("plan").GetProperty("operation").GetString());
        Assert.True(body.GetProperty("findings").GetArrayLength() > 0);
    }

    [Theory]
    [InlineData("id,name\n1,ada")]
    [InlineData("")]
    [InlineData("<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"")]
    public async Task Refuses_what_is_not_a_plan_with_a_sentence(string text)
    {
        await using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/plans/open", new { text });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Opens_a_plan_deeper_than_the_default_json_depth()
    {
        // Real plans nest far deeper than System.Text.Json's default of 64: every operator is two
        // levels (the node and its children list), and a join-heavy statement is dozens deep.
        var nested = "<RelOp NodeId=\"99\" PhysicalOp=\"Constant Scan\" LogicalOp=\"Constant Scan\" EstimateRows=\"1\"><OutputList /><ConstantScan /></RelOp>";
        for (var i = 0; i < 80; i++)
            nested = $"<RelOp NodeId=\"{i}\" PhysicalOp=\"Compute Scalar\" LogicalOp=\"Compute Scalar\" EstimateRows=\"1\"><OutputList /><ComputeScalar>{nested}</ComputeScalar></RelOp>";
        var plan = $"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT 1" StatementType="SELECT"><QueryPlan>{nested}</QueryPlan></StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        await using var factory = Factory();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/plans/open", new { text = plan });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private const string TwoIndexes = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT 1" StatementType="SELECT" StatementSubTreeCost="0.1">
              <QueryPlan>
                <Warnings>
                  <PlanAffectingConvert ConvertIssue="Cardinality Estimate" Expression="CONVERT(int,[a])" />
                  <PlanAffectingConvert ConvertIssue="Cardinality Estimate" Expression="CONVERT(int,[b])" />
                </Warnings>
                <MissingIndexes>
                  <MissingIndexGroup Impact="50"><MissingIndex Schema="[dbo]" Table="[A]"><ColumnGroup Usage="EQUALITY"><Column Name="[x]" /></ColumnGroup></MissingIndex></MissingIndexGroup>
                  <MissingIndexGroup Impact="40"><MissingIndex Schema="[dbo]" Table="[B]"><ColumnGroup Usage="EQUALITY"><Column Name="[y]" /></ColumnGroup></MissingIndex></MissingIndexGroup>
                </MissingIndexes>
                <RelOp NodeId="0" PhysicalOp="Constant Scan" LogicalOp="Constant Scan" EstimateRows="1" EstimatedTotalSubtreeCost="0.1"><OutputList /><ConstantScan /></RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public async Task Every_missing_index_and_every_warning_is_its_own_finding()
    {
        await using var factory = Factory();
        var body = await (await factory.CreateClient().PostAsJsonAsync("/api/plans/open", new { text = TwoIndexes }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var findings = body.GetProperty("findings").EnumerateArray().ToList();

        Assert.Equal(2, findings.Count(f => f.GetProperty("category").GetString() == "missing-index"));
        Assert.Equal(2, findings.Count(f => f.GetProperty("category").GetString() == "plan"));
    }

    [Fact]
    public async Task The_tree_travels_once_with_its_properties()
    {
        await using var factory = Factory();
        var body = await (await factory.CreateClient().PostAsJsonAsync("/api/plans/open", new { text = Plan }))
            .Content.ReadFromJsonAsync<JsonElement>();

        // `plan` is for the tree and the rules; the properties live in the document alone.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("plan").GetProperty("properties").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("document").GetProperty("statements")[0]
            .GetProperty("root").GetProperty("properties").ValueKind);
    }
}
