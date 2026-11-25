using System;
using System.Linq;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.Players;

namespace HeimKit.Core.Control.Server;

internal sealed partial class ControlServer
{
    private string[] _bans = Array.Empty<string>();
    private long _historyRevision = -1;
    private ModerationRow[] _roster = Array.Empty<ModerationRow>();
    private byte[]? _encodedRoster;

    private ModerationRow[] CurrentRoster()
    {
        var native = ZNet.instance.Banned;
        if (_historyRevision != _players.History.Revision || !native.SequenceEqual(_bans))
        {
            _bans = native.ToArray();
            _roster = _players.History.Rows()
                .Concat(_bans.Select(id => new ModerationRow(RosterGroup.Banned, id, "")))
                .OrderBy(row => row.Group).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
            _historyRevision = _players.History.Revision;
            _encodedRoster = null;
        }
        return _roster;
    }

    private bool CanView(Client client) => !_stopped && (client.IsLocal
        ? _localAccess?.Invoke() == true
        : _localAccess == null && ReferenceEquals(_net, ZNet.instance) && _net != null && _net.IsDedicated() &&
          ZNet.World != null && _net.GetWorldUID() == _worldUid && _settings.Enabled.Value && client.Protocol == ControlProtocol.Version &&
          Ready(client.Peer) && IsAdmin(client.Peer));

    private byte[] CurrentModerationBytes()
    {
        var rows = CurrentRoster();
        return _encodedRoster ??= ModerationProtocol.Encode(rows);
    }

    private void SendModeration(Client client) =>
        client.Delivery.Send(client.Admin && CanView(client), CurrentModerationBytes,
            packet => client.Peer.m_rpc.Invoke(ModerationProtocol.State, packet));

    internal ModerationRow[]? ReadLocalModeration() =>
        _localClient != null && CanView(_localClient) ? CurrentRoster() : null;

    internal void ModerateLocal(bool unban, string id)
    {
        if (_localClient != null) Moderate(_localClient, unban, id);
    }

    private void ReceiveModeration(ZRpc rpc, ZPackage packet)
    {
        if (!_clients.TryGetValue(rpc, out var client) || !CanView(client) || !_settings.AllowAdminChanges.Value ||
            packet.Size() > ControlProtocol.MaxBytes || packet.Size() < 2) return;
        if (!ControlRequestRules.Spend(UnityEngine.Time.unscaledTime, client.Requests, ref client.Count, ref client.Next)) return;
        bool unban;
        string id;
        try
        {
            unban = packet.ReadBool();
            id = packet.ReadString();
            if (packet.GetPos() != packet.Size()) return;
        }
        catch (System.IO.IOException) { return; }
        catch (FormatException) { return; }
        Moderate(client, unban, id);
    }

    private void Moderate(Client client, bool unban, string id)
    {
        if (!CanView(client) || (!client.IsLocal && !_settings.AllowAdminChanges.Value)) return;
        var net = ZNet.instance;
        if (unban)
        {
            if (!net.Banned.Contains(id)) return;
            net.Unban(id);
        }
        else
        {
            if (!_players.History.CanBan(id) || net.Banned.Contains(id)) return;
            // InternalBan resolves player names first. Write the confirmed account verbatim.
            NativeAccess.Field<ZNet, SyncedList>("m_bannedList")(net).Add(id);
        }
        DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control,
            DescribeAdmin(client) + (unban ? " removed ban " : " submitted ban ") + PlayerConnectionNotices.CleanName(id) + ".");
    }
}
