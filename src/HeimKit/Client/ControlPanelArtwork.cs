using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace HeimKit.Client;

internal sealed class ControlPanelArtwork : IDisposable
{
    private static readonly string[] IconOrder =
    {
        "InfiniteTorches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch", "SharedMap", "DoorKeeper",
        "ForestRenewal", "SeaSalvage", "TameTravel", "HeimMender", "StationSupply"
    };

    internal enum ButtonRole { Scope, Feature, Close, Apply }

    private readonly Texture2D? _atlas, _banner;
    private readonly Sprite?[] _buttons = new Sprite?[4];

    internal Sprite? Button(ButtonRole role) => _buttons[(int)role];

    internal ControlPanelArtwork()
    {
        _atlas = Load("ControlPanel.png");
        _banner = Load("MenuBanner.png");
        _buttons[(int)ButtonRole.Scope] = ButtonSprite("MenuScope.png", new Vector4(22, 10, 22, 10));
        _buttons[(int)ButtonRole.Feature] = ButtonSprite("MenuFeature.png", new Vector4(18, 10, 18, 10));
        _buttons[(int)ButtonRole.Close] = ButtonSprite("MenuClose.png", new Vector4(28, 20, 28, 20));
        _buttons[(int)ButtonRole.Apply] = ButtonSprite("MenuApply.png", new Vector4(24, 20, 24, 20));
    }

    private static Texture2D? Load(string asset)
    {
        using var resource = typeof(ControlPanelArtwork).Assembly.GetManifestResourceStream("HeimKit.Client." + asset);
        if (resource == null) return null;
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(atlas, bytes.ToArray(), true))
        {
            UnityEngine.Object.Destroy(atlas);
            return null;
        }
        atlas.wrapMode = TextureWrapMode.Clamp;
        return atlas;
    }

    private static Sprite? ButtonSprite(string asset, Vector4 border)
    {
        var texture = Load(asset);
        if (texture == null) return null;
        // Preserve the detail at the edges when the same button is used at different widths.
        return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
            new Vector2(.5f, .5f), 200, 0, SpriteMeshType.FullRect, border);
    }

    internal bool PaintBanner(RectTransform rect)
    {
        if (_banner == null) return false;
        var image = rect.gameObject.AddComponent<RawImage>();
        image.texture = _banner;
        image.raycastTarget = false;
        return true;
    }

    internal bool Paint(RectTransform rect, int x, int y, int width, int height)
    {
        if (_atlas == null) return false;
        var image = rect.gameObject.AddComponent<RawImage>();
        image.texture = _atlas;
        image.uvRect = new Rect((x + .5f) / _atlas.width, 1f - (y + height - .5f) / _atlas.height,
            (width - 1f) / _atlas.width, (height - 1f) / _atlas.height);
        image.raycastTarget = false;
        return true;
    }

    internal void PaintFeatureIcon(RectTransform rect, string name)
    {
        int slot = Array.IndexOf(IconOrder, name);
        if (slot >= 0) Paint(rect, slot * 44, 312, 40, 34);
    }

    public void Dispose()
    {
        foreach (var button in _buttons)
        {
            if (button == null) continue;
            UnityEngine.Object.Destroy(button.texture);
            UnityEngine.Object.Destroy(button);
        }
        if (_atlas != null) UnityEngine.Object.Destroy(_atlas);
        if (_banner != null) UnityEngine.Object.Destroy(_banner);
    }
}
