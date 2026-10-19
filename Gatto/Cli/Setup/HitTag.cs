using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//what a press on a span does. a pane line names its publisher and its file, a regime jump the regimes it counts
internal enum HitKind { Row, PaneLine, PaneFile, FoldedFile, Chip, Door, ParamsHeader, Clause, RegimeJump, EscapeRow, OptionRow, FooterKey }

internal sealed record HitTag(HitKind Kind, int Index = -1, int File = -1, string? Answer = null,
    string? Key = null, IReadOnlySet<FitRegime>? Regimes = null, Region? Area = null);
