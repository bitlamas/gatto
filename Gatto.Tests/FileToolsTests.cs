using System.Text.Json;
using Gatto.Core.Tools;

namespace Gatto.Tests;

public sealed class TestToolContext(string cwd) : IToolContext
{
    public string Cwd => cwd;
    public string HomePath => cwd;
    public IUserPrompter? Prompter => null;
}

public class FileToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-test-").FullName;
    private IToolContext Ctx => new TestToolContext(_dir);
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Read_write_roundtrip_with_relative_path()
    {
        var write = new WriteFileTool();
        var read = new ReadFileTool();
        await write.ExecuteAsync(Args(new { path = "sub/a.txt", content = "hello" }), Ctx, default);
        var r = await read.ExecuteAsync(Args(new { path = "sub/a.txt" }), Ctx, default);
        Assert.Equal("hello", r.Text);
        Assert.False(r.IsError);
    }

    [Fact]
    public async Task Read_missing_file_throws_with_actionable_message()
    {
        var read = new ReadFileTool();
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => read.ExecuteAsync(Args(new { path = "nope.txt" }), Ctx, default));
        Assert.Contains("nope.txt", ex.Message);
    }

    //reading a binary file as text is never what the caller wanted, so the tool throws and the loop reports that as an error result

    [Fact]
    public async Task Read_refuses_a_jpeg_and_names_the_format()
    {
        //write the real jpeg magic bytes and its JFIF tag, so the detector meets the shape a real jpeg has
        File.WriteAllBytes(Path.Combine(_dir, "image.jpg"),
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReadFileTool().ExecuteAsync(Args(new { path = "image.jpg" }), Ctx, default));

        Assert.Contains("image.jpg", ex.Message, StringComparison.Ordinal);
        Assert.Contains("JPEG", ex.Message, StringComparison.Ordinal);   //assert the format name, since a bare binary tells the model nothing
    }

    [Fact]
    public async Task Read_refuses_a_png()
    {
        File.WriteAllBytes(Path.Combine(_dir, "shot.png"),
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReadFileTool().ExecuteAsync(Args(new { path = "shot.png" }), Ctx, default));

        Assert.Contains("PNG", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_refuses_an_unrecognised_binary_by_its_nul_bytes()
    {
        File.WriteAllBytes(Path.Combine(_dir, "blob.dat"),
            new byte[] { 0x01, 0x02, 0x00, 0x03, 0x04, 0x00, 0x05 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReadFileTool().ExecuteAsync(Args(new { path = "blob.dat" }), Ctx, default));

        Assert.Contains("blob.dat", ex.Message, StringComparison.Ordinal);
        Assert.Contains("binary", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_still_reads_ordinary_text()
    {
        File.WriteAllText(Path.Combine(_dir, "fine.txt"), "line one" + Environment.NewLine + "line two");
        var r = await new ReadFileTool().ExecuteAsync(Args(new { path = "fine.txt" }), Ctx, default);
        Assert.Contains("line one", r.Text, StringComparison.Ordinal);
        Assert.False(r.IsError);
    }

    [Fact]
    public async Task Read_does_not_mistake_utf16_text_for_binary()
    {
        //utf-16 encodes ASCII with a NUL after each character, so a bare NUL check calls ordinary text binary. refusing such a file would be worse than no guard
        File.WriteAllText(Path.Combine(_dir, "utf16.txt"), "hello there", new System.Text.UnicodeEncoding(false, true));
        var r = await new ReadFileTool().ExecuteAsync(Args(new { path = "utf16.txt" }), Ctx, default);
        Assert.Contains("hello there", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_treats_an_empty_file_as_text()
    {
        File.WriteAllBytes(Path.Combine(_dir, "empty.txt"), Array.Empty<byte>());
        var r = await new ReadFileTool().ExecuteAsync(Args(new { path = "empty.txt" }), Ctx, default);
        Assert.Equal("", r.Text);
    }

    [Fact]
    public async Task Read_offset_limit_window()
    {
        File.WriteAllLines(Path.Combine(_dir, "n.txt"), new[] { "l1", "l2", "l3", "l4" });
        var r = await new ReadFileTool().ExecuteAsync(
            Args(new { path = "n.txt", offset = 2, limit = 2 }), Ctx, default);
        Assert.Equal("l2" + Environment.NewLine + "l3", r.Text);
    }

    [Fact]
    public async Task Edit_requires_unique_match()
    {
        File.WriteAllText(Path.Combine(_dir, "e.txt"), "aa bb aa");
        var edit = new EditFileTool();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => edit.ExecuteAsync(Args(new { path = "e.txt", old_string = "aa", new_string = "cc" }), Ctx, default));
        Assert.Contains("2 times", ex.Message);

        await edit.ExecuteAsync(Args(new { path = "e.txt", old_string = "bb", new_string = "cc" }), Ctx, default);
        Assert.Equal("aa cc aa", File.ReadAllText(Path.Combine(_dir, "e.txt")));
    }

    [Fact]
    public async Task Missing_required_arg_throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => new WriteFileTool().ExecuteAsync(Args(new { path = "x.txt" }), Ctx, default));
        Assert.Contains("content", ex.Message);
    }

    [Fact]
    public async Task Edit_empty_old_string_throws_immediately()
    {
        File.WriteAllText(Path.Combine(_dir, "e2.txt"), "content");
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => new EditFileTool().ExecuteAsync(Args(new { path = "e2.txt", old_string = "", new_string = "x" }), Ctx, default));
        Assert.Contains("must not be empty", ex.Message);
    }

    [Fact]
    public async Task Write_reports_utf8_byte_count_not_char_count()
    {
        var r = await new WriteFileTool().ExecuteAsync(Args(new { path = "u.txt", content = "hello 世界" }), Ctx, default);
        Assert.Contains("wrote 12 bytes", r.Text);   //the content is 8 characters but 12 bytes in utf-8, so the count must be bytes.
    }

    [Fact]
    public async Task ReadFile_GlossIsLineCount()
    {
        File.WriteAllText(Path.Combine(_dir, "g.txt"), "a\nb\nc");
        var r = await new ReadFileTool().ExecuteAsync(Args(new { path = "g.txt" }), Ctx, default);
        Assert.Equal("3 lines", r.Gloss);
    }

    //one is the only count whose wording differs, so it separates the helper from the bug. the write gloss is a separate path and needs its own test
    [Fact]
    public async Task ReadFile_GlossSaysOneLine_notOneLines()
    {
        File.WriteAllText(Path.Combine(_dir, "one.txt"), "just the one");
        var r = await new ReadFileTool().ExecuteAsync(Args(new { path = "one.txt" }), Ctx, default);
        Assert.Equal("1 line", r.Gloss);
    }

    [Fact]
    public async Task WriteFile_GlossIsBytes()
    {
        var r = await new WriteFileTool().ExecuteAsync(Args(new { path = "out.txt", content = "hello" }), Ctx, default);
        Assert.Equal("wrote 5 bytes", r.Gloss);
    }

    //the tool looks before it writes, the gloss says overwrote and the model's text stays as it was
    [Fact]
    public async Task WriteFile_over_a_file_glosses_overwrote()
    {
        File.WriteAllText(Path.Combine(_dir, "old.txt"), "before");
        var r = await new WriteFileTool().ExecuteAsync(Args(new { path = "old.txt", content = "hello" }), Ctx, default);
        Assert.Equal("overwrote 5 bytes", r.Gloss);
        Assert.StartsWith("wrote 5 bytes to ", r.Text, StringComparison.Ordinal);
    }

    //the same singular check on the write gloss, which names bytes rather than lines. each gloss builds its own text, so a case for one says nothing about the other
    [Fact]
    public async Task WriteFile_GlossSaysOneByte_notOneBytes()
    {
        var r = await new WriteFileTool().ExecuteAsync(Args(new { path = "one.txt", content = "x" }), Ctx, default);
        Assert.Equal("wrote 1 byte", r.Gloss);
    }

    [Fact]
    public async Task EditFile_GlossIsOk()
    {
        File.WriteAllText(Path.Combine(_dir, "eg.txt"), "old text");
        var r = await new EditFileTool().ExecuteAsync(Args(new { path = "eg.txt", old_string = "old", new_string = "new" }), Ctx, default);
        Assert.Equal("ok", r.Gloss);
    }

    [Fact]
    public async Task WriteFile_AtDriveRoot_NoNullDirectoryCrash()
    {
        //a drive root has no parent directory, so the tool must raise a real I/O error (ArgumentNullException or NullReferenceException would be a bug)
        var args = JsonDocument.Parse("""{"path":"C:\\","content":"x"}""").RootElement;
        var ex = await Record.ExceptionAsync(() => new WriteFileTool().ExecuteAsync(args, Ctx, default));
        Assert.NotNull(ex);
        Assert.IsNotType<ArgumentNullException>(ex);
        Assert.IsNotType<NullReferenceException>(ex);
    }

    [Fact]
    public async Task ReadFile_gloss_counts_the_lines_actually_returned()
    {
        //a gloss that counts the whole file while the body is capped makes the model reason over lines it never received. count what was returned
        var lines = Enumerable.Repeat(new string('x', 100), 2_000);   //the file is about 200k characters, far past the cap.
        await File.WriteAllTextAsync(Path.Combine(_dir, "big.txt"), string.Join("\n", lines));

        var r = await new ReadFileTool().ExecuteAsync(Args(new { path = "big.txt" }), Ctx, default);

        var gloss = r.Gloss;
        Assert.NotNull(gloss);
        Assert.Contains("truncated", gloss);
        var reported = int.Parse(gloss.Split(' ')[0]);
        Assert.True(reported < 2_000, $"gloss reported {reported} lines but the file was truncated");
        Assert.Contains("[truncated]", r.Text);
    }

    [Fact]
    public async Task ReadFile_truncation_never_splits_a_surrogate_pair()
    {
        //a cap that splits a surrogate pair leaves a lone surrogate, and that corrupts the request json later.
        var pad = new string('a', 49_999);          //the pad puts the pair exactly at the 50_000 cap, so the cut would split it.
        await File.WriteAllTextAsync(Path.Combine(_dir, "emoji.txt"), pad + "😀" + new string('b', 100));

        var r = await new ReadFileTool().ExecuteAsync(Args(new { path = "emoji.txt" }), Ctx, default);

        Assert.False(HasLoneSurrogate(r.Text), "the capped body left an unpaired surrogate");
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(s[i])) return true;
        }
        return false;
    }

    //a model sends LF in a json argument. raw-byte matching fails every multi-line edit on a crlf file, so the matcher must align line endings

    private string CrlfFile(string name, params string[] lines)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, string.Join("\r\n", lines));
        return p;
    }

    [Fact]
    public async Task Edit_matches_a_multiline_lf_old_string_against_a_crlf_file()
    {
        var p = CrlfFile("crlf.cs", "class A", "{", "    int x = 1;", "}");

        await new EditFileTool().ExecuteAsync(
            Args(new { path = "crlf.cs", old_string = "{\n    int x = 1;", new_string = "{\n    int x = 2;" }), Ctx, default);

        Assert.Contains("int x = 2;", await File.ReadAllTextAsync(p));
    }

    [Fact]
    public async Task Edit_keeps_the_files_crlf_endings_when_new_string_uses_lf()
    {
        //an edit that writes bare lf into a crlf file leaves mixed endings, and the next exact match then fails.
        var p = CrlfFile("mix.cs", "one", "two", "three");

        await new EditFileTool().ExecuteAsync(
            Args(new { path = "mix.cs", old_string = "one\ntwo", new_string = "one\nTWO\nextra" }), Ctx, default);

        var text = await File.ReadAllTextAsync(p);
        Assert.DoesNotContain('\n', text.Replace("\r\n", ""));   //strip every CRLF pair first, then require that no lone LF remains
        Assert.Contains("TWO", text);
        Assert.Contains("extra", text);
    }

    [Fact]
    public async Task Edit_on_an_lf_file_is_unchanged()
    {
        var p = Path.Combine(_dir, "lf.cs");
        await File.WriteAllTextAsync(p, "one\ntwo\nthree");

        await new EditFileTool().ExecuteAsync(
            Args(new { path = "lf.cs", old_string = "one\ntwo", new_string = "one\nTWO" }), Ctx, default);

        Assert.Equal("one\nTWO\nthree", await File.ReadAllTextAsync(p));
    }

    [Fact]
    public async Task Edit_exact_crlf_old_string_still_matches()
    {
        //try the literal match first, so a model that echoes the file's crlf bytes still matches exactly.
        var p = CrlfFile("exact.cs", "alpha", "beta");

        await new EditFileTool().ExecuteAsync(
            Args(new { path = "exact.cs", old_string = "alpha\r\nbeta", new_string = "alpha\r\nGAMMA" }), Ctx, default);

        Assert.Equal("alpha\r\nGAMMA", await File.ReadAllTextAsync(p));
    }

    //the model reads these two texts, so a move of the match must keep them to the character
    [Fact]
    public async Task Edit_errors_keep_their_exact_text()
    {
        var p = Path.Combine(_dir, "two.cs");
        await File.WriteAllTextAsync(p, "x\ny\nx\n");
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EditFileTool().ExecuteAsync(Args(new { path = "two.cs", old_string = "x", new_string = "z" }), Ctx, default));
        Assert.Equal($"old_string occurs 2 times in {p} {(char)0x2014} must be unique", twice.Message);
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EditFileTool().ExecuteAsync(Args(new { path = "two.cs", old_string = "absent", new_string = "z" }), Ctx, default));
        Assert.Equal($"old_string not found in {p}", none.Message);
    }

    [Fact]
    public async Task Edit_non_unique_after_alignment_still_throws()
    {
        CrlfFile("dup.cs", "x", "y", "x", "y");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EditFileTool().ExecuteAsync(
                Args(new { path = "dup.cs", old_string = "x\ny", new_string = "z" }), Ctx, default));

        Assert.Contains("must be unique", ex.Message);
    }

    [Fact]
    public async Task Edit_genuinely_absent_old_string_still_throws()
    {
        CrlfFile("absent.cs", "alpha", "beta");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EditFileTool().ExecuteAsync(
                Args(new { path = "absent.cs", old_string = "nope\nnothing", new_string = "z" }), Ctx, default));

        Assert.Contains("not found", ex.Message);
    }
}
