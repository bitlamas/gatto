namespace Gatto.Repl.Input;

//a fault from the composer's redraw callback, so the loop can tell a paint bug from a dead key source and re-raise it
public sealed class ComposerPaintException(Exception inner)
    : Exception("composer paint failed: " + inner.Message, inner);
