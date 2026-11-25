using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HeimKit.Client;

public sealed class ControlPanelSlider : Slider
{
    // Physical mouse input is captured by the panel across row rebuilds. Keep
    // Unity's normal dispatch for touch and its navigation support for gamepads.
    public override void OnPointerDown(PointerEventData pointer)
    {
        if (pointer.pointerId != -1) base.OnPointerDown(pointer);
    }

    public override void OnDrag(PointerEventData pointer)
    {
        if (pointer.pointerId != -1) base.OnDrag(pointer);
    }

    internal bool TryGrab(Vector2 screenPoint, out float offset)
    {
        offset = 0f;
        if (!IsInteractable() || !RectTransformUtility.RectangleContainsScreenPoint(
            (RectTransform)transform, screenPoint)) return false;

        Select();
        if (RectTransformUtility.RectangleContainsScreenPoint(handleRect, screenPoint) &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(handleRect, screenPoint, null, out var grab))
            offset = grab.x;
        return true;
    }

    internal void MoveThumb(Vector2 screenPoint, float offset)
    {
        var travel = (RectTransform)handleRect.parent;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(travel, screenPoint, null, out var point))
            normalizedValue = Mathf.InverseLerp(travel.rect.xMin, travel.rect.xMax, point.x - offset);
    }
}
