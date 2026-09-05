using EPDeskExtractionWorker.Services.Extraction;

namespace EPDeskExtractionWorker.Tests;

public sealed class OpenXmlExtractorTests
{
    [Fact]
    public async Task DocxExtractor_ReadsHierarchyTableHeaderAndPermittedComments()
    {
        using var files = new TestFiles();
        var path = files.WriteZip("proposal.docx", new Dictionary<string, string>
        {
            ["word/document.xml"] = """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p><w:pPr><w:pStyle w:val="Heading1" /></w:pPr><w:r><w:t>Commercial Terms</w:t></w:r></w:p>
                    <w:p><w:r><w:t>Payment is due in 30 days.</w:t></w:r></w:p>
                    <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Item</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Price</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                  </w:body>
                </w:document>
                """,
            ["word/header1.xml"] = """
                <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:p><w:r><w:t>Confidential</w:t></w:r></w:p></w:hdr>
                """,
            ["word/comments.xml"] = """
                <w:comments xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:comment w:id="7" w:author="Reviewer"><w:p><w:r><w:t>Check pricing</w:t></w:r></w:p></w:comment></w:comments>
                """
        });

        var result = await new DocxExtractor().ExtractAsync(
            new ExtractionRequest(path, "proposal.docx", IncludeWordComments: true));

        Assert.Equal("docx", result.Format);
        var heading = Assert.Single(result.Sections, section => section.SectionType == "heading");
        Assert.Equal("Commercial Terms", heading.Heading);
        var paragraph = Assert.Single(result.Sections, section => section.SectionType == "paragraph");
        Assert.Equal("Commercial Terms", paragraph.Heading);
        Assert.Contains("30 days", paragraph.Content);
        var table = Assert.Single(result.Sections, section => section.SectionType == "table");
        Assert.Contains("Item | Price", table.Content);
        Assert.Equal(
            "Confidential",
            Assert.Single(result.Sections, section => section.SectionType == "header").Content);
        Assert.Equal(
            "Check pricing",
            Assert.Single(result.Sections, section => section.SectionType == "comment").Content);
    }

    [Fact]
    public async Task PptxExtractor_ReadsSlideTitleTableAndSpeakerNotes()
    {
        using var files = new TestFiles();
        var path = files.WriteZip("plan.pptx", new Dictionary<string, string>
        {
            ["ppt/presentation.xml"] = """
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst><p:sldId id="256" r:id="rId1" /></p:sldIdLst></p:presentation>
                """,
            ["ppt/_rels/presentation.xml.rels"] = Relationships(("rId1", "slides/slide1.xml")),
            ["ppt/slides/slide1.xml"] = """
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree>
                  <p:sp><p:nvSpPr><p:cNvPr name="Title 1"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>Launch Plan</a:t></a:r></a:p></p:txBody></p:sp>
                  <p:sp><p:nvSpPr><p:cNvPr name="Body 2"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:txBody><a:p><a:r><a:t>First milestone</a:t></a:r></a:p></p:txBody></p:sp>
                  <p:graphicFrame><a:graphic><a:graphicData><a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>Owner</a:t></a:r></a:p></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>Date</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>
                </p:spTree></p:cSld></p:sld>
                """,
            ["ppt/slides/_rels/slide1.xml.rels"] =
                Relationships(("rIdNotes", "../notesSlides/notesSlide1.xml")),
            ["ppt/notesSlides/notesSlide1.xml"] = """
                <p:notes xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr name="Notes"/><p:cNvSpPr/><p:nvPr><p:ph type="body"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>Discuss with sales.</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:notes>
                """
        });

        var result = await new PptxExtractor().ExtractAsync(
            new ExtractionRequest(path, "plan.pptx", IncludeSpeakerNotes: true));

        var slide = Assert.Single(result.Sections);
        Assert.Equal(1, slide.SectionNumber);
        Assert.Equal("Launch Plan", slide.Heading);
        Assert.Contains("First milestone", slide.Content);
        Assert.Contains("Owner | Date", slide.Content);
        Assert.Contains("Discuss with sales.", slide.Content);
        Assert.Equal("Discuss with sales.", slide.Metadata["speaker_notes"]);
    }

    [Fact]
    public async Task XlsxExtractor_SummarizesSheetsTypesSamplesAndTableBoundaries()
    {
        using var files = new TestFiles();
        var path = files.WriteZip("sales.xlsx", new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Orders" sheetId="1" r:id="rId1" /></sheets></workbook>
                """,
            ["xl/_rels/workbook.xml.rels"] = Relationships(("rId1", "worksheets/sheet1.xml")),
            ["xl/sharedStrings.xml"] = """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Client</t></si><si><t>Amount</t></si><si><t>Approved</t></si><si><t>Acme</t></si><si><t>Beta</t></si></sst>
                """,
            ["xl/worksheets/sheet1.xml"] = """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><dimension ref="A1:C3"/><sheetData>
                  <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>
                  <row r="2"><c r="A2" t="s"><v>3</v></c><c r="B2"><v>42.5</v></c><c r="C2" t="b"><v>1</v></c></row>
                  <row r="3"><c r="A3" t="s"><v>4</v></c><c r="B3"><v>7</v></c><c r="C3" t="b"><v>0</v></c></row>
                </sheetData></worksheet>
                """,
            ["xl/worksheets/_rels/sheet1.xml.rels"] =
                Relationships(("rIdTable", "../tables/table1.xml")),
            ["xl/tables/table1.xml"] = """
                <table xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" name="OrdersTable" displayName="OrdersTable" ref="A1:C3" />
                """
        });

        var result = await new XlsxExtractor().ExtractAsync(new ExtractionRequest(path, "sales.xlsx"));

        var sheet = Assert.Single(result.Sections);
        Assert.Equal("Orders", sheet.Heading);
        Assert.Equal(2, sheet.Metadata["row_count"]);
        Assert.Equal(3, sheet.Metadata["column_count"]);
        Assert.Equal("A1:C3", sheet.Metadata["used_range"]);
        var columns = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
            sheet.Metadata["columns"]);
        Assert.Equal(["Client", "Amount", "Approved"], columns.Select(column => column["name"]));
        Assert.Equal("string", columns[0]["inferred_type"]);
        Assert.Equal("number", columns[1]["inferred_type"]);
        Assert.Equal("boolean", columns[2]["inferred_type"]);
        var tables = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
            sheet.Metadata["table_boundaries"]);
        Assert.Equal("A1:C3", Assert.Single(tables)["range"]);
    }

    private static string Relationships(params (string Id, string Target)[] values) =>
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(values.Select(value =>
            "<Relationship Id=\"" + value.Id + "\" Target=\"" + value.Target + "\" />")) +
        "</Relationships>";
}
