namespace Gatto.Core.Loop;

//a call its tool would fail, refused before the prompt with the tool's own words, so the loop reports it as the tool's error and not as a block
public sealed class CannotApplyException(string message) : InvalidOperationException(message);
