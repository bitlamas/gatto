using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Models;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class SpecialTokenEscapeTests
{
    private static readonly string[] Vocabulary = ["<|im_start|>", "<|endoftext|>", "<think>", "</think>", "[|END|]", "[|", "|]"];

    private static SpecialTokenEscape Escape() => SpecialTokenEscape.From(Vocabulary)!;

    private static readonly char Zw = Escape().Break;

    private static string Broken(string token) => $"{token[0]}{Zw}{token[1..]}";

    [Fact]
    public void A_token_in_text_takes_a_break_after_its_first_character()
    {
        var sent = Escape().Escape("a literal </think> and <|im_start|>user");

        Assert.Equal($"a literal {Broken("</think>")} and {Broken("<|im_start|>")}user", sent);
    }

    [Fact]
    public void Text_with_no_token_comes_back_as_the_same_instance()
    {
        var text = "if (a < b) return list[0] | mask; //nothing special";

        Assert.Same(text, Escape().Escape(text));
    }

    [Fact]
    public void A_token_inside_a_longer_token_is_broken_too()
    {
        var sent = Escape().Escape("x [|END|] y");

        Assert.Equal($"x [{Zw}|END|{Zw}] y", sent);
        foreach (var token in Vocabulary) Assert.DoesNotContain(token, sent, StringComparison.Ordinal);
    }

    [Fact]
    public void No_token_survives_in_escaped_text()
    {
        var text = string.Join(" ", Vocabulary) + string.Concat(Vocabulary);

        var sent = Escape().Escape(text);

        foreach (var token in Vocabulary) Assert.DoesNotContain(token, sent, StringComparison.Ordinal);
    }

    [Fact]
    public void Mend_gives_back_the_text_that_was_escaped()
    {
        var text = "a <think> b [|END|] c <|endoftext|><|im_start|> d";

        Assert.Equal(text, Escape().Mend(Escape().Escape(text)));
    }

    [Fact]
    public void Mend_keeps_a_zero_width_space_that_breaks_no_token()
    {
        var text = $"plain{Zw}word and <{Zw}div> and <|im_{Zw}start|>";

        Assert.Same(text, Escape().Mend(text));
    }

    [Fact]
    public void Escaping_escaped_text_changes_nothing()
    {
        var once = Escape().Escape("a <think> b");

        Assert.Same(once, Escape().Escape(once));
    }

    [Fact]
    public void A_vocabulary_marking_a_word_or_spaces_breaks_no_prose()
    {
        var escape = SpecialTokenEscape.From(["hello", "    ", "<x>", "a>", "<b", "|"]);

        Assert.Equal($"hello     {Broken("<x>")} a> <b |", escape!.Escape("hello     <x> a> <b |"));
    }

    [Fact]
    public void A_vocabulary_with_no_qualifying_token_gives_no_escape()
    {
        Assert.Null(SpecialTokenEscape.From(["hello", " ", "x"]));
    }

    [Fact]
    public void Arguments_are_escaped_in_their_values_and_stay_valid_json()
    {
        var args = """{"path":"a.cs","content":"var s = \"<|im_start|>user\";\n"}""";

        var sent = Escape().EscapeJson(args);

        using var doc = JsonDocument.Parse(sent);
        Assert.Equal($"var s = \"{Broken("<|im_start|>")}user\";\n", doc.RootElement.GetProperty("content").GetString());
        Assert.Equal("a.cs", doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void A_token_written_with_a_json_escape_is_still_broken()
    {
        var args = "{\"content\":\"\\u003c|im_start|>\"}";

        var sent = Escape().EscapeJson(args);

        using var doc = JsonDocument.Parse(sent);
        Assert.Equal(Broken("<|im_start|>"), doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Arguments_with_no_token_keep_their_bytes()
    {
        var args = """{ "path" : "a.cs",  "content":"x < y\n", "n": 1.50 }""";

        Assert.Same(args, Escape().EscapeJson(args));
        Assert.Same(args, Escape().MendJson(args));
    }

    [Fact]
    public void Arguments_that_are_not_json_come_back_untouched()
    {
        var args = "{\"content\":\"<think>";

        Assert.Same(args, Escape().EscapeJson(args));
    }

    [Fact]
    public void A_break_the_model_copied_into_its_arguments_is_taken_out()
    {
        var raw = $"{{\"old_string\":\"    \\\"{Broken("<|im_start|>")}user\\\",\",\"nested\":[\"{Broken("<think>")}\"]}}";
        var escaped = "{\"old_string\":\"<\\u200b|im_start|>user\"}";

        using var first = JsonDocument.Parse(Escape().MendJson(raw));
        using var second = JsonDocument.Parse(Escape().MendJson(escaped));

        Assert.Equal("    \"<|im_start|>user\",", first.RootElement.GetProperty("old_string").GetString());
        Assert.Equal("<think>", first.RootElement.GetProperty("nested")[0].GetString());
        Assert.Equal("<|im_start|>user", second.RootElement.GetProperty("old_string").GetString());
    }

    [Fact]
    public void A_message_goes_out_with_every_text_channel_escaped()
    {
        var call = new ToolCall("c1", "write_file", """{"content":"<think>"}""");
        var assistant = new ChatMessage("assistant", "see </think>", ToolCalls: [call], ReasoningContent: "about <think>");

        using var doc = JsonDocument.Parse(RequestJson.SerialiseMessage(assistant, escape: Escape()));
        var root = doc.RootElement;

        Assert.Equal($"see {Broken("</think>")}", root.GetProperty("content").GetString());
        Assert.Equal($"about {Broken("<think>")}", root.GetProperty("reasoning_content").GetString());
        var sentArgs = root.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString()!;
        using var args = JsonDocument.Parse(sentArgs);
        Assert.Equal(Broken("<think>"), args.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void A_message_serialises_the_same_with_and_without_an_escape_when_it_holds_no_token()
    {
        var m = new ChatMessage("tool", "plain output < 3", ToolCallId: "c1");

        Assert.Equal(RequestJson.SerialiseMessage(m), RequestJson.SerialiseMessage(m, escape: Escape()));
    }

    [Fact]
    public void The_escape_of_a_message_does_not_depend_on_the_messages_around_it()
    {
        var m = new ChatMessage("user", "strip <think> and </think>");

        Assert.Equal(RequestJson.SerialiseMessage(m, escape: Escape()), RequestJson.SerialiseMessage(m, escape: Escape()));
    }

    private static string Chunk(string deltaJson, string? finish = null) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}\n\n";

    private static async Task<List<StreamEvent>> Collect(IChatClient c, ChatRequest req)
    {
        var events = new List<StreamEvent>();
        await foreach (var e in c.StreamAsync(req)) events.Add(e);
        return events;
    }

    [Fact]
    public async Task THE_CLIENT_SENDS_ESCAPED_TEXT_AND_MENDS_THE_ARGUMENTS_IT_RECEIVES()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[]
        {
            Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"edit_file\",\"arguments\":\"{\\\"old_string\\\":\\\"<\\\\u200b|im_start|>user\\\"}\"}}]}"),
            Chunk("{}", finish: "tool_calls"),
            "data: [DONE]\n\n",
        }));
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(s.BaseUrl)) { Specials = Escape };
        var req = new ChatRequest("m", new[]
        {
            new ChatMessage("system", "never print <|endoftext|>"),
            new ChatMessage("user", "a literal </think> here"),
        });

        var events = await Collect(client, req);

        var msgs = s.LastRequestBody!.Value.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal($"never print {Broken("<|endoftext|>")}", msgs[0].GetProperty("content").GetString());
        Assert.Equal($"a literal {Broken("</think>")} here", msgs[1].GetProperty("content").GetString());
        var ready = Assert.Single(events.OfType<StreamEvent.ToolCallReady>());
        using var args = JsonDocument.Parse(ready.Call.ArgumentsJson);
        Assert.Equal("<|im_start|>user", args.RootElement.GetProperty("old_string").GetString());
    }

    [Fact]
    public async Task A_CLIENT_WITH_NO_VOCABULARY_SENDS_THE_TEXT_AS_IT_IS()
    {
        await using var s = new FakeOpenAiServer();
        s.Enqueue(new FakeResponse(Frames: new[] { Chunk("{\"content\":\"ok\"}", finish: "stop"), "data: [DONE]\n\n" }));
        var client = new OpenAiCompatClient(new HttpClient(), "cloud", new EndpointConfig(s.BaseUrl));

        await Collect(client, new ChatRequest("m", new[] { new ChatMessage("user", "a literal </think> here") }));

        var sent = s.LastRequestBody!.Value.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal("a literal </think> here", sent);
    }

    private static byte[] Vocab(string[] tokens, int[] types, bool typesFirst = false)
    {
        void Tokens(GgufTestBytes.Kv kv) => kv.Raw(w =>
        {
            Str(w, "tokenizer.ggml.tokens");
            w.Write(9u);
            w.Write(8u);
            w.Write((ulong)tokens.Length);
            foreach (var t in tokens) Str(w, t);
        });
        void Types(GgufTestBytes.Kv kv) => kv.Raw(w =>
        {
            Str(w, "tokenizer.ggml.token_type");
            w.Write(9u);
            w.Write(5u);
            w.Write((ulong)types.Length);
            foreach (var t in types) w.Write(t);
        });
        return GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "invented");
            if (typesFirst) Types(kv);
            Tokens(kv);
            kv.U32("invented.context_length", 4096);
            if (!typesFirst) Types(kv);
            kv.Str("tokenizer.chat_template", "{{ messages }}");
        });
    }

    private static void Str(BinaryWriter w, string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        w.Write((ulong)b.Length);
        w.Write(b);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_header_gives_its_control_and_user_defined_tokens(bool typesFirst)
    {
        var bytes = Vocab(["hello", "<|im_start|>", "world", "<think>", "<unused1>", "tail"], [1, 3, 1, 4, 5, 1], typesFirst);

        var special = GgufHeaderParser.SpecialTokens(new MemoryStream(bytes));

        Assert.Equal(["<|im_start|>", "<think>"], special);
    }

    [Fact]
    public void A_header_cut_inside_its_vocabulary_is_not_read_as_a_vocabulary_with_no_token()
    {
        var bytes = Vocab(["hello", "<|im_start|>", "<think>"], [1, 3, 4]);

        for (var cut = 0; cut < bytes.Length - 64; cut += 7)
            Assert.Null(GgufHeaderParser.SpecialTokens(new MemoryStream(bytes[..cut])));
    }

    [Fact]
    public void A_header_whose_two_arrays_disagree_in_length_is_not_read()
    {
        Assert.Null(GgufHeaderParser.SpecialTokens(new MemoryStream(Vocab(["a", "<think>"], [1, 4, 4]))));
    }

    [Fact]
    public void A_token_count_larger_than_the_file_sizes_nothing_and_is_not_read()
    {
        var bytes = GgufTestBytes.SyntheticHeader(kv => kv.Raw(w =>
        {
            Str(w, "tokenizer.ggml.tokens");
            w.Write(9u);
            w.Write(8u);
            w.Write(9_000_000UL);
        }));

        Assert.Null(GgufHeaderParser.SpecialTokens(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_model_file_is_read_once_and_gives_its_escape()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-specials-");
        try
        {
            var path = Path.Combine(dir.FullName, "invented.gguf");
            File.WriteAllBytes(path, Vocab(["hello", "<think>"], [1, 4]));

            var first = SpecialTokenEscape.ForModelFile(path, out var unreadable);

            Assert.NotNull(first);
            Assert.False(unreadable);
            Assert.Same(first, SpecialTokenEscape.ForModelFile(path, out _));
            Assert.Equal(Broken("<think>"), first!.Escape("<think>"));
            Assert.Null(SpecialTokenEscape.ForModelFile(Path.Combine(dir.FullName, "absent.gguf"), out var absent));
            Assert.True(absent);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_header_with_no_vocabulary_is_read_and_holds_no_token()
    {
        var bytes = GgufTestBytes.SyntheticHeader(kv => kv.Str("general.architecture", "invented"));

        Assert.Empty(GgufHeaderParser.SpecialTokens(new MemoryStream(bytes))!);
    }

    [Fact]
    public void A_FILE_THAT_COULD_NOT_BE_READ_SAYS_SO_AND_IS_ASKED_AGAIN()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-specials-");
        try
        {
            var path = Path.Combine(dir.FullName, "cut.gguf");
            var whole = Vocab(["hello", "<think>"], [1, 4]);
            File.WriteAllBytes(path, whole[..(whole.Length / 2)]);

            Assert.Null(SpecialTokenEscape.ForModelFile(path, out var cut));
            Assert.True(cut);

            File.WriteAllBytes(path, whole);
            var read = SpecialTokenEscape.ForModelFile(path, out var stillUnreadable);

            Assert.False(stillUnreadable);
            Assert.NotNull(read);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_file_with_no_qualifying_token_is_readable_and_gives_no_escape()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-specials-");
        try
        {
            var path = Path.Combine(dir.FullName, "plain.gguf");
            File.WriteAllBytes(path, Vocab(["hello", "world"], [1, 1]));

            Assert.Null(SpecialTokenEscape.ForModelFile(path, out var unreadable));
            Assert.False(unreadable);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_vocabulary_that_holds_the_break_gets_another_break()
    {
        var first = SpecialTokenEscape.From(["<think>"])!.Break;
        var escape = SpecialTokenEscape.From(["<think>", first.ToString(), $"<{first}x>"])!;

        Assert.NotEqual(first, escape.Break);
        Assert.Equal($"<{escape.Break}think>", escape.Escape("<think>"));
        Assert.Equal("<think>", escape.Mend(escape.Escape("<think>")));
        Assert.DoesNotContain(first.ToString(), escape.Escape("a <think> b"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_is_a_token_is_escaped_and_mended_like_a_value()
    {
        var args = """{"<think>":"x","plain":{"</think>":1}}""";

        var sent = Escape().EscapeJson(args);

        Assert.DoesNotContain("<think>", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("</think>", sent, StringComparison.Ordinal);
        using var back = JsonDocument.Parse(Escape().MendJson(sent));
        Assert.Equal("x", back.RootElement.GetProperty("<think>").GetString());
        Assert.Equal(1, back.RootElement.GetProperty("plain").GetProperty("</think>").GetInt32());
    }

    private static string Tool(ToolSpec t, SpecialTokenEscape? escape)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) RequestJson.WriteTool(w, t, escape);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public void A_tool_goes_out_with_its_description_and_schema_text_escaped()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string","description":"the <think> id"}}}""");
        var tool = new ToolSpec("shell", "prints <|im_start|> when asked", schema.RootElement.Clone());

        var sent = Tool(tool, Escape());

        foreach (var token in Vocabulary) Assert.DoesNotContain(token, sent, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(sent);
        var function = doc.RootElement.GetProperty("function");
        Assert.Equal($"prints {Broken("<|im_start|>")} when asked", function.GetProperty("description").GetString());
        Assert.Equal($"the {Broken("<think>")} id",
            function.GetProperty("parameters").GetProperty("properties").GetProperty("id").GetProperty("description").GetString());
    }

    [Fact]
    public void A_tool_with_no_token_goes_out_with_the_bytes_it_has_without_an_escape()
    {
        using var schema = JsonDocument.Parse("""{ "type":"object",  "properties":{"n":{"type":"integer","minimum":1.50}} }""");
        var tool = new ToolSpec("count", "counts a < b", schema.RootElement.Clone());

        Assert.Equal(Tool(tool, null), Tool(tool, Escape()));
    }

    [Fact]
    public void The_stub_of_a_missing_picture_named_with_a_token_holds_no_token()
    {
        var missing = new ImageRef(Path.Combine(Path.GetTempPath(), "gatto-absent-" + Guid.NewGuid().ToString("N") + ".png"), 9, "00", "image/png", "<think>.png");
        var m = new ChatMessage("user", "look", Images: [missing]);

        var sent = RequestJson.SerialiseMessage(m, new ImageCache(), _ => { }, Escape());

        Assert.Contains("unavailable", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("<think>", sent, StringComparison.Ordinal);
    }
}
