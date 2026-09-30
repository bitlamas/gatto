using System.Reflection;
using System.Text.RegularExpressions;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//the renderer only commits finished lines and repaints the tail, so every assertion here reads the replayed screen or the byte stream
public class StreamRendererTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static StatusInfo Status(string role) =>
        new(@"C:\proj", "qwen", role, new CtxState(), @"C:\Users\x");

    private static (StreamRenderer R, RecordingSurface S, ChromePainter P, ChromeTicker K) Make(
        int width = 80, int height = 0, string role = "coder", ReasoningMode reasoning = ReasoningMode.Collapsed,
        Func<DateTime>? now = null)
    {
        var s = new RecordingSurface { Width = width, Height = height };
        var gate = new object();
        var painter = new ChromePainter(s, T, gate, new TranscriptModel(role))
        {
            Frame = new InputFrame(s, T, role, Status(role), glyphs: GlyphSet.Unicode),
            RoleForTint = role,
        };
        var ticker = new ChromeTicker(painter, gate);
        var r = new StreamRenderer(painter, s, T, role, ticker, gate, reasoning: reasoning, now: now,
            model: painter.Model, convoTail: () => null);
        return (r, s, painter, ticker);
    }

    //a renderer whose surface throws from Width: the first paint faults and every later event writes plain bytes to Inner
    private static (StreamRenderer R, ThrowingWidthSurface S) Degraded()
    {
        var evil = new ThrowingWidthSurface();
        var gate = new object();
        var painter = new ChromePainter(evil, T, gate)
        {
            Frame = new InputFrame(evil, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode),
        };
        var r = new StreamRenderer(painter, evil, T, "coder", new ChromeTicker(painter, gate), gate);
        r.OnTextDelta("## x\n");   //the Width getter throws inside this commit, which is what degrades the renderer
        evil.Inner.Clear();
        return (r, evil);
    }

    //a minimal terminal emulator: it follows the cursor motion the painter emits, drops other escapes, and counts columns in characters
    private static string Visible(RecordingSurface s)
    {
        var lines = new List<System.Text.StringBuilder> { new() };
        int row = 0, col = 0;
        void Ensure() { while (lines.Count <= row) lines.Add(new System.Text.StringBuilder()); }
        var text = s.Text;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\x1b')
            {
                if (i + 1 < text.Length && text[i + 1] == '[')   //a CSI sequence runs to its final byte
                {
                    var j = i + 2;
                    while (j < text.Length && !(text[j] >= '@' && text[j] <= '~')) j++;
                    if (j >= text.Length) break;
                    var fin = text[j];
                    _ = int.TryParse(text.Substring(i + 2, j - (i + 2)), out var n);
                    if (fin == 'A') row = Math.Max(0, row - Math.Max(1, n));
                    else if (fin == 'B') { row += Math.Max(1, n); Ensure(); }
                    else if (fin == 'G') col = Math.Max(0, n - 1);
                    else if (fin == 'J' || fin == 'K')   //the J arm clears below the cursor and the K arm clears to the end of the line, param 0 for both
                    {
                        Ensure();
                        if (col < lines[row].Length) lines[row].Length = col;
                        if (fin == 'J') while (lines.Count > row + 1) lines.RemoveAt(lines.Count - 1);
                    }
                    i = j + 1;
                    continue;
                }
                if (i + 1 < text.Length && text[i + 1] == ']')   //an OSC sequence ends at ST or BEL
                {
                    var j = i + 2;
                    while (j < text.Length)
                    {
                        if (text[j] == '\x07') { j++; break; }
                        if (text[j] == '\x1b' && j + 1 < text.Length && text[j + 1] == '\\') { j += 2; break; }
                        j++;
                    }
                    i = j;
                    continue;
                }
                i++;   //an ESC with nothing after it is dropped
                continue;
            }
            if (c == '\n') { row++; col = 0; Ensure(); i++; continue; }
            if (c == '\r') { col = 0; i++; continue; }
            Ensure();
            var line = lines[row];
            while (line.Length < col) line.Append(' ');
            if (col < line.Length) line[col] = c; else line.Append(c);
            col++;
            i++;
        }
        return string.Join('\n', lines.Select(l => l.ToString()));
    }

    //these fixtures use a width where InputFrame still draws the frame's rules, since it drops them below 20 cells

    //the transcript is the TranscriptModel, so these fixtures assert its committed rows in order, ANSI stripped, and leave the tail and composer out

    //the chip's own SGR run, from its foreground introducer to the next reset, because the transcript carries other backgrounds too
    private static string ChipRun(string raw, string fg)
    {
        var i = raw.IndexOf(fg, StringComparison.Ordinal);
        Assert.True(i >= 0, "no chip found — the assertion below would pass vacuously");
        var end = raw.IndexOf(Ansi.Reset, i, StringComparison.Ordinal);
        return end < 0 ? raw[i..] : raw[i..end];
    }

    private static string[] Rows(ChromePainter p) =>
        p.Model.Items.SelectMany(i => i.Render(p.Width, T, glyphs: GlyphSet.Unicode))
            .Select(r => TermText.StripAnsiForWidth(r).TrimEnd()).ToArray();

    //the committed rows with ANSI intact, the counterpart of Rows (which strips it)
    private static string[] RawRows(ChromePainter p) =>
        p.Model.Items.SelectMany(i => i.Render(p.Width, T, glyphs: GlyphSet.Unicode)).ToArray();

    private static int RowOf(string[] rows, string needle)
    {
        var i = Array.FindIndex(rows, r => r.Contains(needle, StringComparison.Ordinal));
        Assert.True(i >= 0, $"row containing '{needle}' not found in:\n{string.Join("\n", rows)}");
        return i;
    }

    //the row above the anchor is blank and the one above that is not
    private static void AssertOneBlankRowBefore(string[] rows, string anchor)
    {
        var i = RowOf(rows, anchor);
        Assert.True(i > 0, $"'{anchor}' is the first row — expected a blank separator above it");
        Assert.Equal("", rows[i - 1]);
        Assert.True(i - 2 < 0 || rows[i - 2].Length > 0,
            $"expected exactly one blank row before '{anchor}':\n{string.Join("|", rows)}");
    }

    private static int EscCount(string s) => s.Count(c => c == '\x1b');
    private static int CrCount(string s) => s.Count(c => c == '\r');

    private static long TokenChars(ChromeTicker t) => (long)t.GetType()
        .GetField("_tokenChars", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(t)!;

    //the commit and tail model

    [Fact]
    public void Delta_RepaintsChrome_NeverEchoesBytes()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        s.Clear();
        r.OnTextDelta("hel");

        //nothing is committed yet: the partial line lives in the chrome tail
        Assert.Equal("hel", p.State.TailRaw);
        Assert.NotNull(p.State.TailMarker);                      //the partial line opens a block, so it has a marker
        //every byte came out of a painter paint inside a sync window, so nothing was echoed straight to the surface
        Assert.StartsWith(Ansi.SyncStart, s.Text, StringComparison.Ordinal);
        Assert.EndsWith(Ansi.SyncEnd, s.Text, StringComparison.Ordinal);
        //the partial line stays chrome until it is committed
    }

    [Fact]
    public void CompletedLine_CommitsGutteredRow_TailCleared()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("hello ");
        r.OnTextDelta("world\n");
        r.EndTurn();

        Assert.Null(p.State.TailRaw);
        Assert.Equal("● hello world", Rows(p)[0]);
    }

    //ending the turn commits an open tail line, the case a stream dropping mid-line needs. every other EndTurn test here ends its input in a newline
    [Fact]
    public void EndTurn_CommitsTheUnterminatedTail()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("no trailing newline");   //the stream drops here, so no newline ever arrives
        r.EndTurn();

        Assert.Null(p.State.TailRaw);                        //the commit clears the tail chrome
        Assert.Equal("● no trailing newline", Rows(p)[0]);   //the tail line became a transcript row
    }

    [Fact]
    public void SecondLineOfBlock_HangsUnderTheMarker()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("alpha\nbeta\n");
        r.EndTurn();

        var rows = Rows(p);
        Assert.Equal("● alpha", rows[0]);
        Assert.Equal("  beta", rows[1]);
    }

    [Fact]
    public void Heading_CommitsFullyStyled()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("## Cha");
        r.OnTextDelta("nges\n");
        r.EndTurn();

        Assert.Contains(Ansi.Bold, s.Text, StringComparison.Ordinal);   //the heading style is applied at commit
        Assert.Equal("● Changes", Rows(p)[0]);                          //the ## markers are not drawn
    }

    //styling runs once, at commit, so no paragraph length or height can drop it back to raw markdown
    [Fact]
    public void Paragraph_OverEightRows_CommitsFullyStyled_NoRepaintCap()
    {
        var (r, s, p, _) = Make(width: 20);   //height stays 0, which the renderer reads as unknown
        r.BeginTurn();
        var body = string.Join(" ", Enumerable.Repeat("www", 16)) + " **bold** "
                 + string.Join(" ", Enumerable.Repeat("www", 16)) + " `chip` "
                 + string.Join(" ", Enumerable.Repeat("www", 16)) + "\n";
        r.OnTextDelta(body);
        r.EndTurn();

        var rows = Rows(p).Where(x => x.Length > 0).ToArray();
        Assert.True(rows.Length > 8, $"expected a >8-row paragraph, got {rows.Length}");
        Assert.Contains(Ansi.Bold, s.Text, StringComparison.Ordinal);
        Assert.Contains(Ansi.Fg(T.ChipColor(T.Map(T.RoleTint("coder"))), T.TrueColor), s.Text, StringComparison.Ordinal);   //the chip colour comes from the role accent
        Assert.DoesNotContain("48;", ChipRun(s.Text, Ansi.Fg(T.ChipColor(T.Map(T.RoleTint("coder"))), T.TrueColor)), StringComparison.Ordinal);   //the chip run carries no background colour
        var visible = string.Join("\n", Rows(p));
        Assert.DoesNotContain("**bold**", visible, StringComparison.Ordinal);   //no raw markdown on the screen
        Assert.Single(Regex.Matches(visible, "bold"));                          //the word is drawn exactly once
        Assert.All(rows, row => Assert.True(UnicodeWidth.Of(row) <= 20, $"row over width: '{row}'"));
        Assert.Equal("● ", rows[0][..2]);
        Assert.All(rows.Skip(1), row => Assert.Equal("  ", row[..2]));             //continuation rows are indented two spaces
    }

    [Fact]
    public void CjkParagraph_WrapsWithinWidth_TextStartsAtColumnTwo()
    {
        var (r, s, p, _) = Make(width: 20);
        r.BeginTurn();
        r.OnTextDelta("月球 火星 金星 水星 木星 土星 天王星 海王星\n");
        r.EndTurn();

        var rows = Rows(p).Where(x => x.Length > 0).ToArray();
        Assert.True(rows.Length > 1, "the CJK line must wrap");
        Assert.All(rows, row => Assert.True(UnicodeWidth.Of(row) <= 20, $"row over width: '{row}'"));
        Assert.Contains("月球", rows[0], StringComparison.Ordinal);
        Assert.Contains("土星", string.Join("", rows), StringComparison.Ordinal);   //nothing is dropped in the wrap
    }

    [Fact]
    public void BlankSourceLine_CommitsExactlyOneBlankRow()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("a\n\n\nb\n");   //two blank lines collapse into one separator row
        r.EndTurn();

        var rows = Rows(p);
        Assert.Equal("● a", rows[0]);
        Assert.Equal("", rows[1]);
        Assert.Equal("  b", rows[2]);   //the same prose run keeps hanging, one ● per response
    }

    [Fact]
    public void MarkerReArms_AfterAToolBlock_NotAfterAParagraphBlank()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("first\n\nsecond\n");
        r.OnToolCallStart(new ToolCall("id1", "shell", "{}"));
        r.OnToolResult(new ToolCall("id1", "shell", "{}"), new ToolResult("ok", Gloss: "exit 0"));
        r.OnTextDelta("third\n");
        r.EndTurn();

        var rows = Rows(p);
        Assert.Contains("● first", rows);
        Assert.Contains("  second", rows);   //the second paragraph of the same run hangs too
        Assert.Contains("● third", rows);    //the tool block closed the run, so the ● comes back
    }

    [Fact]
    public void FenceBlock_CommitsBody_ClosingFenceIsStructureOnly()
    {
        var (r, s, p, _) = Make(width: 40);
        r.BeginTurn();
        r.OnTextDelta("```cs\nvar x = 1;\n```\ntail line\n");
        r.EndTurn();

        var vis = Visible(s);
        Assert.DoesNotContain('`', Rows(p).Aggregate("", (a, b) => a + b));   //the backtick of the fence is not drawn
        Assert.Contains("var x = 1;", vis, StringComparison.Ordinal);
        Assert.Contains("tail line", vis, StringComparison.Ordinal);
    }

    //reasoning

    [Fact]
    public void Reasoning_CommitsDimItalicHangRows_NoMarker()
    {
        var (r, s, p, _) = Make(reasoning: ReasoningMode.Expanded);   //expanded mode, so the assertions see the full reasoning render
        r.BeginTurn();
        r.OnReasoningDelta("thinking hard");
        Assert.True(p.State.TailReasoning);
        Assert.Null(p.State.TailMarker);        //a reasoning tail carries no marker
        r.OnReasoningDelta(" about it\n");
        r.EndTurn();

        Assert.Contains("▾ thought for", Rows(p)[0]);              //the expanded header row with its chevron
        Assert.Equal("│", Rows(p)[1]);                              //a blank rail row separates the header from the body
        Assert.Equal("│ thinking hard about it", Rows(p)[2]);
        Assert.Contains(Ansi.Italic, s.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReasoningThenText_ExactlyOneBlankRow()
    {
        var (r, s, p, _) = Make(reasoning: ReasoningMode.Expanded);   //expanded mode, so the assertions see the full reasoning row
        r.BeginTurn();
        r.OnReasoningDelta("thinking.\n");
        r.OnTextDelta("answer.\n");
        r.EndTurn();

        var rows = Rows(p);
        Assert.Contains("▾ thought for", rows[0]);   //the expanded header with its chevron
        Assert.Equal("│ thinking.", rows[2]);
        AssertOneBlankRowBefore(rows, "● answer.");
    }

    //reasoning streams and then auto-collapses at block close

    [Fact]
    public void Reasoning_block_auto_collapses_at_close_and_stamps_wall_clock_elapsed()
    {
        //advance the injected clock, so the twelve-second elapsed is a real reading
        var t = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        var (r, _, p, _) = Make(reasoning: ReasoningMode.Collapsed, now: () => t);
        r.BeginTurn();
        r.OnReasoningDelta("thinking about it.\n");   //opening the block records the start on the injected clock
        t = t.AddSeconds(12);                          //the clock advances while the block is open
        r.OnTextDelta("the answer");                   //the text closes the block, which reads the clock for the elapsed
        var reasoning = Assert.IsType<ReasoningItem>(p.Model.Items[0]);
        Assert.True(reasoning.Collapsed);
        Assert.Equal(TimeSpan.FromSeconds(12), reasoning.Elapsed);
    }

    [Fact]
    public void Reasoning_block_stays_open_under_expanded_mode()
    {
        var (r, _, p, _) = Make(reasoning: ReasoningMode.Expanded);
        r.BeginTurn();
        r.OnReasoningDelta("thinking.\n");
        r.OnTextDelta("answer");
        Assert.False(Assert.IsType<ReasoningItem>(p.Model.Items[0]).Collapsed);
    }

    [Fact]
    public void Passthrough_summary_never_auto_collapses()
    {
        var (r, _, p, _) = Make(reasoning: ReasoningMode.Collapsed);
        r.PassthroughReasoning = true;
        r.BeginTurn();
        r.OnReasoningDelta("A summary sentence. And a second one that must survive in full.\n");
        r.OnTextDelta("after");
        Assert.False(Assert.IsType<ReasoningItem>(p.Model.Items[0]).Collapsed);
    }

    [Fact]
    public void A_manual_expand_sticks_through_close_and_does_not_snap_to_summary()   //a manual expand survives the close
    {
        var (r, _, p, _) = Make(reasoning: ReasoningMode.Collapsed);
        r.BeginTurn();
        r.OnReasoningDelta("thinking about it.\n");   //the block opens collapsed in this mode
        var ri = Assert.IsType<ReasoningItem>(p.Model.Items[0]);
        ri.Collapsed = false; ri.UserToggled = true;  //as if the user pressed Ctrl+R to follow along
        r.OnTextDelta("done");                          //sending text closes the reasoning block
        Assert.False(ri.Collapsed);                     //the block stayed expanded when it closed, so the manual expand stuck
        Assert.False(ri.Streaming);
    }

    [Fact]
    public void Reasoning_only_turn_collapses_on_EndTurn()   //ending the turn also closes the reasoning block
    {
        var (r, _, p, _) = Make(reasoning: ReasoningMode.Collapsed);
        r.BeginTurn();
        r.OnReasoningDelta("thinking with no answer.\n");
        r.EndTurn();                                          //the turn ends mid-reasoning, with no text delta
        var reasoning = Assert.IsType<ReasoningItem>(p.Model.Items[0]);
        Assert.True(reasoning.Collapsed);
        Assert.True(reasoning.Elapsed >= TimeSpan.Zero);
    }

    //tools

    [Fact]
    public void ToolCallStart_IsChrome_NotCommitted()
    {
        var (r, s, p, _) = Make(role: "coder");
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("i", "read_file", """{"path":"a.cs","limit":5}"""));

        //the live tool line reads "a.cs (first 5 lines)", so this fails if that path keeps its own formatter
        Assert.Equal(("read_file", "a.cs (first 5 lines)"), p.State.Tool);   //the bullet is chrome while the tool runs
        Assert.DoesNotContain(Rows(p), row => row.Contains("read_file"));   //the live bullet is not in the transcript
    }

    [Fact]
    public void ToolResult_CommitsTheStaticPair_WithTokGloss()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("i", "read_file", """{"path":"a.cs"}"""));
        r.OnToolResult(new ToolCall("i", "read_file", """{"path":"a.cs"}"""),
            new ToolResult(new string('c', 4000), Gloss: "74 lines"));

        Assert.Null(p.State.Tool);                                //the bullet became a transcript row on the result
        var rows = Rows(p);
        var i = RowOf(rows, "○ read_file a.cs");
        Assert.Equal("  ⎿ ✓ 74 lines · click to expand · ~1.0k tok", rows[i + 1]);   //4000 chars over 4 gives 1000, which KFormat prints as ~1.0k
    }

    //the row helpers strip ANSI, so a check for the bullet's role tint has to read the raw bytes
    [Fact]
    public void ToolResult_CommittedBullet_CarriesRoleTint()
    {
        var (r, s, p, _) = Make(role: "coder");
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("i", "read_file", """{"path":"a.cs"}"""));
        r.OnToolResult(new ToolCall("i", "read_file", """{"path":"a.cs"}"""),
            new ToolResult("ok", Gloss: "1 line"));

        Assert.Contains(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), T.RoleTint("coder")), s.Text, StringComparison.Ordinal);
    }

    //an answered prompt is a moment: its outcome shows as one mark on the tool's result row, and it never reaches the transcript

    //the tool block reaches scrollback and the answered prompt does not, before or after the commit
    [Fact]
    public void CommitPrompt_DuringAToolCall_NeverReachesScrollback()
    {
        var (r, s, p, _) = Make();
        var call = new ToolCall("i", "shell", """{"cmd":"git status"}""");
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.CommitPrompt(new[] { "  allow?", "  ✓ allowed once" });

        Assert.NotNull(p.State.Tool);                                        //the bullet is still chrome at this point
        Assert.DoesNotContain(Rows(p), row => row == "  ✓ allowed once");

        r.OnToolResult(call, new ToolResult("ok", Gloss: "exit 0"));

        var rows = Rows(p);
        var bullet = RowOf(rows, "○ shell git status");
        Assert.Equal("  ⎿ ✓ exit 0 · click to expand · ~0 tok", rows[bullet + 1]);   //the result row sits right under the bullet
        Assert.DoesNotContain(rows, row => row.Contains("allow?", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("allowed once", StringComparison.Ordinal));
        Assert.Equal(bullet + 2, rows.Length);                     //nothing follows the result row
    }

    //one tool call can raise several prompts, so the buffer keeps every block in order while none of them reaches scrollback
    [Fact]
    public void CommitPrompt_CalledTwiceDuringOneToolCall_BuffersBothAndCommitsNeitherToScrollback()
    {
        var (r, s, p, _) = Make();
        var committed = new List<string>();
        r.CommitTap = batch => committed.AddRange(batch);
        var call = new ToolCall("i", "run_agent", """{"agent":"coder"}""");
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.CommitPrompt(new[] { "  allow?", "  ✓ allowed once" });
        r.CommitPrompt(new[] { "  allow?", "  ✗ denied" });

        Assert.NotNull(p.State.Tool);   //the bullet is still chrome, no block has flushed yet

        r.OnToolResult(call, new ToolResult("ok", Gloss: "done"));

        var rows = Rows(p);
        var bullet = RowOf(rows, "○ run_agent");
        Assert.Equal("  ⎿ ✓ done · click to expand · ~0 tok", rows[bullet + 1]);   //the result sits under the bullet
        Assert.Equal(bullet + 2, rows.Length);                    //the transcript ends with the result row
        //the buffer still held both blocks in order through the flush
        Assert.Equal(new[] { "  ✓ allowed once", "  ✗ denied" },
            committed.Where(row => row is "  ✓ allowed once" or "  ✗ denied").ToArray());
    }

    //an aborted turn must not leak its buffered block into the next turn. watch the commit tap, since the transcript never holds a decision row
    [Fact]
    public void BeginTurn_DropsAPromptBufferedByAnAbortedTurn()
    {
        var (r, s, p, _) = Make();
        var committed = new List<string>();
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("i", "shell", """{"cmd":"rm -rf /"}"""));
        r.CommitPrompt(new[] { "  ✗ denied" });   //the prompt is answered and then the turn is aborted

        r.BeginTurn();
        r.CommitTap = batch => committed.AddRange(batch);   //the tap is set after the reset, so only turn 2 is watched
        var call = new ToolCall("j", "glob", "{}");
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("ok", Gloss: "1 match"));

        Assert.DoesNotContain(Rows(p), row => row == "  ✗ denied");
        Assert.DoesNotContain(committed, row => row == "  ✗ denied");
    }

    //outcome notes

    //a denied call's result row says ✗ denied, since the gate's message is written for the model. the bullet paints Theme.Err over the role tint
    [Fact]
    public void DeniedNote_ResultRowIsDeniedGlyph_RedBullet()
    {
        var (r, s, p, _) = Make(role: "coder");
        var call = new ToolCall("i", "shell", """{"cmd":"rm -rf /"}""");
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.NotePermissionOutcome(PermissionOutcomeKind.Denied);
        r.OnToolResult(call, new ToolResult("The user declined this tool call. Do not retry it.", IsError: true));

        var rows = Rows(p);
        var bullet = RowOf(rows, "○ shell");
        Assert.Equal("  ⎿ ✗ denied", rows[bullet + 1]);
        Assert.DoesNotContain(rows, row => row.Contains("declined", StringComparison.Ordinal));
        //the ● paints with Err rather than the coder role tint, so assert on the raw bytes (Rows() strips the paint)
        Assert.StartsWith(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), Theme.Err), RawRows(p)[bullet], StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), T.RoleTint("coder")), RawRows(p)[bullet], StringComparison.Ordinal);
    }

    //esc withdrew the question rather than answering it, so the row says cancelled
    [Fact]
    public void CancelledNote_ResultRowIsCancelled()
    {
        var (r, s, p, _) = Make(role: "coder");
        var call = new ToolCall("i", "write_file", """{"path":"a.cs"}""");
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.NotePermissionOutcome(PermissionOutcomeKind.Cancelled);
        r.OnToolResult(call, new ToolResult("cancelled by user", IsError: true));

        var rows = Rows(p);
        var bullet = RowOf(rows, "○ write_file");
        Assert.Equal("  ⎿ ✗ cancelled", rows[bullet + 1]);   //a cancelled write carries no link, its body is the gate's words to the model
        Assert.StartsWith(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), Theme.Err), RawRows(p)[bullet], StringComparison.Ordinal);
    }

    //an auto-approved call keeps the normal result row, with one dim row after it naming the scope of the grant. the ● keeps the role tint here.
    [Fact]
    public void AutoApprovedNote_DimFollowUpLine_CarriesScope()
    {
        var (r, s, p, _) = Make(role: "coder");
        var call = new ToolCall("i", "write_file", """{"path":"a.cs"}""");
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.NotePermissionOutcome(PermissionOutcomeKind.AutoApproved, @"~\proj\*");
        r.OnToolResult(call, new ToolResult("ok", Gloss: "12 lines"));

        var rows = Rows(p);
        var bullet = RowOf(rows, "○ write_file");
        Assert.Equal("  ⎿ ✓ 12 lines · ~0 tok", rows[bullet + 1]);           //the normal result row
        Assert.Equal(@"  auto-approved on this project: ~\proj\*", rows[bullet + 2]);
        Assert.StartsWith(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), T.RoleTint("coder")), RawRows(p)[bullet], StringComparison.Ordinal);
        Assert.Contains(T.Paint(@"auto-approved on this project: ~\proj\*", Theme.Dim),
            RawRows(p)[bullet + 2], StringComparison.Ordinal);
    }

    //the same rows are built twice, for the model item and for the batch handed to CommitRows, so change both copies together
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OutcomeNote_CommitSeamRowsMatchTheModelRows(bool denied)
    {
        var (r, s, p, _) = Make(role: "coder");
        var committed = new List<string>();
        r.CommitTap = batch => committed.AddRange(batch);
        var call = new ToolCall("i", "write_file", """{"path":"a.cs"}""");
        r.BeginTurn();
        r.OnToolCallStart(call);
        if (denied) r.NotePermissionOutcome(PermissionOutcomeKind.Denied);
        else r.NotePermissionOutcome(PermissionOutcomeKind.AutoApproved, @"~\proj\*");
        r.OnToolResult(call, new ToolResult(denied ? "The user declined this tool call." : "ok",
            Gloss: denied ? null : "12 lines", IsError: denied));

        //the same rows in the same order on both channels, minus the leading blank CommitRows adds
        Assert.Equal(RawRows(p), committed.Where(row => row.Length > 0).ToArray());

        var plain = committed.Select(row => TermText.StripAnsiForWidth(row).TrimEnd()).ToArray();
        if (denied)
        {
            Assert.Contains("  ⎿ ✗ denied", plain);   //a denied write carries no link, its body is the gate's words to the model
            Assert.DoesNotContain(plain, row => row.Contains("declined", StringComparison.Ordinal));
            Assert.Contains(committed, row => row.StartsWith(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), Theme.Err), StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(@"  auto-approved on this project: ~\proj\*", plain);
        }
    }

    //one permission note decorates one call only, so a later result in the same turn renders normally
    [Fact]
    public void Note_ConsumedOnce()
    {
        var (r, s, p, _) = Make(role: "coder");
        var first = new ToolCall("i", "shell", """{"cmd":"rm -rf /"}""");
        var second = new ToolCall("j", "glob", "{}");
        r.BeginTurn();
        r.OnToolCallStart(first);
        r.NotePermissionOutcome(PermissionOutcomeKind.Denied);
        r.OnToolResult(first, new ToolResult("declined", IsError: true));
        r.OnToolCallStart(second);
        r.OnToolResult(second, new ToolResult("ok", Gloss: "1 match"));

        var rows = Rows(p);
        var glob = RowOf(rows, "○ glob");
        Assert.Equal("  ⎿ ✓ 1 match · click to expand · ~0 tok", rows[glob + 1]);
        Assert.StartsWith(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), T.RoleTint("coder")), RawRows(p)[glob], StringComparison.Ordinal);
        Assert.Single(rows, row => row == "  ⎿ ✗ denied");
    }

    //a note left by an aborted turn must not decorate the next turn's first tool (BeginTurn drops it, the same as the buffered prompt)
    [Fact]
    public void BeginTurn_DropsAnUnconsumedPermissionNote()
    {
        var (r, s, p, _) = Make(role: "coder");
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("i", "shell", """{"cmd":"rm -rf /"}"""));
        r.NotePermissionOutcome(PermissionOutcomeKind.Denied);   //answered, then the turn is aborted

        r.BeginTurn();
        var call = new ToolCall("j", "glob", "{}");
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("ok", Gloss: "1 match"));

        var rows = Rows(p);
        var glob = RowOf(rows, "○ glob");
        Assert.Equal("  ⎿ ✓ 1 match · click to expand · ~0 tok", rows[glob + 1]);
        Assert.StartsWith(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), T.RoleTint("coder")), RawRows(p)[glob], StringComparison.Ordinal);
    }

    //a turn aborted mid-tool never sends OnToolResult, so EndTurn commits the stranded call as cancelled, and the buffered decision reaches the commit seam only
    [Fact]
    public void EndTurn_WithAStrandedToolBullet_CommitsTheCallAndACancelledResult_ButNotTheDecision()
    {
        var (r, s, p, _) = Make();
        var committed = new List<string>();
        r.CommitTap = batch => committed.AddRange(batch);
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("i", "shell", """{"cmd":"rm -rf ./build"}"""));
        r.CommitPrompt(new[] { "  allow?", "  ✓ allowed once" });   //buffered: the bullet is still chrome

        r.EndTurn();   //no OnToolResult ever arrives on the Ctrl+C path

        Assert.Null(p.State.Tool);
        var rows = Rows(p);
        var bullet = RowOf(rows, "○ shell rm -rf ./build");
        Assert.Equal("  ⎿ ✗ cancelled", rows[bullet + 1]);   //the cancelled result under the bullet
        Assert.Equal(bullet + 2, rows.Length);               //nothing else was committed
        Assert.DoesNotContain(rows, row => row.Contains("allow?", StringComparison.Ordinal));
        Assert.Contains(committed, row => row == "  ✓ allowed once");   //the buffered prompt still reaches the commit seam
    }

    //the committed pair is real scrollback, so the next turn's system line sits below it and the abort stays visible
    [Fact]
    public void EndTurn_CommittedCancelledBullet_SurvivesIntoTheNextTurn()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("i", "shell", """{"cmd":"sleep 300"}"""));
        r.EndTurn();
        r.CommitSystem("turn cancelled");

        var rows = Rows(p);
        Assert.True(RowOf(rows, "○ shell sleep 300") < RowOf(rows, "♯ turn cancelled"));
        Assert.Contains(rows, row => row == "  ⎿ ✗ cancelled");
    }

    //the loop must be told once when the renderer degrades, otherwise it keeps calling a dead painter's Repaint() and the user types blind
    [Fact]
    public void Degrade_FiresOnDegradedExactlyOnce()
    {
        var evil = new ThrowingWidthSurface();
        var gate = new object();
        var painter = new ChromePainter(evil, T, gate) { Frame = new InputFrame(evil, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode) };
        var r = new StreamRenderer(painter, evil, T, "coder", new ChromeTicker(painter, gate), gate);
        var fired = 0;
        r.OnDegraded = () => fired++;

        r.OnTextDelta("boom\n");   //the Width getter throws inside the commit, so the renderer degrades
        r.OnTextDelta("again\n");  //already degraded, so the plain fallback runs and no second signal fires

        Assert.Equal(1, fired);
    }

    //a renderer built around a painter that is already torn down must start degraded, so its commits reach the surface as plain bytes
    [Fact]
    public void RoleSwap_AfterDegrade_ReplacementRendererStaysOnThePlainFallback()
    {
        var evil = new ThrowingWidthSurface();
        var gate = new object();
        var painter = new ChromePainter(evil, T, gate) { Frame = new InputFrame(evil, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode) };
        var ticker = new ChromeTicker(painter, gate);
        var r = new StreamRenderer(painter, evil, T, "coder", ticker, gate);

        r.OnTextDelta("boom\n");           //the Width getter throws inside the commit, so the renderer degrades and the painter is torn down
        Assert.False(painter.Alive);
        evil.Inner.Clear();

        //the /role swap's exact shape: a fresh StreamRenderer around the same painter, ticker and gate
        var next = new StreamRenderer(painter, evil, T, "coder", ticker, gate);
        next.AdoptTranscriptState(r);

        next.CommitSystem("role: coder");
        next.OnTextDelta("still here\n");

        Assert.Contains("role: coder", evil.Inner.Text);
        Assert.Contains("still here", evil.Inner.Text);
    }

    //with no live tool there is nothing to sit under, so the block flushes to the commit seam and scrollback stays empty
    [Fact]
    public void CommitPrompt_WithNoLiveTool_FlushesImmediately_ButNotToScrollback()
    {
        var (r, s, p, _) = Make();
        var committed = new List<string>();
        r.CommitTap = batch => committed.AddRange(batch);
        r.BeginTurn();
        r.CommitPrompt(new[] { "  allow?", "  ✓ allowed once" });
        Assert.Contains(committed, row => row == "  ✓ allowed once");
        Assert.Empty(Rows(p));
    }

    [Fact]
    public void ToolResult_EmptyText_OmitsTheTokSuffix()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolResult(new ToolCall("i", "glob", "{}"), new ToolResult("", Gloss: "no matches"));
        Assert.Contains(Rows(p), row => row == "  ⎿ ✓ no matches");
        Assert.DoesNotContain("tok", Visible(s), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolResult_Error_ShowsFirstLineOnly_TabsFlattened_C1Stripped()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolResult(new ToolCall("i", "glob", "{}"),
            new ToolResult($"\n\n   \nbad{(char)0x85}\tvalue\nsecond line should not appear", IsError: true));

        Assert.Contains(Rows(p), row => row == "  ⎿ ✗ bad value · click to expand");
        Assert.DoesNotContain("second line", Visible(s), StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x85, s.Text);
    }

    [Fact]
    public void ToolResult_Error_TruncatedAtTheCellBudget()
    {
        var (r, s, p, _) = Make(width: 200);
        r.BeginTurn();
        r.OnToolResult(new ToolCall("i", "glob", "{}"),
            new ToolResult(new string('e', 130) + "\nsecond", IsError: true));

        //the ellipsis is budgeted inside the 120 cells: 119 e's plus the …
        Assert.Contains(Rows(p), row => row == "  ⎿ ✗ " + new string('e', 119) + "… · click to expand");
    }

    [Fact]
    public void ToolResult_BlankError_RendersBareErrMark()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolResult(new ToolCall("i", "glob", "{}"), new ToolResult("\n  \n\t\n", IsError: true));
        Assert.Contains(Rows(p), row => row == "  ⎿ ✗");
    }

    //progress is a chrome row under the bullet, replaced in place and gone once the result commits, while the ⎿ gloss holds the turn count
    [Fact]
    public void SubagentProgress_IsChromeUnderTheBullet_NotTranscript()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("i", "run_agent", "{}"));
        r.OnSubagentProgress("spec-review · turn 1");
        r.OnSubagentProgress("spec-review · turn 2");

        Assert.Equal("spec-review · turn 2", p.State.ToolProgress);   //the later progress replaced the earlier one in place, so the state holds only the latest
        Assert.Equal("run_agent", p.State.Tool?.Name);                //the bullet is live chrome until its result commits it
        var rows = Rows(p);
        //progress lives in ChromeState, so it never reaches the committed transcript
        Assert.DoesNotContain(rows, row => row.Contains("spec-review", StringComparison.Ordinal));

        r.OnToolResult(new ToolCall("i", "run_agent", "{}"), new ToolResult("ok", Gloss: "2 turns"));
        Assert.Null(p.State.ToolProgress);
        Assert.DoesNotContain(Rows(p), row => row.Contains("turn 2", StringComparison.Ordinal));
    }

    //blank-row discipline between blocks

    [Fact]
    public void TextThenToolCall_ExactlyOneBlankRow()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("Reading the file.\n");
        r.OnToolCallStart(new ToolCall("id1", "read_file", """{"path":"a.md"}"""));
        r.OnToolResult(new ToolCall("id1", "read_file", """{"path":"a.md"}"""), new ToolResult("ok"));
        AssertOneBlankRowBefore(Rows(p), "○ read_file");
    }

    [Fact]
    public void MidLineToolCall_CommitsTheOpenLine_ThenOneBlankRow()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("Reading the file now");   //no newline, so the tool call interrupts the line
        r.OnToolCallDelta();
        r.OnToolCallStart(new ToolCall("id1", "read_file", "{}"));

        Assert.Null(p.State.TailRaw);            //the open line was committed
        r.OnToolResult(new ToolCall("id1", "read_file", "{}"), new ToolResult("ok"));   //the result completes the tool block into the transcript model
        var rows = Rows(p);
        Assert.Equal("● Reading the file now", rows[0]);
        AssertOneBlankRowBefore(rows, "○ read_file");
    }

    //the test above flushes the tail through OnToolCallStart too, so this pins the delta alone: the open line commits and thinking clears
    [Fact]
    public void OnToolCallDelta_Alone_FlushesTheOpenLine()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("Reading the file now");   //no newline, so the line stays open
        r.OnToolCallDelta();

        Assert.Null(p.State.TailRaw);
        Assert.False(p.State.Thinking);
        Assert.Equal("● Reading the file now", Rows(p)[0]);
    }

    //a notice raised mid-paragraph commits the open line and the chrome tail keeps a second copy, so only the screen shows both copies
    [Fact]
    public void SystemLine_RaisedMidParagraph_LeavesNoSecondCopyOnScreen()
    {
        const string para = "the meta line keeps the build's order";
        var (r, s, p, _) = Make(height: 24);
        r.BeginTurn();
        r.OnTextDelta(para);        //no newline, so the line is still open when the notice arrives
        s.Clear();                  //from here the recorded bytes are the notice's own paint
        r.OnWarning("auto-compacted at 95% — continuing");

        //the neighbouring tests only check presence, so assert order: the paragraph's last copy must be above the notice
        var painted = Visible(s);
        var notice = painted.LastIndexOf("auto-compacted", StringComparison.Ordinal);
        var lastCopy = painted.LastIndexOf(para, StringComparison.Ordinal);
        Assert.True(notice >= 0 && lastCopy >= 0, "fixture drew neither line — the check below would be vacuous");
        Assert.True(lastCopy < notice, $"the paragraph is painted AGAIN below the notice:\n{painted}");

        Assert.Null(p.State.TailRaw);                  //the notice's flush committed the line, so nothing is live
        Assert.Equal("● " + para, Rows(p)[0]);         //the paragraph is still the first row of scrollback
    }

    //a /compact summary takes the reasoning arm, so CloseReasoningBlock must keep flushing the open line before it commits
    [Fact]
    public void SystemLine_RaisedMidPassthroughSummary_LeavesNoSecondCopyOnScreen()
    {
        const string summary = "kept: the plan, the notes, the open question";
        var (r, s, p, _) = Make(height: 24);
        r.BeginTurn();
        r.PassthroughReasoning = true;              //a /compact summary is styled reasoning, so it stays uncollapsed
        r.OnReasoningDelta(summary);                //no newline, so the line is still open when the notice arrives
        s.Clear();
        r.OnWarning("auto-compacted at 95% — continuing");

        var painted = Visible(s);
        var notice = painted.LastIndexOf("auto-compacted", StringComparison.Ordinal);
        var lastCopy = painted.LastIndexOf(summary, StringComparison.Ordinal);
        Assert.True(notice >= 0 && lastCopy >= 0, "fixture drew neither line — the check below would be vacuous");
        Assert.True(lastCopy < notice, $"the summary is painted AGAIN below the notice:\n{painted}");
        Assert.Null(p.State.TailRaw);
    }

    [Fact]
    public void ToolResultThenNextToolCall_ExactlyOneBlankRow()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("id1", "read_file", "{}"));
        r.OnToolResult(new ToolCall("id1", "read_file", "{}"), new ToolResult("ok", Gloss: "74 lines"));
        r.OnToolCallStart(new ToolCall("id2", "glob", "{}"));
        r.OnToolResult(new ToolCall("id2", "glob", "{}"), new ToolResult("ok"));
        AssertOneBlankRowBefore(Rows(p), "○ glob");
    }

    [Fact]
    public void ToolResultThenText_ExactlyOneBlankRow()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("id1", "shell", "{}"));
        r.OnToolResult(new ToolCall("id1", "shell", "{}"), new ToolResult("ok", Gloss: "exit 0"));
        r.OnTextDelta("Good, done.\n");
        r.EndTurn();
        AssertOneBlankRowBefore(Rows(p), "● Good, done.");
    }

    [Fact]
    public void ReasoningThenToolCall_ExactlyOneBlankRow()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnReasoningDelta("thinking about it.\n");
        r.OnToolCallDelta();
        r.OnToolCallStart(new ToolCall("id1", "glob", "{}"));
        r.OnToolResult(new ToolCall("id1", "glob", "{}"), new ToolResult("ok"));
        AssertOneBlankRowBefore(Rows(p), "○ glob");
    }

    [Fact]
    public void FirstOutputOfTurn_ToolCall_HasNoLeadingBlank()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("id1", "read_file", "{}"));
        r.OnToolResult(new ToolCall("id1", "read_file", "{}"), new ToolResult("ok"));
        Assert.Equal(0, RowOf(Rows(p), "○ read_file"));
    }

    //the ♯ system lines

    //a warning raised while a tool is live must commit after that tool's block
    [Fact]
    public void WarningDuringALiveTool_CommitsAfterTheToolBlock()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolCallStart(new ToolCall("id1", "shell", """{"command":"Start-Sleep 30"}"""));
        r.OnWarning("turn cancelled — remaining tool calls marked cancelled, not executed");
        r.EndTurn();   //the Ctrl+C path: commits the stranded bullet as ✗ cancelled, then the ♯ line

        var rows = Rows(p);
        Assert.True(RowOf(rows, "○ shell") < RowOf(rows, "✗ cancelled"),
            "the bullet must precede its cancelled result");
        Assert.True(RowOf(rows, "✗ cancelled") < RowOf(rows, "♯ turn cancelled"),
            "the ♯ line must land AFTER the tool block it is about");
        AssertOneBlankRowBefore(rows, "♯ turn cancelled");
    }

    [Fact]
    public void Warning_CommitsAHashSystemRow()
    {
        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("half a line");
        r.OnWarning("turn truncated");

        var rows = Rows(p);
        Assert.Equal("● half a line", rows[0]);            //the open line is committed first
        AssertOneBlankRowBefore(rows, "♯ turn truncated");
        Assert.DoesNotContain("! turn truncated", Visible(s), StringComparison.Ordinal);   //the prefix is ♯, so an exclamation form must not appear
    }

    [Fact]
    public void CommitSystem_WrapsWithATwoSpaceHang()
    {
        var (r, s, p, _) = Make(width: 20);
        r.BeginTurn();
        r.CommitSystem("writing to journal because the span is now covered");

        var rows = Rows(p).Where(x => x.Length > 0).ToArray();
        Assert.StartsWith("♯ ", rows[0], StringComparison.Ordinal);
        Assert.True(rows.Length > 1, "the long system line must wrap");
        Assert.All(rows.Skip(1), row => Assert.StartsWith("  ", row, StringComparison.Ordinal));
        Assert.All(rows, row => Assert.True(UnicodeWidth.Of(row) <= 20));
    }

    //the user echo

    [Fact]
    public void CommitUser_CommitsBandedPromptRows()
    {
        var (r, s, p, _) = Make(width: 40);
        r.CommitUser(new[] { "hello there" });

        Assert.Equal("❯ hello there", Rows(p)[0]);
        Assert.Contains(Ansi.Bg(T.Map(Theme.UserInputBg), T.TrueColor), s.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitUser_WrapsWithATwoSpaceHang()
    {
        var (r, s, p, _) = Make(width: 24);
        r.CommitUser(new[] { "alpha beta gamma delta epsilon zeta" });

        var rows = Rows(p).Where(x => x.Length > 0).ToArray();
        Assert.StartsWith("❯ ", rows[0], StringComparison.Ordinal);
        Assert.True(rows.Length > 1, "the long line must wrap");
        Assert.All(rows.Skip(1), row => Assert.StartsWith("  ", row, StringComparison.Ordinal));
        Assert.All(rows, row => Assert.True(UnicodeWidth.Of(row) <= 24));
    }

    //deleting the InlineStyler.Parse call before wrapping would echo bold and chips literally and render "#"/"-" lines as block markdown
    [Fact]
    public void CommitUser_BoldAndChip_StyledInline_MarkersStripped()
    {
        var (r, s, p, _) = Make(width: 40);
        r.CommitUser(new[] { "**hi** `x`" });

        var raw = string.Join("\n", RawRows(p));
        var visible = string.Join("\n", Rows(p));
        Assert.Contains(Ansi.Bold, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("48;", ChipRun(raw, Ansi.Fg(Theme.CodeInlineFg, T.TrueColor)), StringComparison.Ordinal);   //the chip takes no background band, whatever the colour
        Assert.Contains(Ansi.Fg(Theme.CodeInlineFg, T.TrueColor), raw, StringComparison.Ordinal);   //the chip text is painted with the code colour
        Assert.DoesNotContain("**", visible, StringComparison.Ordinal);   //the bold markers are consumed by the styler
        Assert.DoesNotContain("`", visible, StringComparison.Ordinal);    //the backticks are consumed by the styler
        Assert.Contains("hi x", visible, StringComparison.Ordinal);      //bold hi, a separator, then the unpadded chip x
        Assert.DoesNotContain(" x ", visible, StringComparison.Ordinal);  //the chip has no padding spaces around it
    }

    [Fact]
    public void CommitUser_HeadingHashAndListDash_EchoLiterally_NoBlockMarkdown()
    {
        var (r, s, p, _) = Make(width: 40);
        r.CommitUser(new[] { "# h" });
        Assert.Equal("❯ # h", Rows(p)[0]);   //the hash stays literal, so no heading styling appears

        var (r2, s2, p2, _) = Make(width: 40);
        r2.CommitUser(new[] { "- item" });
        Assert.Equal("❯ - item", Rows(p2)[0]);   //the dash stays literal, so no list markdown appears
    }

    //the test above only checks the band appears somewhere, so this pins it on every row and a Reset at each row's end
    [Fact]
    public void CommitUser_BandPaintsEveryRow_AndEachRowClosesClean()
    {
        var (r, s, p, _) = Make(width: 21);
        r.CommitUser(new[] { "hello world this wraps", "second" });

        var band = Ansi.Bg(T.Map(Theme.UserInputBg), T.TrueColor);
        //the committed user block is the UserEchoItem itself, so its rendered rows show the band (the composer frame is chrome)
        var committed = RawRows(p).Where(row => row.Length > 0).ToArray();
        Assert.True(committed.Length >= 3, "expected the first line to wrap plus a second logical line");
        foreach (var row in committed)
        {
            Assert.Contains(band, row, StringComparison.Ordinal);            //every row opens with the band, the wrapped ones included
            Assert.Contains(Ansi.ClearToEol, row, StringComparison.Ordinal); //the band spans the full width
            Assert.EndsWith(Ansi.Reset, row, StringComparison.Ordinal);      //each row closes with a Reset, so no background bleeds below
        }
    }

    //a fresh renderer must adopt the transcript state, or _last starts at None and the blank row before the role line is dropped
    [Fact]
    public void RoleSwap_KeepsTheBlankRow_BetweenTheUserEchoAndTheRoleLine()
    {
        var s = new RecordingSurface { Width = 40 };
        var gate = new object();   //the /role swap reuses the same painter, ticker and gate
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode), RoleForTint = "coder" };
        var ticker = new ChromeTicker(painter, gate);
        var r = new StreamRenderer(painter, s, T, "coder", ticker, gate, model: painter.Model, convoTail: () => null);
        r.CommitUser(new[] { "/role coder" });   //the old renderer commits the echo, as the swap does in the REPL

        var swapped = new StreamRenderer(painter, s, T, "coder", ticker, gate, model: painter.Model, convoTail: () => null);
        swapped.AdoptTranscriptState(r);
        swapped.CommitSystem("role: coder");

        AssertOneBlankRowBefore(Rows(painter), "♯ role: coder");
    }

    //the leading blank comes from the item's position in TranscriptModel, so a renderer swap keeps it even without AdoptTranscriptState
    [Fact]
    public void RoleSwap_LeadingBlankIsStructural_SurvivesAnUnadoptedSwap()
    {
        var s = new RecordingSurface { Width = 40 };
        var gate = new object();
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode), RoleForTint = "coder" };
        var ticker = new ChromeTicker(painter, gate);
        var r = new StreamRenderer(painter, s, T, "coder", ticker, gate, model: painter.Model, convoTail: () => null);
        r.CommitUser(new[] { "/role coder" });

        var swapped = new StreamRenderer(painter, s, T, "coder", ticker, gate, model: painter.Model, convoTail: () => null);
        swapped.CommitSystem("role: coder");   //the swap never adopts the transcript state

        AssertOneBlankRowBefore(Rows(painter), "♯ role: coder");   //still exactly one blank, since the model sets it from item position
    }

    //tool blocks and prose blocks stamp their role from TranscriptModel.Role, so a role change without a swap moves both together
    [Fact]
    public void RoleStamp_FollowsTheModel_ForBothProseAndToolBlocks()
    {
        var (r, _, painter, _) = Make(role: "coder");   //the renderer's fixed _role stays on coder throughout
        painter.Model.Role = "coder";   //pin the model role, since the harness default differs

        r.OnTextDelta("first\n");       //opens an AssistantBlock while the model role is coder
        var c1 = new ToolCall("i", "shell", "{}");
        r.OnToolCallStart(c1);          //closes the prose block and opens the tool
        r.OnToolResult(c1, new ToolResult("ok", Gloss: "exit 0"));   //the tool block is stamped coder

        painter.Model.Role = "generalist";   //the model role changes with no new renderer

        r.OnTextDelta("second\n");      //opens a new AssistantBlock while the model role is generalist
        var c2 = new ToolCall("j", "glob", "{}");
        r.OnToolCallStart(c2);
        r.OnToolResult(c2, new ToolResult("ok", Gloss: "1 match"));   //the tool block is stamped generalist

        var prose = painter.Model.Items.OfType<AssistantBlockItem>().ToList();
        var tools = painter.Model.Items.OfType<ToolBlockItem>().ToList();
        Assert.Equal(2, prose.Count);
        Assert.Equal(2, tools.Count);
        //both item kinds stamp the model's role at commit time, and a past item keeps the role it was stamped with
        Assert.Equal("coder", prose[0].Role);
        Assert.Equal("generalist", prose[1].Role);
        Assert.Equal("coder", tools[0].Role);
        Assert.Equal("generalist", tools[1].Role);
    }

    //the blank-row state belongs to the transcript, so BeginTurn must keep it, or the echo and the output lose their blank row
    [Fact]
    public void CommitUser_ThenBeginTurn_StillLeavesOneBlankRowBeforeOutput()
    {
        var (r, s, p, _) = Make();
        r.CommitUser(new[] { "hi" });
        r.BeginTurn();
        r.OnTextDelta("hi\n");
        r.EndTurn();

        AssertOneBlankRowBefore(Rows(p), "● hi");
    }

    //the token estimate feeds the ticker

    [Fact]
    public void Deltas_FeedTheTickerTokenEstimate()
    {
        var (r, _, _, k) = Make();
        r.BeginTurn();
        r.OnTextDelta("abcd");            //4 chars
        r.OnReasoningDelta("efgh");       //4 chars
        Assert.Equal(8, TokenChars(k));
    }

    //sanitize: the clean run and the hostile run must show the same text, so their ESC and CR counts match exactly

    [Fact]
    public void OnTextDelta_HostileEscAndCr_StrippedButTextSurvives()
    {
        var (clean, cs, cp, _) = Make();
        clean.BeginTurn();
        clean.OnTextDelta("go [2Aforged\n");   //the hostile run must degrade to this literal text
        clean.EndTurn();

        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("go \x1b[2Aforge\rd\n");
        r.EndTurn();

        Assert.Equal(EscCount(cs.Text), EscCount(s.Text));
        Assert.Equal(CrCount(cs.Text), CrCount(s.Text));
        Assert.Equal("● go [2Aforged", Rows(p)[0]);
    }

    [Fact]
    public void OnTextDelta_EscSplitAcrossDeltas_LeavesNoEscByte()
    {
        var (clean, cs, cp, _) = Make();
        clean.BeginTurn();
        clean.OnTextDelta("safe ");
        clean.OnTextDelta("");
        clean.OnTextDelta("[2Aforged\n");
        clean.EndTurn();

        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnTextDelta("safe ");
        r.OnTextDelta("\x1b");          //the ESC introducer by itself in this fragment
        r.OnTextDelta("[2Aforged\n");   //the CSI body arrives in the next fragment
        r.EndTurn();

        Assert.Equal(EscCount(cs.Text), EscCount(s.Text));
        Assert.Equal("● safe [2Aforged", Rows(p)[0]);
    }

    //reasoning deltas are untrusted too, so an ESC split across two of them must stay inert like on the text path
    [Fact]
    public void OnReasoningDelta_EscSplitAcrossDeltas_LeavesNoEscByte()
    {
        var (clean, cs, cp, _) = Make(reasoning: ReasoningMode.Expanded);   //full reasoning render on both sides, so the ESC counts compare
        clean.BeginTurn();
        clean.OnReasoningDelta("safe ");
        clean.OnReasoningDelta("");
        clean.OnReasoningDelta("[2Aforged\n");
        clean.EndTurn();

        var (r, s, p, _) = Make(reasoning: ReasoningMode.Expanded);
        r.BeginTurn();
        r.OnReasoningDelta("safe ");
        r.OnReasoningDelta("\x1b");          //the ESC introducer by itself in this fragment
        r.OnReasoningDelta("[2Aforged\n");   //the CSI body arrives in the next fragment
        r.EndTurn();

        Assert.Equal(EscCount(cs.Text), EscCount(s.Text));
        Assert.Equal("│ safe [2Aforged", Rows(p)[2]);   //the body sits after the ▾ header and the blank rail row, the same on both sides
    }

    [Fact]
    public void OnReasoningDelta_HostileEscAndCr_StrippedButTextSurvives()
    {
        var (clean, cs, cp, _) = Make(reasoning: ReasoningMode.Expanded);   //full reasoning render on both sides, so the ESC counts compare
        clean.BeginTurn();
        clean.OnReasoningDelta("th[2Ainkingmore\n");
        clean.EndTurn();

        var (r, s, p, _) = Make(reasoning: ReasoningMode.Expanded);
        r.BeginTurn();
        r.OnReasoningDelta("th\x1b[2Ainking\rmore\n");
        r.EndTurn();

        Assert.Equal(EscCount(cs.Text), EscCount(s.Text));
        Assert.Equal(CrCount(cs.Text), CrCount(s.Text));
        Assert.Equal("│ th[2Ainkingmore", Rows(p)[2]);   //the body sits after the ▾ header and the blank rail row
    }

    [Fact]
    public void OnToolCallStart_HostileArgsAndName_CannotEmitControlBytes()
    {
        var (clean, cs, cp, _) = Make();
        clean.BeginTurn();
        var cleanCall = new ToolCall("i", "ev[31mil", """{"cmd":"a.cs[2Kforgedtail"}""");
        clean.OnToolCallStart(cleanCall);
        clean.OnToolResult(cleanCall, new ToolResult("ok"));   //commits the bullet into the transcript

        var (r, s, p, _) = Make();
        r.BeginTurn();
        var hostileCall = new ToolCall("i", "ev\x1b[31mil\r",
            "{\"cmd\":\"a.cs\\u001b[2Kforged\\r\\ntail\"}");
        r.OnToolCallStart(hostileCall);
        r.OnToolResult(hostileCall, new ToolResult("ok"));

        Assert.Equal(EscCount(cs.Text), EscCount(s.Text));
        Assert.Equal(CrCount(cs.Text), CrCount(s.Text));
        Assert.Contains(Rows(p), row => row == "○ ev[31mil a.cs[2Kforgedtail");
    }

    [Fact]
    public void OnToolResult_HostileGlossAndError_CannotEmitControlBytes()
    {
        var (clean, cs, cp, _) = Make();
        clean.BeginTurn();
        clean.OnToolResult(new ToolCall("i", "shell", "{}"), new ToolResult("ok", Gloss: "3 lines[2Kforged"));

        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnToolResult(new ToolCall("i", "shell", "{}"), new ToolResult("ok", Gloss: "3 lines\x1b[2K\rforged"));

        Assert.Equal(EscCount(cs.Text), EscCount(s.Text));
        Assert.Equal(CrCount(cs.Text), CrCount(s.Text));
        Assert.Contains("3 lines[2Kforged", Visible(s), StringComparison.Ordinal);

        var (r2, s2, p2, _) = Make();
        r2.BeginTurn();
        r2.OnToolResult(new ToolCall("i", "shell", "{}"), new ToolResult("boom\x1b[2K\rforged", IsError: true));
        Assert.Contains(Rows(p2), row => row == "  ⎿ ✗ boom[2Kforged");
    }

    [Fact]
    public void OnWarning_HostileEscAndCr_StrippedOnTheSystemRow()
    {
        var (clean, cs, cp, _) = Make();
        clean.BeginTurn();
        clean.OnWarning("care[2Kful");

        var (r, s, p, _) = Make();
        r.BeginTurn();
        r.OnWarning("care\x1b[2Kful\r");

        Assert.Equal(EscCount(cs.Text), EscCount(s.Text));
        Assert.Equal(CrCount(cs.Text), CrCount(s.Text));
        Assert.Contains(Rows(p), row => row == "♯ care[2Kful");
    }

    //the degraded fallback: plain passthrough, still sanitized

    [Fact]
    public void RendererException_DegradesToPassthrough_TurnSurvives()
    {
        var (r, evil) = Degraded();
        r.OnTextDelta("survives\n");
        Assert.Equal("survives\n", evil.Inner.Text);
    }

    [Fact]
    public void OnTextDelta_DegradedPath_SanitizesHostileProse()
    {
        var (r, evil) = Degraded();
        r.OnTextDelta("go \x1b[2Aforge\rd\n");
        Assert.Equal("go [2Aforged\n", evil.Inner.Text);
    }

    [Fact]
    public void OnReasoningDelta_DegradedPath_SanitizesHostileProse()
    {
        var (r, evil) = Degraded();
        r.OnReasoningDelta("th\x1b[2Ainking\rmore\n");
        Assert.Equal("th[2Ainkingmore\n", evil.Inner.Text);
    }

    [Fact]
    public void OnWarning_DegradedPath_SanitizesHostileWarning()
    {
        var (r, evil) = Degraded();
        r.OnWarning("care\x1b[2Kful\r");
        Assert.Equal("\n! care[2Kful\n", evil.Inner.Text);
    }

    [Fact]
    public void OnToolCallStart_DegradedPath_PlainBullet()
    {
        var (r, evil) = Degraded();
        r.OnToolCallStart(new ToolCall("i", "glob", """{"pattern":"*.cs"}"""));
        Assert.Equal("\n[tool] glob {\"pattern\":\"*.cs\"}\n", evil.Inner.Text);
    }

    [Fact]
    public void OnToolResult_DegradedPath_MultilineError_ShowsFirstLineOnly()
    {
        var (r, evil) = Degraded();
        r.OnToolResult(new ToolCall("i", "glob", "{}"),
            new ToolResult($"first{(char)0x9B} line\nsecond line should not appear", IsError: true));
        Assert.Equal("[tool] glob ✗ first line\n", evil.Inner.Text);
        Assert.DoesNotContain((char)0x9B, evil.Inner.Text);
    }

    [Fact]
    public void OnToolResult_DegradedPath_Ok()
    {
        var (r, evil) = Degraded();
        r.OnToolResult(new ToolCall("i", "glob", "{}"), new ToolResult("ok", Gloss: "3 hits"));
        Assert.Equal("[tool] glob ✓\n", evil.Inner.Text);
    }

    //the degraded line reads the same words as the plain repl, one result line and no click affordance
    [Fact]
    public void OnToolResult_DegradedPath_ShellUsesTheBlockWords()
    {
        var (r, evil) = Degraded();
        r.OnToolResult(new ToolCall("i", "shell", "{}"),
            new ToolResult("out\r\n--- stderr ---\r\nbad\r\n(exit code 1)", IsError: true, Gloss: "exit 1"));
        Assert.Equal("[tool] shell \u2713 ran \u00b7 exit 1 \u00b7 bad \u00b7 ~9 tok\n", evil.Inner.Text);
    }

    [Fact]
    public void OnSubagentProgress_DegradedPath_PlainIndentedLine()
    {
        var (r, evil) = Degraded();
        r.OnSubagentProgress("turn 2");
        Assert.Equal("  turn 2\n", evil.Inner.Text);
    }

    //usage

    [Fact]
    public void OnUsage_Forwards()
    {
        Usage? got = null;
        var s = new RecordingSurface();
        var gate = new object();
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode) };
        var r = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, u => got = u);
        r.OnUsage(new Usage(100, 20));
        Assert.Equal(new Usage(100, 20), got);
    }

    //the TermText sanitize contract, independent of the renderer

    [Fact]
    public void TermTextSanitizeProse_TabColumnResetsAfterNewline() =>
        Assert.Equal("ab  c\nab  c", TermText.SanitizeProse("ab\tc\nab\tc"));

    [Fact]
    public void TermTextSanitizeProse_TabAtColumnZero_ExpandsToFourSpaces() =>
        Assert.Equal("    x", TermText.SanitizeProse("\tx"));

    [Fact]
    public void TermTextSanitize_TabExpandsRatherThanDropping() =>
        Assert.Equal("a   b", TermText.Sanitize("a\tb"));

    [Fact]
    public void TermTextSanitize_StripsC1Control()
    {
        var nel = ((char)0x0085).ToString();   //NEL, a raw C1 control
        Assert.Equal("ab", TermText.Sanitize("a" + nel + "b"));
    }

    [Fact]
    public void TermTextSanitizeProse_StripsC1Control()
    {
        var csi = ((char)0x009b).ToString();   //CSI, a raw C1 control
        Assert.Equal("ab", TermText.SanitizeProse("a" + csi + "b"));
    }

    [Fact]
    public void TermTextSanitize_C1UpperBoundStripped_JustPastRangeSurvives()
    {
        var lastC1 = ((char)0x009f).ToString();
        var nbsp = ((char)0x00a0).ToString();
        Assert.Equal("a" + nbsp + "b", TermText.Sanitize("a" + lastC1 + nbsp + "b"));
    }

    [Fact]
    public void TermTextSanitize_C1LowerBoundStripped_JustBeforeRangeSurvives()
    {
        var del = ((char)0x007f).ToString();
        var firstC1 = ((char)0x0080).ToString();
        Assert.Equal("ab", TermText.Sanitize("a" + del + firstC1 + "b"));
    }

    [Fact]
    public void TermTextSanitize_NonC1HighUnicode_PassesThroughUntouched()
    {
        var cjk = ((char)0x6f22).ToString() + ((char)0x5b57).ToString();
        var emoji = char.ConvertFromUtf32(0x1F389);
        Assert.Equal(cjk + emoji, TermText.Sanitize(cjk + emoji));
    }

    [Fact]
    public void TermTextSanitizeProse_NonC1HighUnicode_PassesThroughUntouched()
    {
        var cjk = ((char)0x6f22).ToString() + ((char)0x5b57).ToString();
        var emoji = char.ConvertFromUtf32(0x1F389);
        Assert.Equal(cjk + emoji + "\n", TermText.SanitizeProse(cjk + emoji + "\n"));
    }

    [Fact]
    public void OnTextDelta_TabExpansion_FeedsTheCommitWrapMath()
    {
        var (r, s, p, _) = Make(width: 40);
        r.BeginTurn();
        r.OnTextDelta("a\tb\n");
        r.EndTurn();
        Assert.DoesNotContain('\t', s.Text);
        Assert.Equal("● a   b", Rows(p)[0]);   //one tab at column 1 expands to 3 spaces
    }

    //textWidth goes negative at width 0, and only the SpanWrap and GutterWrap guards keep that from throwing
    [Fact]
    public void WidthZero_LineStillCommits_NoThrow_NoDegrade()
    {
        var (r, s, p, _) = Make(width: 0);
        r.BeginTurn();
        r.OnTextDelta("hi\n");
        r.EndTurn();

        Assert.False(IsDegraded(r));
        //at width 0 nothing reaches the surface, so assert on the transcript, where the line still commits
        Assert.Contains(Rows(p), row => row == "● hi");
    }

    //the argument preview is asserted through the live renderer, since StreamRenderer has its own copy of the formatter

    [Fact]
    public void FinishedToolRow_NeverShowsAFragmentOfTheEditBody()
    {
        //a finished edit_file row must not show a slice of the file body
        var (r, _, p, _) = Make(width: 200);
        r.BeginTurn();
        var call = new ToolCall("id1", "edit_file",
            """{"path":"a.html","old_string":"rx=\"12\" fill=\"#4a90e2\"/>","new_string":"rx=\"14\""}""");

        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("ok"));

        var row = Rows(p).First(x => x.Contains("edit_file", StringComparison.Ordinal));
        Assert.DoesNotContain("rx=", row);
        Assert.DoesNotContain("4a90e2", row);
        Assert.Contains("a.html", row);
    }

    [Fact]
    public void FinishedToolRow_LabelsTheLineWindowInsteadOfTrailingBareNumbers()
    {
        var (r, _, p, _) = Make(width: 200);
        r.BeginTurn();
        var call = new ToolCall("id1", "read_file", """{"path":"a.md","offset":240,"limit":50}""");

        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("ok"));

        var row = Rows(p).First(x => x.Contains("read_file", StringComparison.Ordinal));
        Assert.Contains("lines 240–289", row);
    }

    [Fact]
    public void TheInFlightRowAndTheFinishedRow_AgreeOnThePath()
    {
        //the in-flight row and the finished row must take the same path, so any change to the preview rule moves both
        var (r, _, p, _) = Make(width: 200);
        r.BeginTurn();
        var json = """{"path":"a.html","old_string":"x","new_string":"y"}""";

        r.OnToolCallDelta("edit_file", json);
        var inFlight = p.State.Tool!.Value.Args;

        var call = new ToolCall("id1", "edit_file", json);
        r.OnToolCallStart(call);
        var finished = p.State.Tool!.Value.Args;

        Assert.Equal(inFlight, finished);
    }

    private static bool IsDegraded(StreamRenderer r) => (bool)typeof(StreamRenderer)
        .GetField("_degraded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r)!;
}
