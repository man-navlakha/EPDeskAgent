using EPDeskExtractionWorker.Services.Extraction;

namespace EPDeskExtractionWorker.Tests;

public sealed class CsvExtractorTests
{
    [Fact]
    public async Task ExtractAsync_SummarizesQuotedRowsAndInfersColumnTypes()
    {
        using var files = new TestFiles();
        var path = files.WriteText(
            "orders.csv",
            "Client,Client,,Amount,Approved,OrderDate\r\n" +
            "\"Acme, Inc.\",North,,12.50,true,2026-04-02\r\n" +
            "\"Line one\nLine two\",South,,7,false,2026-05-03\r\n");
        var extractor = new CsvExtractor(new ExtractionLimits { SampleRowCount = 5 });

        var result = await extractor.ExtractAsync(new ExtractionRequest(path, "orders.csv"));

        Assert.Equal("csv", result.Format);
        var section = Assert.Single(result.Sections);
        Assert.Equal("table", section.SectionType);
        Assert.Equal(2, section.Metadata["row_count"]);
        Assert.Equal("A1:F3", section.Metadata["table_boundary"]);

        var columns = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
            section.Metadata["columns"]);
        Assert.Equal(
            ["Client", "Client_2", "Column3", "Amount", "Approved", "OrderDate"],
            columns.Select(column => column["name"]));
        Assert.Equal("number", columns[3]["inferred_type"]);
        Assert.Equal("boolean", columns[4]["inferred_type"]);
        Assert.Equal("date", columns[5]["inferred_type"]);

        var samples = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, string?>>>(
            section.Metadata["sample_rows"]);
        Assert.Equal("Acme, Inc.", samples[0]["Client"]);
        Assert.Equal("Line one\nLine two", samples[1]["Client"]);
    }

    [Fact]
    public async Task ExtractAsync_EmptyFileReturnsWarningWithoutSections()
    {
        using var files = new TestFiles();
        var path = files.WriteText("empty.csv", "");

        var result = await new CsvExtractor().ExtractAsync(new ExtractionRequest(path, "empty.csv"));

        Assert.Empty(result.Sections);
        Assert.Equal(0, result.Metadata["row_count"]);
        Assert.Single(result.Warnings);
    }
}
