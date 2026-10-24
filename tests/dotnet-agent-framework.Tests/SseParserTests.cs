using System.Text;

namespace DotnetAgentFramework.Tests;

public class SseParserTests
{
    private static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<List<string>> ReadAllAsync(string text)
    {
        using var stream = Utf8(text);
        var events = new List<string>();
        await foreach (var data in SseParser.ReadDataLinesAsync(stream, CancellationToken.None))
        {
            events.Add(data);
        }

        return events;
    }

    [Fact]
    public async Task ReadDataLinesAsync_LfLineEndings_YieldsEachEvent()
    {
        var events = await ReadAllAsync("data: first\n\ndata: second\n\n");

        Assert.Equal(["first", "second"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_CrLfLineEndings_YieldsEachEvent()
    {
        var events = await ReadAllAsync("data: first\r\n\r\ndata: second\r\n\r\n");

        Assert.Equal(["first", "second"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_MultiLineData_JoinedWithNewline()
    {
        var events = await ReadAllAsync("data: line one\ndata: line two\n\n");

        Assert.Equal(["line one\nline two"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_HandlesCrLfAndMultiLineEvents()
    {
        var events = await ReadAllAsync(
            "event: message\r\ndata: first line\r\ndata: second line\r\n\r\n: ping\r\n\r\ndata: last\r\n");

        Assert.Equal(["first line\nsecond line", "last"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_LeadingUtf8Bom_FirstEventKept()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("data: first\n\ndata: second\n\n")).ToArray();
        using var stream = new MemoryStream(bytes);
        var events = new List<string>();
        await foreach (var data in SseParser.ReadDataLinesAsync(stream, CancellationToken.None))
        {
            events.Add(data);
        }

        Assert.Equal(["first", "second"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_CommentsAndOtherFields_Ignored()
    {
        var events = await ReadAllAsync(": keep-alive\nevent: message\nid: 7\nretry: 1000\ndata: payload\n\n");

        Assert.Equal(["payload"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_DataWithoutSpace_Accepted()
    {
        var events = await ReadAllAsync("data:compact\n\n");

        Assert.Equal(["compact"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_OnlyOneLeadingSpaceRemoved_KeepsRest()
    {
        var events = await ReadAllAsync("data:  indented\n\n");

        Assert.Equal([" indented"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_EmptyDataField_YieldsEmptyString()
    {
        var events = await ReadAllAsync("data:\n\ndata: next\n\n");

        Assert.Equal(["", "next"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_ConsecutiveBlankLines_YieldNothingExtra()
    {
        var events = await ReadAllAsync("\n\ndata: only\n\n\n\n");

        Assert.Equal(["only"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_NoTrailingBlankLine_YieldsLastEvent()
    {
        var events = await ReadAllAsync("data: first\n\ndata: last");

        Assert.Equal(["first", "last"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_MultiByteUtf8_DecodedWhole()
    {
        var events = await ReadAllAsync("data: Привіт 👋\n\n");

        Assert.Equal(["Привіт 👋"], events);
    }

    [Fact]
    public async Task ReadDataLinesAsync_CancelledToken_Throws()
    {
        using var stream = Utf8("data: never\n\n");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SseParser.ReadDataLinesAsync(stream, cts.Token))
            {
            }
        });
    }
}
