using System;
using System.Collections.Generic;
using System.Globalization;
using HeimKit.Core.Control;
using HeimKit.Core.Control.Models;
using TMPro;
using HarmonyLib;
using Valheim.SettingsGui;
using UnityEngine;
using UnityEngine.UI;

namespace HeimKit.Client;

public sealed partial class ControlPanelView : MonoBehaviour
{
    internal sealed class Row
    {
        internal readonly string Name, Status;
        internal readonly List<Option> Options = new();
        internal readonly List<(NumericSettingState Entry, string Feedback)> Numbers = new();
        internal SwitchState? Entry;
        internal string Feedback = "";
        internal Row(string name, string status) { Name = name; Status = status; }
    }

    internal readonly struct Option
    {
        internal Option(SwitchState entry, string feedback) { Entry = entry; Feedback = feedback; }
        internal readonly SwitchState Entry;
        internal readonly string Feedback;
    }

    internal string ConnectionType = "", ConnectionStatus = "", Access = "", Footer = "", AdminMessage = "";
    internal string SettingsSource = "", MenuShortcut = "", LocalConfigHelp = "";
    internal IReadOnlyList<Row> Rows = Array.Empty<Row>();
    internal bool CanEdit;
    internal bool TraceTameTravel;
    internal Action<bool>? SetTameTravelTrace;
    internal bool HeimDefenderTrace;
    internal Action<bool>? SetHeimDefenderTrace;
    internal bool MapMarkers, MapTrace;
    internal Action<bool>? SetMapMarkers, SetMapTrace;
    internal bool TraceInteractions;
    internal Action<bool>? SetInteractionTrace;
    internal Action<SwitchState>? Click;
    internal Action<NumericSettingState, double>? ApplyNumber;
    private readonly Dictionary<(string Module, string Key), string> _numberDrafts = new();
    private readonly Dictionary<(string Module, string Key), ControlPanelSlider> _visibleSliders = new();
    private (string Module, string Key)? _grabbedSlider;
    private float _grabOffset;
    private string? _selected;
    private bool _clientSettings;
    private Vector2 _serverScrollPosition;
    private ControlPanelArtwork? _artwork;
    private Button _serverTab = null!, _clientTab = null!;
    private RectTransform _sidebar = null!, _divider = null!;
    private RectTransform _navigation = null!;
    private GameObject? _canvas;
    private Button _buttonStyle = null!;
    private TMP_Text _textStyle = null!;
    private GameObject? _tooltipTemplate;
    private RectTransform _tooltipLayer = null!;
    private RectTransform _content = null!;
    private ScrollRect _scroll = null!;
    private TextMeshProUGUI _headline = null!, _footer = null!, _admin = null!;

    internal bool Open(Action close)
    {
        var menu = Menu.instance;
        if (menu == null || menu.m_settingsPrefab == null) return false;
        if (_canvas == null)
        {
            DisposeCanvas();
            var gameplay = menu.m_settingsPrefab.GetComponentInChildren<Valheim.SettingsGui.GameplaySettings>(true);
            if (gameplay == null) return false;
            foreach (var tooltip in menu.m_settingsPrefab.GetComponentsInChildren<SettingsTooltip>(true))
            {
                _tooltipTemplate = Traverse.Create(tooltip).Field<GameObject>("m_tooltip").Value;
                if (_tooltipTemplate != null) break;
            }
            _buttonStyle = HarmonyLib.Traverse.Create(gameplay).Field<Button>("m_resetTutorial").Value;
            if (_buttonStyle == null) return false;
            _textStyle = _buttonStyle.GetComponentInChildren<TMP_Text>(true);
            if (_textStyle == null || _textStyle.font == null) return false;
            Build(close);
        }
        _canvas!.SetActive(true);
        return true;
    }

    internal void Close()
    {
        if (_administration) ClearModeration();
        _canvas?.SetActive(false);
        _numberDrafts.Clear();
        _grabbedSlider = null;
    }

    internal void ResetRows()
    {
        _selected = null;
        _clientSettings = false;
        _serverScrollPosition = Vector2.zero;
        if (_canvas != null) _content.anchoredPosition = Vector2.zero;
    }

    private void Build(Action close)
    {
        var root = Rect("HeimKit Settings", transform);
        _canvas = root.gameObject;
        _canvas.SetActive(false);
        var canvas = _canvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var nativeCanvas = _buttonStyle.GetComponentInParent<Canvas>(true);
        canvas.sortingOrder = nativeCanvas == null ? 20 : nativeCanvas.rootCanvas.sortingOrder + 20;
        var scaler = _canvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        // Keep the panel inset from the HUD on both narrow and wide displays.
        scaler.referenceResolution = new Vector2(1536, 900);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        _canvas.AddComponent<GraphicRaycaster>();

        var shade = Rect("Backdrop", root);
        Stretch(shade);
        shade.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .55f);

        var panel = Rect("Dialog", root);
        panel.sizeDelta = new Vector2(1140, 674);
        var background = panel.gameObject.AddComponent<Image>();
        background.color = PanelNavy;
        _artwork = new ControlPanelArtwork();
        var wood = Rect("Blue timber", panel);
        Stretch(wood, 4, 4);
        _artwork.Paint(wood, 0, 220, 512, 84);
        var tint = Rect("Timber shade", panel);
        Stretch(tint, 4, 4);
        var tintImage = tint.gameObject.AddComponent<Image>();
        tintImage.color = new Color(PanelNavy.r, PanelNavy.g, PanelNavy.b, .72f);
        tintImage.raycastTarget = false;
        AddOuterFrame(panel);

        var brand = Rect("HeimKit banner", panel);
        brand.anchorMin = brand.anchorMax = new Vector2(.5f, 1);
        brand.anchoredPosition = new Vector2(0, 22);
        brand.sizeDelta = new Vector2(1060, 119);
        if (!_artwork.PaintBanner(brand))
        {
            brand.anchoredPosition = new Vector2(0, 8);
            brand.sizeDelta = new Vector2(438, 90);
            var backing = brand.gameObject.AddComponent<Image>();
            backing.color = PanelNavy;
            backing.raycastTarget = false;
            var bannerTimber = Rect("Banner timber", brand);
            Stretch(bannerTimber, 3, 3);
            _artwork.Paint(bannerTimber, 0, 220, 512, 84);
            DrawFrame(brand);

            // Shield and lettering have different proportions in the atlas.
            // Give each the same on-screen height without distorting its artwork.
            var shield = Rect("Shield", brand);
            shield.anchoredPosition = new Vector2(-163, 0);
            shield.sizeDelta = new Vector2(74, 78);
            _artwork.Paint(shield, 22, 6, 170, 185);
            var wordmark = Rect("Wordmark", brand);
            wordmark.anchoredPosition = new Vector2(41, 0);
            wordmark.sizeDelta = new Vector2(320, 78);
            if (!_artwork.Paint(wordmark, 214, 58, 358, 87))
            {
                var title = Label(wordmark, "HeimKit", 58, TextAlignmentOptions.Center);
                title.color = HighlightGold;
                Stretch(title.rectTransform);
            }
        }

        const float bodyInset = 19;
        var body = Rect("Dialog content", panel);
        Stretch(body, bodyInset, 30);
        body.offsetMax = new Vector2(-bodyInset, -49);
        Stack(body, 6);

        var scopes = Rect("Settings scope", body);
        Line(scopes);
        _serverTab = Button(scopes, "Server Settings", 0, () => ShowScope(false), ControlPanelArtwork.ButtonRole.Scope);
        _clientTab = Button(scopes, "Client Settings", 0, () => ShowScope(true), ControlPanelArtwork.ButtonRole.Scope);
        _adminTab = Button(scopes, "Server Administration", 0, ShowAdministration, ControlPanelArtwork.ButtonRole.Scope);
        _adminTab.GetComponent<LayoutElement>().flexibleWidth = 1;
        _serverTab.GetComponent<LayoutElement>().flexibleWidth = 1;
        _clientTab.GetComponent<LayoutElement>().flexibleWidth = 1;

        _headline = Label(body, "", 17, TextAlignmentOptions.Center);
        _headline.richText = true;
        _headline.textWrappingMode = TextWrappingModes.NoWrap;
        _headline.enableAutoSizing = true;
        _headline.fontSizeMin = 12;
        _headline.fontSizeMax = 17;
        _headline.overflowMode = TextOverflowModes.Truncate;
        _headline.gameObject.AddComponent<LayoutElement>().preferredHeight = 25;
        _admin = Label(body, "", 15, TextAlignmentOptions.Center);

        var frame = Rect("Settings frame", body);
        frame.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1;
        frame.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .18f);
        var columns = Rect("Settings columns", frame);
        Stretch(columns, 1, 1);
        var columnsLayout = Line(columns);
        columnsLayout.spacing = 0;
        columnsLayout.childForceExpandHeight = true;

        _sidebar = Rect("Feature tabs", columns);
        var navSize = _sidebar.gameObject.AddComponent<LayoutElement>();
        navSize.minWidth = navSize.preferredWidth = 241;
        _sidebar.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .23f);
        _navigation = Rect("Tabs", _sidebar);
        Stretch(_navigation, 8, 8);
        _navigation.offsetMax = new Vector2(-8, -13);
        Stack(_navigation, 3);

        _divider = Rect("Tab divider", columns);
        var dividerSize = _divider.gameObject.AddComponent<LayoutElement>();
        dividerSize.minWidth = dividerSize.preferredWidth = 8;
        PaintTrim(_divider, 524, 204, 12, 140);

        var scroll = Rect("Settings", columns);
        var pane = scroll.gameObject.AddComponent<LayoutElement>();
        pane.minWidth = pane.preferredWidth = 0;
        pane.flexibleWidth = 1;
        scroll.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .04f);
        _scroll = scroll.gameObject.AddComponent<ScrollRect>();
        _scroll.horizontal = false;
        _scroll.movementType = ScrollRect.MovementType.Clamped;
        _scroll.scrollSensitivity = 0; // Read the wheel through Valheim input in ScrollUnderPointer.
        var viewport = Rect("Viewport", scroll);
        Stretch(viewport, 18, 14);
        viewport.gameObject.AddComponent<RectMask2D>();

        _content = Rect("Rows", viewport);
        _content.anchorMin = new Vector2(0, 1);
        _content.anchorMax = new Vector2(1, 1);
        _content.pivot = new Vector2(.5f, 1);
        _content.sizeDelta = Vector2.zero;
        Stack(_content, 5);
        _content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        _scroll.viewport = viewport;
        _scroll.content = _content;

        var scrollbar = Rect("Scrollbar", scroll);
        scrollbar.anchorMin = new Vector2(1, 0);
        scrollbar.anchorMax = Vector2.one;
        scrollbar.pivot = new Vector2(1, .5f);
        scrollbar.sizeDelta = new Vector2(10, -12);
        scrollbar.anchoredPosition = new Vector2(-4, 0);
        scrollbar.gameObject.AddComponent<Image>().color = new Color(.12f, .09f, .07f, .95f);
        var handle = Rect("Handle", scrollbar);
        Stretch(handle);
        var grip = handle.gameObject.AddComponent<Image>();
        grip.color = new Color(.76f, .49f, .20f);
        var bar = scrollbar.gameObject.AddComponent<Scrollbar>();
        bar.handleRect = handle;
        bar.targetGraphic = grip;
        bar.direction = Scrollbar.Direction.BottomToTop;
        _scroll.verticalScrollbar = bar;
        _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        viewport.offsetMax = new Vector2(-24, -14);
        // Span the body inset so the inner rails meet the outer uprights.
        AddRail(frame, 1, 0, 1, -bodyInset, bodyInset);
        AddRail(frame, 0, 0, 1, -bodyInset, bodyInset);

        var footer = Rect("Footer", body);
        var line = Line(footer);
        line.childAlignment = TextAnchor.MiddleLeft;
        var summary = Rect("Connection details", footer);
        var summarySize = summary.gameObject.AddComponent<LayoutElement>();
        summarySize.minWidth = summarySize.preferredWidth = 0;
        summarySize.flexibleWidth = 1;
        Stack(summary, 2);
        _footer = Label(summary, "", 15);
        _tooltipLayer = Rect("Tooltips", root);
        Stretch(_tooltipLayer);
        Button(footer, "Close", 145, close, ControlPanelArtwork.ButtonRole.Close);
    }

    private void ShowScope(bool clientSettings)
    {
        if (_clientSettings == clientSettings && !_administration) return;
        _administration = false;
        _target = null;
        if (clientSettings) _serverScrollPosition = _content.anchoredPosition;
        _clientSettings = clientSettings;
        _scroll.StopMovement();
        _content.anchoredPosition = clientSettings ? Vector2.zero : _serverScrollPosition;
        Refresh();
    }

    internal void Refresh()
    {
        if (_canvas == null || !_canvas.activeSelf) return;
        _adminTab.gameObject.SetActive(Moderation != null);
        if (Moderation != null) SetSelected(_adminTab, _administration, scopeTab: true);
        SetSelected(_serverTab, !_clientSettings && !_administration, scopeTab: true);
        SetSelected(_clientTab, _clientSettings, scopeTab: true);
        const string separator = "  <color=#9F8055>|</color>  ";
        _headline.text = _clientSettings ? "Applies to this installation" : string.Join(separator,
            StatusField("Type", ConnectionType), StatusField("Status", ConnectionStatus),
            StatusField("Access", Access), StatusField("Settings", SettingsSource));
        _admin.text = _administration && AdminMessage.Length != 0
            ? "Ban and unban are unavailable because AllowAdminChanges is off on this server." : AdminMessage;
        _admin.gameObject.SetActive(!_clientSettings && AdminMessage.Length != 0);
        _footer.text = Footer;
        _sidebar.gameObject.SetActive(!_clientSettings && !_administration);
        _divider.gameObject.SetActive(!_clientSettings && !_administration);

        var position = _clientSettings ? Vector2.zero : _content.anchoredPosition;
        _visibleSliders.Clear();
        ClearRows(_content);
        if (_administration) AddModeration();
        else if (_clientSettings) AddClientSettings();
        else AddServerSettings();
        LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
        _content.anchoredPosition = position;
    }

    private static string StatusField(string label, string value) =>
        $"<color=#DBB477>{label}:</color> {value}";

    private void AddServerSettings()
    {
        ClearRows(_navigation);
        Row? selected = null;
        foreach (var row in Rows)
            if (row.Name == _selected) { selected = row; break; }
        if (selected == null && Rows.Count != 0) selected = Rows[0];
        _selected = selected?.Name;

        foreach (var row in Rows)
        {
            var choice = Button(_navigation, row.Name, 0, () =>
            {
                _selected = row.Name;
                _scroll.StopMovement();
                _content.anchoredPosition = Vector2.zero;
                Refresh();
            }, ControlPanelArtwork.ButtonRole.Feature);
            var size = choice.GetComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = 30;
            var label = choice.GetComponentInChildren<TMP_Text>();
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.fontSize = 17;
            label.rectTransform.offsetMin = new Vector2(43, 0);
            AddFeatureIcon(choice.GetComponent<RectTransform>(), row.Name);
            SetSelected(choice, row == selected);
        }
        if (selected != null) AddRow(selected);
    }

    private void AddClientSettings()
    {
        Label(_content, "General", 29).color = HighlightGold;
        var row = Rect("Menu shortcut", _content);
        Line(row);
        var label = Label(row, "Menu shortcut", 18);
        var keycap = Label(row, MenuShortcut, 19, TextAlignmentOptions.Center);
        var size = keycap.gameObject.AddComponent<LayoutElement>();
        size.minWidth = 68;
        size.minHeight = 32;
        DrawFrame(keycap.rectTransform);
        var target = keycap.gameObject.AddComponent<Selectable>();
        target.transition = Selectable.Transition.None;
        target.navigation = new Navigation { mode = Navigation.Mode.None };
        AddTooltip(label, target, "Menu shortcut", LocalConfigHelp);
        Notice(_content, "Edit heimkit.client.cfg and restart Valheim to change it.");
        AddClientSwitch("Object interaction logging", TraceInteractions, SetInteractionTrace,
            "Record container actions immediately in this client's BepInEx log.");

        Label(_content, "HeimDefender", 29).color = HighlightGold;
        AddClientSwitch("Debug logging", HeimDefenderTrace, SetHeimDefenderTrace, "Record native Ballista targets and ammunition observed on this PC.");
        Label(_content, "SharedMap", 29).color = HighlightGold;
        AddClientSwitch("Automatic markers", MapMarkers, SetMapMarkers, "Show nearby discoveries without visiting a cartography table.");
        AddClientSwitch("Debug logging", MapTrace, SetMapTrace, "Record marker delivery and personal-map reconciliation on this PC.");
        Label(_content, "TameTravel", 29).color = HighlightGold;
        AddClientSwitch("Debug logging", TraceTameTravel, SetTameTravelTrace,
            "Record local portal and dungeon trips and nearby animal IDs on this PC.");
    }

    private void AddClientSwitch(string label, bool enabled, Action<bool>? save, string description)
    {
        var toggle = AddCheckboxRow(_content, label, enabled, description);
        toggle.onValueChanged.AddListener(value => save?.Invoke(value));
    }

    private void ClearRows(Transform parent)
    {
        foreach (Transform child in parent)
        {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }
    }

    private void AddRow(Row row)
    {
        var header = Rect("Feature status", _content);
        Line(header);
        var name = Label(header, row.Name, 29);
        name.color = HighlightGold;
        FillLine(name);
        if (row.Name == ControlProtocol.DiagnosticsGroup)
        {
            foreach (var option in row.Options)
            {
                var entry = option.Entry;
                if (entry.Definition.Key == "DebugVerbosity")
                {
                    var line = Rect("Log detail", _content);
                    Line(line);
                    AddDropdown(line, entry.Definition.Label, new List<string> { "Normal", "Verbose" },
                        entry.Value ? 1 : 0, CanEdit, choice =>
                        {
                            if ((choice == 1) != entry.Value) Click?.Invoke(entry);
                        });
                    Notice(_content, option.Feedback);
                }
                else AddSwitchRow(_content, entry, option.Feedback);
            }
            return;
        }
        var status = row.Entry?.Status ?? SwitchStatus.Unavailable;
        bool failed = status == SwitchStatus.Unavailable &&
            row.Entry?.Reason is UnavailableReason.StoppedAfterError or UnavailableReason.FailureProtection;
        string state = status switch
        {
            SwitchStatus.On => "On",
            SwitchStatus.Off => "Off",
            _ => failed ? "Disabled after error" : "Unavailable"
        };
        Label(header, "Status:", 17).color = Color.white;
        Label(header, state, 17).color = failed ? new Color(1f, .60f, .44f) :
            status == SwitchStatus.On ? new Color(.61f, .92f, .39f) : new Color(1f, .66f, .38f);
        var rule = Rect("Feature heading rule", _content);
        rule.gameObject.AddComponent<LayoutElement>().preferredHeight = 1;
        var ruleImage = rule.gameObject.AddComponent<Image>();
        ruleImage.color = FrameGold;
        ruleImage.raycastTarget = false;
        if (status == SwitchStatus.Unavailable) Notice(_content, row.Status);

        if (row.Options.Count >= 10)
        {
            var columns = Rect("Switch columns", _content);
            var layout = Line(columns);
            layout.spacing = 22;
            layout.childAlignment = TextAnchor.UpperLeft;
            var left = AddSwitchColumn(columns);
            var right = AddSwitchColumn(columns);
            if (row.Entry is { } entry) AddSwitchRow(left, entry, row.Feedback);
            int enabledRowCount = row.Entry.HasValue ? 1 : 0;
            int firstRight = (row.Options.Count + enabledRowCount + 1) / 2 - enabledRowCount;
            for (int i = 0; i < row.Options.Count; i++)
            {
                var option = row.Options[i];
                AddSwitchRow(i < firstRight ? left : right, option.Entry, option.Feedback);
            }
        }
        else
        {
            if (row.Entry is { } entry) AddSwitchRow(_content, entry, row.Feedback);
            foreach (var option in row.Options)
            {
                var line = AddSwitchRow(_content, option.Entry, option.Feedback);
                if (row.Name == "HeimDefender" && option.Entry.Definition.Key == "FreeWoodBolts")
                {
                    foreach (var number in row.Numbers)
                        if (number.Entry.Key == "BoltType")
                            AddBoltDropdown(line, number.Entry, number.Feedback, row.Entry?.Reason != UnavailableReason.FailureProtection);
                }
            }
        }
        foreach (var number in row.Numbers)
            if (row.Name != "HeimDefender" || number.Entry.Key != "BoltType")
                AddNumber(number.Entry, number.Feedback, row.Entry?.Reason != UnavailableReason.FailureProtection);
    }

    private static RectTransform AddSwitchColumn(Transform parent)
    {
        var column = Rect("Switch column", parent);
        Stack(column, 3);
        var size = column.gameObject.AddComponent<LayoutElement>();
        size.minWidth = size.preferredWidth = 0;
        size.flexibleWidth = 1;
        return column;
    }

    private Transform AddSwitchRow(Transform parent, SwitchState entry, string feedback)
    {
        var toggle = AddCheckboxRow(parent, entry.Definition.Label, entry.Value, SettingDescription(entry.Definition.Description));
        toggle.interactable = CanEdit && !entry.PendingValue.HasValue;
        toggle.onValueChanged.AddListener(_ =>
        {
            toggle.SetIsOnWithoutNotify(entry.Value);
            Click?.Invoke(entry);
        });
        Notice(parent, feedback);
        return toggle.transform.parent;
    }

    private void AddBoltDropdown(Transform line, NumericSettingState entry, string feedback, bool available)
    {
        AddDropdown(line, entry.Label, new List<string>(Features.HeimDefender.HeimDefenderSettings.BoltLabels),
            (int)entry.Value, CanEdit && available, choice => ApplyNumber?.Invoke(entry, choice));
        Notice(_content, feedback);
    }

    private void AddDropdown(Transform line, string label, List<string> options, int selected, bool editable, Action<int> apply)
    {
        Label(line, label, 17);
        var root = TMP_DefaultControls.CreateDropdown(default);
        root.transform.SetParent(line, false);
        var size = root.AddComponent<LayoutElement>();
        size.minWidth = size.preferredWidth = 165;
        size.minHeight = size.preferredHeight = 30;
        var dropdown = root.GetComponent<TMP_Dropdown>();
        dropdown.ClearOptions();
        dropdown.AddOptions(options);
        StyleDropdown(dropdown);
        dropdown.SetValueWithoutNotify(selected);
        dropdown.RefreshShownValue();
        dropdown.interactable = editable;
        dropdown.onValueChanged.AddListener(choice =>
        {
            dropdown.SetValueWithoutNotify(selected);
            apply(choice);
        });
    }

    private void StyleDropdown(TMP_Dropdown dropdown)
    {
        foreach (var text in new[] { dropdown.captionText, dropdown.itemText })
        {
            text.font = _textStyle.font;
            text.fontSharedMaterial = _textStyle.fontSharedMaterial;
            text.fontStyle = _textStyle.fontStyle;
            text.fontSize = 17;
            text.color = Ivory;
            text.raycastTarget = false;
        }

        var face = Rect("Button face", dropdown.transform);
        Stretch(face);
        face.SetAsFirstSibling();
        dropdown.GetComponent<Image>().enabled = false;
        dropdown.targetGraphic = ButtonSurface(face, ControlPanelArtwork.ButtonRole.Feature);
        dropdown.colors = ButtonColors();
        dropdown.captionText.rectTransform.offsetMin = new Vector2(10, 1);
        dropdown.captionText.rectTransform.offsetMax = new Vector2(-28, -1);
        var arrow = (RectTransform)dropdown.transform.Find("Arrow");
        arrow.GetComponent<Image>().enabled = false;
        var chevron = Label(arrow, "▼", 14, TextAlignmentOptions.Center);
        chevron.color = HighlightGold;
        Stretch(chevron.rectTransform);

        // TMP clones the inactive template when the list opens.
        var template = dropdown.template;
        template.GetComponent<Image>().color = PanelNavy;
        var frame = Rect("List frame", template);
        Stretch(frame);
        frame.SetAsFirstSibling();
        var border = frame.gameObject.AddComponent<ControlPanelBevel>();
        border.Surface = ControlPanelBevel.Finish.Recess;
        border.raycastTarget = false;

        var scroll = template.GetComponent<ScrollRect>();
        scroll.verticalScrollbar.gameObject.SetActive(false);
        scroll.verticalScrollbar = null;
        Stretch(scroll.viewport, 3, 0);
        var option = dropdown.itemText.transform.parent.GetComponent<Toggle>();
        const float rowHeight = 30;
        ((RectTransform)option.transform).sizeDelta = new Vector2(0, rowHeight);
        scroll.content.sizeDelta = new Vector2(0, rowHeight + 8);
        template.sizeDelta = new Vector2(0, rowHeight * dropdown.options.Count + 8);
        option.targetGraphic.color = Color.white;
        var colors = ButtonColors();
        colors.normalColor = PanelNavy;
        colors.highlightedColor = colors.selectedColor = new Color(.29f, .18f, .08f);
        colors.pressedColor = new Color(.41f, .24f, .08f);
        option.colors = colors;

        var indicator = Rect("Gold checkmark", option.graphic.transform);
        Stretch(indicator, 2, 2);
        option.graphic.enabled = false;
        var mark = indicator.gameObject.AddComponent<ControlPanelMark>();
        mark.IsCheckmark = true;
        mark.raycastTarget = false;
        option.graphic = mark;
        dropdown.itemText.rectTransform.offsetMin = new Vector2(27, 1);
    }

    private void AddNumber(NumericSettingState entry, string feedback, bool available)
    {
        var line = Rect("Numeric setting", _content);
        Line(line);
        var label = Label(line, entry.Label, 18);
        FillLine(label);
        var culture = CultureInfo.InvariantCulture;
        double shownValue = Math.Round(entry.Value, MidpointRounding.AwayFromZero);
        string saved = shownValue.ToString("0", culture);
        string range = $"{entry.Minimum.ToString(culture)}–{entry.Maximum.ToString(culture)}";
        var key = (entry.Module, entry.Key);
        bool editable = CanEdit && available;
        var slider = AddNumericSlider(line, entry, editable);
        string details = $"{entry.Description}\n\nRange {range}. {entry.Applies}";
        string tooltip = feedback.Length == 0 ? details : details + "\n\n" + feedback;
        var help = AddTooltip(label, slider, entry.Label, tooltip);
        if (!editable)
        {
            Label(line, saved, 18);
            return;
        }

        _visibleSliders.Add(key, slider);
        var field = Rect("Value", line);
        var size = field.gameObject.AddComponent<LayoutElement>();
        size.minWidth = size.preferredWidth = 100;
        size.minHeight = size.preferredHeight = 35;
        var background = field.gameObject.AddComponent<ControlPanelBevel>();
        background.Surface = ControlPanelBevel.Finish.Recess;

        var viewport = Rect("Value viewport", field);
        Stretch(viewport, 6, 2);
        viewport.gameObject.AddComponent<RectMask2D>();
        var text = Label(viewport, "", 18, TextAlignmentOptions.Center);
        Stretch(text.rectTransform);
        var input = field.gameObject.AddComponent<TMP_InputField>();
        input.targetGraphic = background;
        input.textViewport = viewport;
        input.textComponent = text;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.characterLimit = 12;
        input.SetTextWithoutNotify(_numberDrafts.TryGetValue(key, out string draft) ? draft : saved);

        var apply = Button(line, "Apply", 82, () =>
        {
            if (!Read(out double value)) return;
            _numberDrafts.Remove(key);
            ApplyNumber?.Invoke(entry, value);
        }, ControlPanelArtwork.ButtonRole.Apply);
        void UpdateInput(string value)
        {
            if (value == saved) _numberDrafts.Remove(key);
            else _numberDrafts[key] = value;
            bool valid = Read(out double requested);
            apply.interactable = valid && requested != entry.Value;
            if (valid) slider.SetValueWithoutNotify((float)requested);
            text.color = valid ? Ivory : new Color(1f, .60f, .44f);
            help?.SetTexts(entry.Label, valid ? tooltip : tooltip + $"\n\nEnter a whole number from {range}.");
        }
        bool Read(out double value) =>
            double.TryParse(input.text, NumberStyles.None, culture, out value) && entry.AcceptsWhole(value);

        input.onValueChanged.AddListener(UpdateInput);
        slider.onValueChanged.AddListener(value => input.text = value.ToString("0", culture));
        UpdateInput(input.text);
    }

    private ControlPanelSlider AddNumericSlider(RectTransform line, NumericSettingState entry, bool editable)
    {
        var rect = Rect("Slider", line);
        var layout = rect.gameObject.AddComponent<LayoutElement>();
        layout.minWidth = layout.preferredWidth = 190;
        layout.minHeight = layout.preferredHeight = 35;

        AddTransparentHitArea(rect);
        var travel = Rect("Slider travel", rect);
        Stretch(travel, 10, 0);

        var track = Rect("Track", travel);
        SetSliderBar(track);
        var background = track.gameObject.AddComponent<ControlPanelBevel>();
        background.Surface = ControlPanelBevel.Finish.Recess;
        background.raycastTarget = false;

        var progress = Rect("Fill area", travel);
        SetSliderBar(progress, 6);
        var fill = Rect("Fill", progress);
        Stretch(fill);
        var fillImage = fill.gameObject.AddComponent<ControlPanelBevel>();
        fillImage.Surface = ControlPanelBevel.Finish.Gold;
        fillImage.raycastTarget = false;

        var handle = Rect("Handle", travel);
        handle.pivot = new Vector2(.5f, .5f);
        handle.sizeDelta = new Vector2(20, 0);
        handle.anchoredPosition = Vector2.zero;

        // The slider root receives pointer input across the track and thumb.
        var thumbRect = Rect("Gold slider thumb", handle);
        thumbRect.anchorMin = thumbRect.anchorMax = new Vector2(.5f, .5f);
        thumbRect.pivot = new Vector2(.5f, .5f);
        thumbRect.anchoredPosition = Vector2.zero;
        thumbRect.sizeDelta = new Vector2(24, 28);
        var thumb = thumbRect.gameObject.AddComponent<ControlPanelMark>();

        thumb.raycastTarget = false;
        var slider = rect.gameObject.AddComponent<ControlPanelSlider>();
        slider.fillRect = fill;
        slider.handleRect = handle;
        slider.targetGraphic = thumb;
        var colors = ColorBlock.defaultColorBlock;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1f, .97f, .86f);
        colors.pressedColor = new Color(.88f, .76f, .59f);
        colors.disabledColor = new Color(.75f, .75f, .75f);
        slider.colors = colors;
        slider.minValue = (float)entry.Minimum;
        slider.maxValue = (float)entry.Maximum;
        slider.wholeNumbers = true;
        slider.SetValueWithoutNotify((float)Math.Round(entry.Value, MidpointRounding.AwayFromZero));
        slider.interactable = editable;
        return slider;
    }

    private static void AddTransparentHitArea(RectTransform rect)
    {
        var image = rect.gameObject.AddComponent<Image>();
        image.color = Color.clear;
        image.canvasRenderer.cullTransparentMesh = false;
    }

    private static void SetSliderBar(RectTransform rect, float height = 11)
    {
        rect.anchorMin = new Vector2(0, .5f);
        rect.anchorMax = new Vector2(1, .5f);
        rect.sizeDelta = new Vector2(0, height);
    }

    private SettingsTooltip? AddTooltip(TMP_Text label, Selectable anchor, string title, string details)
    {
        if (_tooltipTemplate == null) return null;
        var popup = Instantiate(_tooltipTemplate, _tooltipLayer, false);
        popup.SetActive(false);
        var group = popup.GetComponent<CanvasGroup>() ?? popup.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        foreach (var text in popup.GetComponentsInChildren<TMP_Text>(true)) text.richText = false;
        var tooltip = label.gameObject.AddComponent<SettingsTooltip>();
        var fields = Traverse.Create(tooltip);
        fields.Field<GameObject>("m_tooltip").Value = popup;
        fields.Field<Selectable>("m_selectableOverride").Value = anchor;
        fields.Field<SettingsTooltip.TooltipAlignment>("m_tooltipAlignment").Value = SettingsTooltip.TooltipAlignment.Right;
        // The popup renders above the pane and follows the label's lifetime.
        label.gameObject.AddComponent<ControlTooltipLifetime>().Attach(popup, label);
        label.raycastTarget = true;
        tooltip.SetTexts(title, details);
        return tooltip;
    }

    private static string SettingDescription(string description)
    {
        foreach (string note in new[] { "File edits require a restart.", "HeimKit Settings applies changes live.", "The admin menu applies changes live.",
            "Restart after edits.", "Reloads during play." })
            description = description.Replace(note, "");
        return description.Trim();
    }

    internal void UpdateSliderPointer()
    {
        bool down = Input.GetMouseButtonDown(0);
        bool up = Input.GetMouseButtonUp(0);
        if (!Application.isFocused || (!down && !up && !Input.GetMouseButton(0)))
        {
            _grabbedSlider = null;
            return;
        }

        Vector2 pointer = Input.mousePosition;
        if (down)
        {
            _grabbedSlider = null;
            if (!RectTransformUtility.RectangleContainsScreenPoint(_scroll.viewport, pointer)) return;
            foreach (var item in _visibleSliders)
            {
                if (!item.Value.TryGrab(pointer, out _grabOffset)) continue;
                _grabbedSlider = item.Key;
                _scroll.StopMovement();
                break;
            }
        }
        if (_grabbedSlider is { } key && _visibleSliders.TryGetValue(key, out var slider))
            slider.MoveThumb(pointer, _grabOffset);
        else
            _grabbedSlider = null;
        if (up) _grabbedSlider = null;
    }

    internal void ScrollUnderPointer()
    {
        float wheel = ZInput.GetMouseScrollWheel();
        if (wheel == 0 || _scroll.viewport == null ||
            !RectTransformUtility.RectangleContainsScreenPoint(_scroll.viewport, UnityEngine.Input.mousePosition)) return;
        float overflow = _content.rect.height - _scroll.viewport.rect.height;
        if (overflow <= 0) return;
        _scroll.StopMovement();
        _scroll.verticalNormalizedPosition = Mathf.Clamp01(_scroll.verticalNormalizedPosition + Mathf.Sign(wheel) * 42f / overflow);
    }

    private static void FillLine(TMP_Text label)
    {
        var layout = label.gameObject.AddComponent<LayoutElement>();
        layout.minWidth = layout.preferredWidth = 0;
        layout.flexibleWidth = 1;
    }

    private Toggle AddCheckboxRow(Transform parent, string title, bool enabled, string description)
    {
        var line = Rect(title, parent);
        Line(line);
        var rect = Rect("Switch", line);
        var layout = rect.gameObject.AddComponent<LayoutElement>();
        layout.minWidth = layout.preferredWidth = 32;
        layout.minHeight = layout.preferredHeight = 28;
        var control = rect.gameObject.AddComponent<Toggle>();
        var visual = Rect("Background", rect);
        visual.sizeDelta = new Vector2(24, 24);
        var box = visual.gameObject.AddComponent<ControlPanelBevel>();
        box.Surface = enabled ? ControlPanelBevel.Finish.Bronze : ControlPanelBevel.Finish.Recess;
        control.targetGraphic = box;
        var indicator = Rect("Checkmark", rect);
        indicator.sizeDelta = new Vector2(22, 22);
        var mark = indicator.gameObject.AddComponent<ControlPanelMark>();
        mark.IsCheckmark = true;
        mark.raycastTarget = false;
        control.graphic = mark;
        control.SetIsOnWithoutNotify(enabled);
        var label = Label(line, title, 17);
        FillLine(label);
        if (description.Length != 0) AddTooltip(label, control, title, description);
        return control;
    }

    private void Notice(Transform parent, string text)
    {
        if (text.Length != 0) Label(parent, text, 16);
    }

    private Button Button(Transform parent, string text, float width, Action action, ControlPanelArtwork.ButtonRole role)
    {
        var rect = Rect(text, parent);
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = ButtonSurface(rect, role);
        button.colors = ButtonColors();
        button.onClick.AddListener(() => action());
        var label = Label(rect, text, 18, TextAlignmentOptions.Center);
        Stretch(label.rectTransform, 3, 0);
        var layout = rect.gameObject.AddComponent<LayoutElement>();
        layout.minWidth = layout.preferredWidth = width;
        layout.minHeight = layout.preferredHeight = 35;
        return button;
    }

    private Graphic ButtonSurface(RectTransform rect, ControlPanelArtwork.ButtonRole role)
    {
        var sprite = _artwork?.Button(role);
        if (sprite != null)
        {
            var art = rect.gameObject.AddComponent<Image>();
            art.sprite = sprite;
            art.type = Image.Type.Sliced;
            return art;
        }
        else
        {
            var bevel = rect.gameObject.AddComponent<ControlPanelBevel>();
            bevel.Surface = role is ControlPanelArtwork.ButtonRole.Apply or ControlPanelArtwork.ButtonRole.Close
                ? ControlPanelBevel.Finish.Hammered : ControlPanelBevel.Finish.Steel;
            return bevel;
        }
    }

    private static ColorBlock ButtonColors()
    {
        var colors = ColorBlock.defaultColorBlock;
        colors.highlightedColor = new Color(1f, .94f, .81f);
        colors.pressedColor = new Color(.75f, .65f, .52f);
        colors.selectedColor = colors.highlightedColor;
        colors.disabledColor = new Color(.48f, .48f, .48f);
        return colors;
    }

    private void SetSelected(Button button, bool selected, bool scopeTab = false)
    {
        var glow = button.GetComponentInChildren<ControlPanelSelectionGlow>(true);
        if (selected && glow == null)
        {
            var outline = Rect("Selection glow", button.transform);
            Stretch(outline);
            outline.SetAsFirstSibling();
            glow = outline.gameObject.AddComponent<ControlPanelSelectionGlow>();
            glow.ScopeTab = scopeTab;
            glow.raycastTarget = false;
        }
        if (glow != null) glow.gameObject.SetActive(selected);

        if (button.targetGraphic is Image art)
        {
            art.color = selected ? Color.white : new Color(.64f, .67f, .74f);
            button.GetComponentInChildren<TMP_Text>().color = selected ? HighlightGold : Ivory;
        }
        else if (button.targetGraphic is ControlPanelBevel bevel)
            bevel.Surface = selected ? ControlPanelBevel.Finish.Bronze : ControlPanelBevel.Finish.Steel;
    }

    private TextMeshProUGUI Label(Transform parent, string text, float size, TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft)
    {
        var labelObject = Rect("Label", parent).gameObject;
        // Assign the native font before TextMesh Pro enables the label.
        labelObject.SetActive(false);
        var label = labelObject.AddComponent<TextMeshProUGUI>();
        label.font = _textStyle.font;
        label.fontSharedMaterial = _textStyle.fontSharedMaterial;
        label.color = Ivory;
        label.fontSize = size;
        label.fontStyle = _textStyle.fontStyle;
        label.alignment = alignment;
        label.richText = false;
        label.raycastTarget = false;
        label.text = text;
        labelObject.SetActive(true);
        return label;
    }

    private void AddFeatureIcon(RectTransform button, string name)
    {
        var icon = Rect("Feature icon", button);
        icon.anchorMin = icon.anchorMax = new Vector2(0, .5f);
        icon.pivot = new Vector2(0, .5f);
        icon.anchoredPosition = new Vector2(11, 0);
        icon.sizeDelta = new Vector2(24, 20.4f);
        _artwork?.PaintFeatureIcon(icon, name);
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    private static readonly Color FrameGold = new(.73f, .46f, .17f, .92f);
    private static readonly Color PanelNavy = new(.041f, .084f, .126f);
    private static readonly Color HighlightGold = new(1f, .78f, .43f);
    private static readonly Color Ivory = new(.98f, .93f, .83f);

    private void AddOuterFrame(RectTransform panel)
    {
        AddRail(panel, 0, 0, 1, 0, 0);
        AddRail(panel, 1, 0, 1, 0, 0);

        foreach (float edge in new[] { 0f, 1f })
        {
            var vertical = Rect("Hammered bronze stile", panel);
            vertical.anchorMin = new Vector2(edge, 0);
            vertical.anchorMax = new Vector2(edge, 1);
            vertical.pivot = new Vector2(edge, .5f);
            vertical.sizeDelta = new Vector2(8, 0);
            vertical.anchoredPosition = Vector2.zero;
            PaintTrim(vertical, 524, 204, 12, 140);
        }
        foreach (var corner in new[] { Vector2.zero, Vector2.up, Vector2.right, Vector2.one })
        {
            var rivet = Rect("Bronze corner", panel);
            rivet.anchorMin = rivet.anchorMax = rivet.pivot = corner;
            rivet.sizeDelta = new Vector2(22, 22);
            rivet.anchoredPosition = Vector2.zero;
            var cap = rivet.gameObject.AddComponent<ControlPanelBevel>();
            cap.Surface = ControlPanelBevel.Finish.Hammered;
            cap.raycastTarget = false;
            var stud = Rect("Corner stud", rivet);
            stud.anchorMin = stud.anchorMax = stud.pivot = new Vector2(.5f, .5f);
            stud.anchoredPosition = Vector2.zero;
            stud.sizeDelta = new Vector2(13, 13);
            stud.gameObject.AddComponent<ControlPanelMark>().raycastTarget = false;
        }
    }

    private void AddRail(RectTransform panel, float height, float left, float right, float leftInset, float rightInset)
    {
        var rail = Rect("Hammered bronze rail", panel);
        rail.anchorMin = new Vector2(left, height);
        rail.anchorMax = new Vector2(right, height);
        rail.offsetMin = new Vector2(leftInset, height == 0 ? 0 : -8);
        rail.offsetMax = new Vector2(rightInset, height == 0 ? 8 : 0);
        PaintTrim(rail, 0, 204, 480, 12);
    }

    private void PaintTrim(RectTransform rect, int x, int y, int width, int height)
    {
        if (_artwork?.Paint(rect, x, y, width, height) == true) return;
        var image = rect.gameObject.AddComponent<Image>();
        image.color = FrameGold;
        image.raycastTarget = false;
    }

    private static void DrawFrame(RectTransform frame)
    {
        FrameEdge(frame, "Top", Vector2.up, Vector2.one, new Vector2(0, 1));
        FrameEdge(frame, "Bottom", Vector2.zero, Vector2.right, new Vector2(0, 1));
        FrameEdge(frame, "Left", Vector2.zero, Vector2.up, new Vector2(1, 0));
        FrameEdge(frame, "Right", Vector2.right, Vector2.one, new Vector2(1, 0));
    }

    private static Image FrameEdge(Transform parent, string name, Vector2 min, Vector2 max, Vector2 size)
    {
        var edge = Rect(name, parent);
        edge.anchorMin = min;
        edge.anchorMax = max;
        edge.pivot = min;
        edge.anchoredPosition = Vector2.zero;
        edge.sizeDelta = size;
        var image = edge.gameObject.AddComponent<Image>();
        image.color = FrameGold;
        image.raycastTarget = false;
        return image;
    }

    private static void Stretch(RectTransform rect, float x = 0, float y = 0)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(x, y);
        rect.offsetMax = new Vector2(-x, -y);
    }

    private static VerticalLayoutGroup Stack(RectTransform rect, float spacing)
    {
        var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = spacing;
        layout.childControlWidth = layout.childControlHeight = layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return layout;
    }

    private static HorizontalLayoutGroup Line(RectTransform rect)
    {
        var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 8;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        return layout;
    }

    private void DisposeCanvas()
    {
        _artwork?.Dispose();
        _artwork = null;
        if (_canvas == null) return;
        _canvas.SetActive(false);
        Destroy(_canvas);
        _canvas = null;
    }

    private void OnDestroy() => DisposeCanvas();
}
