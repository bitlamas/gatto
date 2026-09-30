namespace Gatto.Terminal;

//the row counts up from the bottom, the face drops blank rows above the field. the column is zero-based cells off the drawn caret, so the two share one cell
public readonly record struct CaretSpot(int FromEnd, int Column);
