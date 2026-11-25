using TMPro;
using UnityEngine;

namespace HeimKit.Client;

internal sealed class ControlTooltipLifetime : MonoBehaviour, ICanvasRaycastFilter
{
    private GameObject? _popup;
    private RectTransform? _background;
    private TMP_Text? _label;
    private readonly Vector3[] _corners = new Vector3[4];

    internal void Attach(GameObject popup, TMP_Text label)
    {
        _popup = popup;
        _label = label;
        _background = Utils.FindChild(popup.transform, "Background") as RectTransform;
    }

    public bool IsRaycastLocationValid(Vector2 point, Camera eventCamera)
    {
        if (_label == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _label.rectTransform, point, eventCamera, out var local)) return false;
        // The layout may stretch the label across the row. Only the rendered text is a hover target.
        var bounds = _label.textBounds;
        return local.x >= bounds.min.x && local.x <= bounds.max.x &&
            local.y >= bounds.min.y && local.y <= bounds.max.y;
    }

    private void LateUpdate()
    {
        if (_popup == null || !_popup.activeInHierarchy || _background == null) return;

        // The tooltip layer is a screen-space overlay. Move the visible
        // background rather than assuming the popup and background share a pivot.
        _background.GetWorldCorners(_corners);
        const float margin = 8f;
        var min = _corners[0];
        var max = _corners[2];
        float width = max.x - min.x, height = max.y - min.y;
        float left = min.x, bottom = min.y;
        if (!ZInput.IsExclusiveGamepadActive())
        {
            var pointer = Input.mousePosition;
            left = pointer.x + 16f;
            bottom = pointer.y - height * .5f;
            if (left + width > Screen.width - margin)
                bottom = pointer.y - height - 12f;
        }
        left = Mathf.Clamp(left, margin, Mathf.Max(margin, Screen.width - width - margin));
        bottom = Mathf.Clamp(bottom, margin, Mathf.Max(margin, Screen.height - height - margin));
        _popup.transform.position += new Vector3(left - min.x, bottom - min.y, 0);
    }

    private void OnDestroy()
    {
        if (_popup != null) Destroy(_popup);
    }
}
