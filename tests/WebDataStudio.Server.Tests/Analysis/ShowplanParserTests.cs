using WebDataStudio.Server.Drivers.SqlServer;

namespace WebDataStudio.Server.Tests.Analysis;

public class ShowplanParserTests
{
    /// Two statements (a DECLARE without a plan, then a SELECT), a nested loop over a seek and a
    /// scan, runtime counters on two threads, a missing index, a statement warning, and a RelOp
    /// hidden inside a scalar subquery.
    private const string Plan = """
        <?xml version="1.0" encoding="utf-16"?>
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6" Build="16.0">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="DECLARE @x int" StatementId="1" StatementType="DECLARE" />
            <StmtSimple StatementText="SELECT o.Id FROM dbo.Orders o JOIN dbo.Lines l ON l.OrderId = o.Id" StatementId="2" StatementType="SELECT" StatementSubTreeCost="1.5">
              <QueryPlan DegreeOfParallelism="2" MemoryGrant="1024" CompileTime="12">
                <Warnings><PlanAffectingConvert ConvertIssue="Cardinality Estimate" Expression="CONVERT(int,[l].[Code])" /></Warnings>
                <MissingIndexes>
                  <MissingIndexGroup Impact="87.5">
                    <MissingIndex Database="[shop]" Schema="[dbo]" Table="[Lines]">
                      <ColumnGroup Usage="EQUALITY"><Column Name="[OrderId]" ColumnId="2" /></ColumnGroup>
                      <ColumnGroup Usage="INCLUDE"><Column Name="[Qty]" ColumnId="3" /></ColumnGroup>
                    </MissingIndex>
                  </MissingIndexGroup>
                </MissingIndexes>
                <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10" EstimatedTotalSubtreeCost="1.5">
                  <OutputList><ColumnReference Database="[shop]" Schema="[dbo]" Table="[Orders]" Alias="[o]" Column="Id" /></OutputList>
                  <RunTimeInformation>
                    <RunTimeCountersPerThread Thread="1" ActualRows="4" ActualElapsedms="30" ActualExecutions="1" />
                    <RunTimeCountersPerThread Thread="2" ActualRows="6" ActualElapsedms="42" ActualExecutions="1" />
                  </RunTimeInformation>
                  <NestedLoops Optimized="false">
                    <RelOp NodeId="1" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="0.2">
                      <OutputList />
                      <IndexScan Ordered="true">
                        <Object Database="[shop]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" Alias="[o]" />
                        <SeekPredicates><SeekPredicateNew><SeekKeys><Prefix ScanType="EQ">
                          <RangeColumns><ColumnReference Table="[Orders]" Column="Id" /></RangeColumns>
                          <RangeExpressions><ScalarOperator ScalarString="[l].[OrderId]" /></RangeExpressions>
                        </Prefix></SeekKeys></SeekPredicateNew></SeekPredicates>
                      </IndexScan>
                    </RelOp>
                    <RelOp NodeId="2" PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimateRows="5000" EstimatedTotalSubtreeCost="1.1">
                      <OutputList />
                      <Warnings NoJoinPredicate="true" />
                      <TableScan>
                        <Object Database="[shop]" Schema="[dbo]" Table="[Lines]" Alias="[l]" />
                        <Predicate><ScalarOperator ScalarString="[l].[Qty]&gt;(0)">
                          <Subquery Operation="EXISTS"><RelOp NodeId="3" PhysicalOp="Constant Scan" LogicalOp="Constant Scan" EstimateRows="1" EstimatedTotalSubtreeCost="0">
                            <OutputList /><ConstantScan />
                          </RelOp></Subquery>
                        </ScalarOperator></Predicate>
                      </TableScan>
                    </RelOp>
                  </NestedLoops>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void Recognises_showplan_and_nothing_else()
    {
        Assert.True(ShowplanParser.IsShowplan(Plan));
        Assert.False(ShowplanParser.IsShowplan("id,name\n1,ada"));
        Assert.False(ShowplanParser.IsShowplan("<root/>"));
        Assert.False(ShowplanParser.IsShowplan(""));
    }

    [Fact]
    public void Keeps_the_raw_text_for_export()
    {
        var document = ShowplanParser.Parse(Plan);

        Assert.Equal("sqlserver", document.Engine);
        Assert.Equal("sqlplan", document.RawFormat);
        Assert.Equal(Plan, document.Raw);
    }

    [Fact]
    public void Lists_only_statements_that_have_a_plan()
    {
        var statement = Assert.Single(ShowplanParser.Parse(Plan).Statements);

        Assert.Equal("SELECT", statement.Type);
        Assert.Equal(1.5, statement.Cost);
        Assert.StartsWith("SELECT o.Id", statement.Text);
    }

    [Fact]
    public void Builds_the_tree_including_a_relop_inside_a_subquery()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;

        Assert.Equal("Nested Loops", root.Operation);
        Assert.Equal("Inner Join", root.Detail);
        Assert.Equal(0, root.NodeId);
        Assert.Equal(["Clustered Index Seek", "Table Scan"], root.Children.Select(c => c.Operation));
        Assert.Equal("Constant Scan", Assert.Single(root.Children[1].Children).Operation);
    }

    [Fact]
    public void Sums_actual_rows_over_threads_and_takes_the_slowest_thread()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;

        Assert.Equal(10, root.ActualRows);
        Assert.Equal(42, root.ActualMs);
        // An estimated-only node says nothing about actuals.
        Assert.Null(root.Children[0].ActualRows);
        Assert.Null(root.Children[0].ActualMs);
    }

    [Fact]
    public void Names_the_object_a_node_touches()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;

        Assert.Equal("[dbo].[Orders].[PK_Orders] [o]", root.Children[0].Object);
        Assert.Equal("[dbo].[Lines] [l]", root.Children[1].Object);
    }

    [Fact]
    public void Carries_every_detail_as_properties()
    {
        var seek = ShowplanParser.Parse(Plan).Statements[0].Root!.Children[0];
        var properties = seek.Properties!;

        Assert.Contains(properties, p => p.Name == "Estimate Rows" && p.Value == "1");
        var indexScan = Assert.Single(properties, p => p.Name == "Index Scan");
        Assert.Contains(indexScan.Children, p => p.Name == "Ordered" && p.Value == "true");
        // Scalar operators read as the expression, not as an empty group.
        Assert.Contains("[l].[OrderId]", Flatten(indexScan).Select(p => p.Value));
        // Column references read as [table].[column].
        Assert.Contains("[Orders].[Id]", Flatten(indexScan).Select(p => p.Value));
    }

    [Fact]
    public void Shows_runtime_counters_per_thread()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;
        var runtime = Assert.Single(root.Properties!, p => p.Name == "Run Time Information");

        Assert.Equal(["Thread 1", "Thread 2"], runtime.Children.Select(c => c.Name));
    }

    [Fact]
    public void Output_list_reads_as_its_columns()
    {
        var root = ShowplanParser.Parse(Plan).Statements[0].Root!;

        Assert.Contains(root.Properties!, p => p.Name == "Output List" && p.Value == "[o].[Id]");
    }

    [Fact]
    public void Reports_warnings_of_the_statement_and_of_nodes()
    {
        var statement = ShowplanParser.Parse(Plan).Statements[0];

        Assert.Contains(statement.Warnings, w => w.Contains("Plan Affecting Convert") && w.Contains("CONVERT(int,[l].[Code])"));
        Assert.Contains(statement.Root!.Children[1].Warnings, w => w.Contains("No Join Predicate"));
    }

    [Fact]
    public void Turns_a_missing_index_into_a_statement()
    {
        var index = Assert.Single(ShowplanParser.Parse(Plan).Statements[0].MissingIndexes);

        Assert.Contains("-- estimated impact 87.5%", index);
        Assert.Contains("CREATE INDEX [IX_Lines_OrderId] ON [dbo].[Lines] ([OrderId]) INCLUDE ([Qty]);", index);
    }

    [Fact]
    public void Statement_properties_include_the_query_plan()
    {
        var properties = ShowplanParser.Parse(Plan).Statements[0].Properties;

        Assert.Contains(properties, p => p.Name == "Memory Grant" && p.Value == "1024");
        Assert.Contains(properties, p => p.Name == "Degree Of Parallelism" && p.Value == "2");
        Assert.DoesNotContain(properties, p => p.Name == "Statement Text");
    }

    [Fact]
    public void Broken_xml_is_a_format_exception()
    {
        Assert.Throws<FormatException>(() => ShowplanParser.Parse("<ShowPlanXML"));
    }

    private static IEnumerable<WebDataStudio.Server.Drivers.Abstractions.PlanProperty> Flatten(
        WebDataStudio.Server.Drivers.Abstractions.PlanProperty property) =>
        new[] { property }.Concat(property.Children.SelectMany(Flatten));
}
