namespace Gatto.Core.Memory;

//one nullable seam holding both halves of memory consent at a compaction, through which the piggyback can only create c- fact files
public sealed record PiggybackSeam(
    Func<IReadOnlyList<string>, (int Banked, int Skipped)> Bank,
    Func<MemoryIndex.LoadResult> ComposeIndex);
