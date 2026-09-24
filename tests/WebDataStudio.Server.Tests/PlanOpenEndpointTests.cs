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
}
