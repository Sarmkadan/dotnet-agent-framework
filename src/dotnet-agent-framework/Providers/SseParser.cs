using System.Runtime.CompilerServices;
using System.Text;

namespace DotnetAgentFramework;

/// <summary>
/// Minimal Server-Sent Events reader that yields the payload of each event. Only the <c>data</c> field is
/// kept; comments and the <c>event</c>, <c>id</c> and <c>retry</c> fields are skipped.
/// </summary>
internal static class SseParser
{
    private const string _dataPrefix = "data:";

    /// <summary>
    /// Reads <c>data:</c> lines from an SSE stream and yields one string per event. Consecutive <c>data:</c>
    /// lines of the same event are joined with '\n'. Both <c>\n</c> and <c>\r\n</c> line endings are accepted.
    /// </summary>
    /// <remarks>
    /// An event ends at a blank line. If the stream ends with an event that has no trailing blank line, that
    /// event is still yielded. The stream is read but not disposed; the caller owns it.
    /// </remarks>
    /// <param name="stream">Response body to read. Read as UTF-8.</param>
    /// <param name="cancellationToken">Checked before each line is read.</param>
    /// <returns>The data payload of each event, in stream order.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal static async IAsyncEnumerable<string> ReadDataLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

        var data = new StringBuilder();
        var hasData = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                if (hasData)
                {
                    yield return data.ToString();
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return data.ToString();
                    data.Clear();
                    hasData = false;
                }

                continue;
            }

            if (!line.StartsWith(_dataPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // The SSE spec drops exactly one space after the colon, if present.
            var valueStart = line.Length > _dataPrefix.Length && line[_dataPrefix.Length] == ' '
                ? _dataPrefix.Length + 1
                : _dataPrefix.Length;

            if (hasData)
            {
                data.Append('\n');
            }

            data.Append(line, valueStart, line.Length - valueStart);
            hasData = true;
        }
    }
}
