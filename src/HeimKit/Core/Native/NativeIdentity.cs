using System;
using System.Globalization;

namespace HeimKit.Core.Native;

internal static class NativeIdentity
{
    // Preserve existing native textual ordering without allocating object-ID strings.
    internal static int Compare(ZDOID a, ZDOID b)
    {
        Span<char> left = stackalloc char[32]; Span<char> right = stackalloc char[32];
        int l = Format(a, left), r = Format(b, right);
        return MemoryExtensions.CompareTo(left.Slice(0, l), right.Slice(0, r), StringComparison.CurrentCulture);
    }
    internal static int CompareOrdinal(ZDOID a, ZDOID b)
    {
        Span<char> left = stackalloc char[32]; Span<char> right = stackalloc char[32];
        int l = Format(a, left), r = Format(b, right);
        return left.Slice(0, l).SequenceCompareTo(right.Slice(0, r));
    }
    private static int Format(ZDOID id, Span<char> buffer)
    {
        id.UserID.TryFormat(buffer, out int written, provider: CultureInfo.CurrentCulture);
        buffer[written++] = ':'; id.ID.TryFormat(buffer.Slice(written), out int rest, provider: CultureInfo.CurrentCulture);
        return written + rest;
    }
}
