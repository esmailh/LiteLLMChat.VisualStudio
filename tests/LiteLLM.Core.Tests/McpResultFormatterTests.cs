using System;
using System.Collections.Generic;
using System.Text;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiteLLM.Core.Tests;
public sealed class McpResultFormatterTests
{
    [Fact]
    public void Format_ReturnsTextBlocksInOrder()
    {
        var result = Formatter().Format(Tool("echo"), Result(
            Block("text", "hello"),
            Block("text", "world")));

        Assert.True(result.Ok);
        Assert.Equal("hello\n\nworld", result.Content);
        Assert.Equal("Files · echo · ok · 12 chars", result.Summary);
    }

    [Fact]
    public void Format_SavesAnImageAndNamesTheFile()
    {
        string? extension = null;
        var saved = "";
        var formatter = new McpResultFormatter((ext, bytes) =>
        {
            extension = ext;
            saved = "C:\\media\\shot.png";
            return saved;
        });

        var result = formatter.Format(Tool("screen"), Result(new JObject
        {
            ["type"] = "image",
            ["mimeType"] = "image/png",
            ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"))
        }));

        Assert.Equal(".png", extension);
        Assert.Equal(1, result.Attachments);
        Assert.Contains(saved, result.Content, StringComparison.Ordinal);
        Assert.Contains("1 attachment", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReportsAnAttachmentItCouldNotSave()
    {
        var formatter = new McpResultFormatter((ext, bytes) => throw new InvalidOperationException("disk full"));

        var result = formatter.Format(Tool("screen"), Result(new JObject
        {
            ["type"] = "image",
            ["mimeType"] = "image/png",
            ["data"] = Convert.ToBase64String(new byte[] { 1, 2, 3 })
        }));

        Assert.Equal(1, result.Unsaved);
        Assert.Contains("disk full", result.Content, StringComparison.Ordinal);
        Assert.Contains("not saved", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReportsAnImageWhoseDataIsNotBase64()
    {
        var result = Formatter().Format(Tool("screen"), Result(new JObject
        {
            ["type"] = "image",
            ["mimeType"] = "image/png",
            ["data"] = "not base64!"
        }));

        Assert.Equal(1, result.Unsaved);
        Assert.Contains("no readable data", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_LabelsAResourceLinkWithoutFollowingIt()
    {
        var result = Formatter().Format(Tool("find"), Result(new JObject
        {
            ["type"] = "resource_link",
            ["name"] = "notes",
            ["uri"] = "file:///notes.md",
            ["mimeType"] = "text/markdown"
        }));

        Assert.Contains("notes · file:///notes.md · text/markdown", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_EmbedsResourceTextInline()
    {
        var result = Formatter().Format(Tool("read"), Result(new JObject
        {
            ["type"] = "resource",
            ["resource"] = new JObject
            {
                ["uri"] = "file:///a.txt",
                ["mimeType"] = "text/plain",
                ["text"] = "alpha"
            }
        }));

        Assert.Contains("[resource file:///a.txt (text/plain)]", result.Content, StringComparison.Ordinal);
        Assert.Contains("alpha", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_SavesAnEmbeddedBlob()
    {
        string? extension = null;
        var formatter = new McpResultFormatter((ext, bytes) =>
        {
            extension = ext;
            return "C:\\media\\blob.bin";
        });

        var result = formatter.Format(Tool("read"), Result(new JObject
        {
            ["type"] = "resource",
            ["resource"] = new JObject
            {
                ["uri"] = "file:///a.bin",
                ["mimeType"] = "application/octet-stream",
                ["blob"] = Convert.ToBase64String(new byte[] { 4, 5, 6 })
            }
        }));

        Assert.Equal(1, result.Attachments);
        Assert.Equal(".bin", extension);
        Assert.Contains("C:\\media\\blob.bin", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_PlacesStructuredContentBesideTextOrAlone()
    {
        var beside = Formatter().Format(Tool("read"), new JObject
        {
            ["content"] = new JArray(Block("text", "summary")),
            ["structuredContent"] = new JObject { ["count"] = 2 }
        });
        Assert.Contains("Structured content:\n{\"count\":2}", beside.Content, StringComparison.Ordinal);

        var alone = Formatter().Format(Tool("read"), new JObject
        {
            ["structuredContent"] = new JObject { ["count"] = 2 }
        });
        Assert.Equal("{\"count\":2}", alone.Content);
    }

    [Fact]
    public void Format_ReportsAnErrorFromTheServer()
    {
        var result = Formatter().Format(Tool("echo"), new JObject
        {
            ["content"] = new JArray(Block("text", "no such file")),
            ["isError"] = true
        });

        Assert.False(result.Ok);
        Assert.Contains("failed", result.Summary, StringComparison.Ordinal);
        Assert.Contains("no such file", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ClipsALongBlockAndSaysSo()
    {
        var text = new string('a', McpResultFormatter.MaxBlockChars + 10);
        var result = Formatter().Format(Tool("echo"), Result(Block("text", text)));

        Assert.True(result.Truncated);
        Assert.EndsWith("[output truncated]", result.Content);
        Assert.Contains("truncated", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_StopsAtTheTotalBudget()
    {
        var blocks = new List<JObject>();
        for (var index = 0; index < 4; index++)
            blocks.Add(Block("text", new string((char)('a' + index), McpResultFormatter.MaxBlockChars)));

        var result = Formatter().Format(Tool("echo"), new JObject { ["content"] = new JArray(blocks) });

        Assert.True(result.Truncated);
        Assert.True(result.Content.Length <= McpResultFormatter.MaxTotalChars,
            "The body must stay inside its budget.");
        Assert.EndsWith("[output truncated]", result.Content);
    }

    [Fact]
    public void Format_SaysSoWhenTheServerReturnedNothing()
    {
        var result = Formatter().Format(Tool("echo"), new JObject());

        Assert.Equal("(the server returned no content)", result.Content);
    }

    [Fact]
    public void Format_LabelsAContentTypeItDoesNotKnow()
    {
        var result = Formatter().Format(Tool("echo"), Result(new JObject { ["type"] = "video" }));

        Assert.Contains("[video content omitted]", result.Content, StringComparison.Ordinal);
    }

    private static McpResultFormatter Formatter() =>
        new McpResultFormatter((ext, bytes) => "C:\\media\\saved" + ext);

    private static McpToolInfo Tool(string name) => new McpToolInfo
    {
        Server = new McpServerConfiguration { Name = "Files", Command = "fake" },
        Name = name
    };

    private static JObject Result(params JObject[] blocks) => new JObject
    {
        ["content"] = new JArray(blocks)
    };

    private static JObject Block(string type, string text) => new JObject
    {
        ["type"] = type,
        ["text"] = text
    };
}
