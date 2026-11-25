using System;
using System.Linq;
using HeimKit.Core.Control;
using UnityEngine;
using UnityEngine.UI;

namespace HeimKit.Client;

public sealed partial class ControlPanelView
{
    private const int PageSize = 16;
    internal ModerationRow[]? Moderation;
    internal Action<bool, string>? Moderate;
    private Button _adminTab = null!;
    private bool _administration;
    private ModerationRow? _target;
    private RosterGroup _group;
    private int _page;

    internal void ClearModeration()
    {
        Moderation = null;
        _target = null;
        _page = 0;
        _group = RosterGroup.Online;
        if (_adminTab != null) _adminTab.gameObject.SetActive(false);
        if (_administration && _canvas != null) ClearRows(_content);
        _administration = false;
    }

    private void ShowAdministration()
    {
        if (Moderation == null || _administration) return;
        if (!_clientSettings) _serverScrollPosition = _content.anchoredPosition;
        _clientSettings = false;
        _administration = true;
        _content.anchoredPosition = Vector2.zero;
        Refresh();
    }

    private void AddModeration()
    {
        if (Moderation == null) return;
        var filters = Rect("Player lists", _content);
        Line(filters);
        GroupButton(filters, RosterGroup.Online, "Online players");
        GroupButton(filters, RosterGroup.Previous, "Previous visitors");
        GroupButton(filters, RosterGroup.Banned, "Native bans");
        if (!CanEdit) _target = null;
        if (_target is { } target)
        {
            bool unban = target.Group == RosterGroup.Banned;
            string verb = unban ? "Unban" : "Ban";
            Label(_content, verb + " " + target.Label + "?", 20);
            var confirm = Rect("Confirm target", _content);
            Line(confirm);
            Button(confirm, verb, 120, () =>
            {
                if (!ReferenceEquals(_target, target)) return;
                _target = null;
                _group = RosterGroup.Banned;
                _page = 0;
                Moderate?.Invoke(unban, target.Id);
                Refresh();
            }, ControlPanelArtwork.ButtonRole.Apply);
            Button(confirm, "Cancel", 120, () => { _target = null; Refresh(); }, ControlPanelArtwork.ButtonRole.Close);
            return;
        }
        var entries = Moderation.Where(row => row.Group == _group).ToArray();
        _page = Math.Min(_page, Math.Max(0, (entries.Length - 1) / PageSize));
        if (entries.Length == 0) Notice(_content, "No entries.");
        foreach (var row in entries.Skip(_page * PageSize).Take(PageSize))
        {
            var button = Button(_content, row.Label, 0, () =>
            {
                _target = row;
                _content.anchoredPosition = Vector2.zero;
                Refresh();
            }, ControlPanelArtwork.ButtonRole.Feature);
            button.interactable = CanEdit && (row.Group == RosterGroup.Banned ||
                !row.Id.StartsWith("session:", StringComparison.Ordinal));
        }
        if (entries.Length <= PageSize) return;
        var pages = Rect("Pages", _content);
        Line(pages);
        Button(pages, "Previous", 120, () => { _page--; Refresh(); }, ControlPanelArtwork.ButtonRole.Scope).interactable = _page > 0;
        Button(pages, "Next", 120, () => { _page++; Refresh(); }, ControlPanelArtwork.ButtonRole.Scope).interactable = (_page + 1) * PageSize < entries.Length;
    }

    private void GroupButton(RectTransform parent, RosterGroup group, string label)
    {
        var button = Button(parent, label, 180, () => { _group = group; _page = 0; _target = null; Refresh(); },
            ControlPanelArtwork.ButtonRole.Scope);
        SetSelected(button, _group == group);
    }
}
