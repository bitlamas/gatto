# Rebuilds oracle.json from corpus.txt with Python's own tokenize module.
# Run: python generate-oracle.py
# Entries in corpus.txt are separated by a line that holds only four dashes.
# tokenize gives a row and a column in code points, and the test indexes UTF-16 chars, so every offset is converted.
# An entry that tokenize cannot finish records the kind of text left open, and the test reads that field, never the message.
import io
import json
import keyword
import pathlib
import sys
import tokenize

NL = chr(10)
here = pathlib.Path(__file__).resolve().parent
raw = (here / "corpus.txt").read_bytes().decode("utf-8").replace(chr(13) + NL, NL)
entries = []
for chunk in raw.split(NL + "----" + NL):
    chunk = chunk.rstrip(NL)
    if chunk.strip():
        entries.append(chunk)
version = "%d.%d.%d" % sys.version_info[:3]
SKIP = {"NEWLINE", "NL", "INDENT", "DEDENT", "ENDMARKER"}
PREFIX_LETTERS = "rRbBuUfFtT"


def converter(source):
    starts = []
    at = 0
    for line in source.split(NL):
        starts.append(at)
        at += len(line) + 1
    astral = [0]
    for ch in source:
        astral.append(astral[-1] + (1 if ord(ch) > 0xFFFF else 0))

    def point(row, col):
        return len(source) if row - 1 >= len(starts) else min(starts[row - 1] + col, len(source))

    def utf16(row, col):
        p = point(row, col)
        return p + astral[p]

    return point, utf16


def open_kind(source, at, number):
    while at < len(source) and source[at].isspace():
        at += 1
    rest = source[at:]
    letters = 0
    while letters < 2 and letters < len(rest) and rest[letters] in PREFIX_LETTERS:
        letters += 1
    if rest[letters:letters + 1] in ("'", '"'):
        return "string"
    sys.exit("refused: entry %d stops at text this script cannot name: %r" % (number, rest[:20]))


lines = []
for number, source in enumerate(entries, 1):
    point, utf16 = converter(source)
    tokens = []
    last = 0
    error = None
    unterminated = None
    try:
        for t in tokenize.generate_tokens(io.StringIO(source).readline):
            name = tokenize.tok_name[t.type]
            if name in SKIP:
                continue
            token = {"k": name, "s": utf16(*t.start), "e": utf16(*t.end)}
            if name == "NAME" and keyword.iskeyword(t.string):
                token["kw"] = True
            tokens.append(token)
            last = point(*t.end)
    except (tokenize.TokenError, SyntaxError) as e:
        error = str(e.args[0])
        unterminated = open_kind(source, last, number)
    entry = {"source": source, "parser": version, "tokens": tokens, "error": error, "unterminated": unterminated}
    lines.append(json.dumps(entry, ensure_ascii=False, separators=(",", ":")))

with open(here / "oracle.json", "w", encoding="utf-8", newline=NL) as out:
    out.write("[" + NL + ("," + NL).join(lines) + NL + "]" + NL)
print("wrote %d entries with Python %s" % (len(lines), version))
