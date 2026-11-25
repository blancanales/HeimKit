using System;
using System.IO;
using HeimKit.Core.Control;

namespace HeimKit.Client;

internal sealed partial class ClientLink
{
    private MemoryStream? _moderationChunks;
    internal ModerationRow[]? Moderation { get; private set; }
    internal event Action? ModerationCleared;

    private void ClearModeration()
    {
        _moderationChunks?.Dispose();
        _moderationChunks = null;
        Moderation = null;
        ModerationCleared?.Invoke();
    }

    private void ReceiveModeration(ZRpc rpc, ZPackage packet)
    {
        if (!ReferenceEquals(rpc, _rpc) || !rpc.IsConnected() || State != LinkState.Connected ||
            Snapshot?.IsAdmin != true || packet.Size() < 1 || packet.Size() > ModerationProtocol.ChunkBytes + 1) return;
        var bytes = packet.GetArray();
        if (bytes[0] > 3) return;
        if ((bytes[0] & 1) != 0)
        {
            _moderationChunks?.Dispose();
            _moderationChunks = new MemoryStream();
        }
        if (_moderationChunks == null) return;
        _moderationChunks.Write(bytes, 1, bytes.Length - 1);
        if ((bytes[0] & 2) == 0) return;
        try { Moderation = ModerationProtocol.Decode(_moderationChunks); }
        catch (InvalidDataException) { ClearModeration(); }
        catch (IOException) { ClearModeration(); }
        catch (FormatException) { ClearModeration(); }
        finally { _moderationChunks?.Dispose(); _moderationChunks = null; }
        Changed?.Invoke();
    }

    internal void Moderate(bool unban, string id)
    {
        if (!ReferenceEquals(_sessionWorld, ZNet.World) || Moderation == null || Snapshot?.IsAdmin != true || !Snapshot.AdminChangesAllowed) return;
        if (State == LinkState.LocalHostedWorld)
        {
            if (_localControl != null && ReferenceEquals(_localControl, _local?.Control))
                _localControl.ModerateLocal(unban, id);
        }
        else if (State == LinkState.Connected && _rpc?.IsConnected() == true &&
                 ReferenceEquals(_rpc, ZNet.instance?.GetServerPeer()?.m_rpc))
        {
            var packet = new ZPackage();
            packet.Write(unban);
            packet.Write(id);
            _rpc.Invoke(ModerationProtocol.Action, packet);
        }
    }
}
