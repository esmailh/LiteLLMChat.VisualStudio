using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpStdioFramingTests
{
    private static async Task<(List<string> Lines, List<string> Diagnostics)> ReadAsync(byte[] bytes, int max = 1024)
    {
        var lines = new List<string>();
        var diagnostics = new List<string>();
        using (var stream = new MemoryStream(bytes))
            await McpStdioFraming.ReadLinesAsync(stream,
                line => { lines.Add(line); return Task.CompletedTask; },
                diagnostic => diagnostics.Add(diagnostic), max, CancellationToken.None);
        return (lines, diagnostics);
    }

    [Fact]
    public async Task ReadsNewlineDelimitedMessages()
    {
        var (lines, diagnostics) = await ReadAsync(Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}\n"));

        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, lines);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task StripsTheCarriageReturnFromAWindowsLineEnding()
    {
        var (lines, _) = await ReadAsync(Encoding.UTF8.GetBytes("{\"a\":1}\r\n"));

        Assert.Equal(new[] { "{\"a\":1}" }, lines);
    }

    [Fact]
    public async Task ReadsAMultiByteCharacterSplitAcrossTwoReads()
    {
        // The stream hands over one byte at a time, so the two bytes of the Persian character land in different
        // reads. The incremental decoder is what keeps that from becoming two replacement characters.
        var lines = new List<string>();
        var bytes = Encoding.UTF8.GetBytes("\"سلام\"\n");
        using (var stream = new OneByteAtATimeStream(bytes))
            await McpStdioFraming.ReadLinesAsync(stream,
                line => { lines.Add(line); return Task.CompletedTask; },
                _ => { }, 1024, CancellationToken.None);

        Assert.Equal(new[] { "\"سلام\"" }, lines);
    }

    private sealed class OneByteAtATimeStream : Stream
    {
        private readonly byte[] _bytes;
        private int _offset;
        public OneByteAtATimeStream(byte[] bytes) => _bytes = bytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0 || _offset >= _bytes.Length) return 0;
            buffer[offset] = _bytes[_offset++];
            return 1;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task DiscardsAnOverlongLineAndKeepsReading()
    {
        // The cap must be above the length of the line that follows, or it would discard both and prove nothing.
        var bytes = Encoding.UTF8.GetBytes(new string('x', 32) + "\n{\"ok\":true}\n");
        var (lines, diagnostics) = await ReadAsync(bytes, max: 16);

        Assert.Equal(new[] { "{\"ok\":true}" }, lines);
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task EmitsAnUnterminatedLastLine()
    {
        var (lines, _) = await ReadAsync(Encoding.UTF8.GetBytes("{\"a\":1}"));

        Assert.Equal(new[] { "{\"a\":1}" }, lines);
    }
}
