using System;

namespace HeimKit.Core.Control.Server;

// Each authenticated connection advances independently on the ordered direct RPC.
internal sealed class ModerationDelivery
{
    private byte[]? _generation;
    private int _offset;

    internal void Send(bool authorized, Func<byte[]> current, Action<ZPackage> send)
    {
        if (!authorized)
        {
            _generation = null;
            _offset = 0;
            return;
        }
        // Finish an in-flight generation even if other players join between pages.
        if (_generation == null || _offset < 0)
        {
            var latest = current();
            if (ReferenceEquals(_generation, latest)) return;
            _generation = latest;
            _offset = 0;
        }
        var chunk = ModerationProtocol.Chunk(_generation, _offset, out int next);
        send(chunk);
        _offset = next == _generation.Length ? -1 : next;
    }
}
