using UnityEngine;
using UnityEngine.UI;

namespace HeimKit.Client;

public sealed class ControlPanelBevel : MaskableGraphic
{
    internal enum Finish { Steel, Bronze, Recess, Gold, Hammered }

    [SerializeField] private Finish _finish;
    internal Finish Surface
    {
        set { _finish = value; SetVerticesDirty(); }
    }

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        Rect box = GetPixelAdjustedRect();
        if (box.width <= 0 || box.height <= 0) return;
        var (rimLight, rimDark, faceLight, faceDark) = _finish switch
        {
            Finish.Bronze => (new Color32(255, 209, 88, 255), new Color32(204, 106, 15, 255),
                new Color32(109, 49, 16, 255), new Color32(41, 20, 11, 255)),
            Finish.Hammered => (new Color32(255, 219, 143, 255), new Color32(194, 82, 13, 255),
                new Color32(182, 93, 26, 255), new Color32(101, 40, 11, 255)),
            Finish.Gold => (new Color32(255, 238, 169, 255), new Color32(189, 87, 15, 255),
                new Color32(255, 186, 57, 255), new Color32(217, 112, 22, 255)),
            Finish.Recess => (new Color32(213, 161, 96, 255), new Color32(89, 58, 38, 255),
                new Color32(7, 15, 24, 255), new Color32(18, 34, 48, 255)),
            _ => (new Color32(226, 178, 93, 255), new Color32(119, 73, 34, 255),
                new Color32(29, 53, 71, 255), new Color32(12, 26, 39, 255))
        };
        float rim = Mathf.Min(2f, Mathf.Min(box.width, box.height) * .22f);
        float cut = Mathf.Min(2.5f, box.height * .23f);
        AddRing(mesh, box, cut, rimLight, rimDark);
        var inset = Rect.MinMaxRect(box.xMin + rim, box.yMin + rim, box.xMax - rim, box.yMax - rim);
        AddRing(mesh, inset, Mathf.Max(0, cut - rim), faceLight, faceDark);
        for (int i = 0; i < 8; i++)
        {
            int next = (i + 1) % 8;
            mesh.AddTriangle(i, next, next + 8);
            mesh.AddTriangle(i, next + 8, i + 8);
        }
        for (int i = 9; i < 15; i++) mesh.AddTriangle(8, i, i + 1);
        if (_finish == Finish.Hammered && inset.width > 13 && inset.height > 13)
            AddHammeredFace(mesh, inset);
    }

    private void AddHammeredFace(VertexHelper mesh, Rect box)
    {
        int columns = Mathf.CeilToInt(box.width / 12f);
        int rows = Mathf.CeilToInt(box.height / 10f);
        float width = box.width / columns, height = box.height / rows;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            float left = box.xMin + x * width, bottom = box.yMin + y * height;
            int index = mesh.currentVertCount;
            float offset = ((x * 7 + y * 13) % 5) * .09f;
            var shade = new Color(.29f, .13f, .03f, .18f + offset * .3f) * color;
            var light = new Color(1f, .62f, .24f, .12f + offset * .45f) * color;
            mesh.AddVert(new Vector2(left, bottom + height), light, Vector2.zero);
            mesh.AddVert(new Vector2(left + width, bottom + height), light, Vector2.zero);
            mesh.AddVert(new Vector2(left + width, bottom), shade, Vector2.zero);
            mesh.AddVert(new Vector2(left, bottom), shade, Vector2.zero);
            mesh.AddVert(new Vector2(left + width * (.32f + offset), bottom + height * .58f), light, Vector2.zero);
            for (int i = 0; i < 4; i++) mesh.AddTriangle(index + i, index + (i + 1) % 4, index + 4);
        }
    }

    private void AddRing(VertexHelper mesh, Rect box, float cut, Color top, Color bottom)
    {
        cut = Mathf.Min(cut, Mathf.Min(box.width, box.height) * .5f);
        for (int i = 0; i < 8; i++)
        {
            Vector2 corner = i switch
            {
                0 => new(box.xMin + cut, box.yMin),
                1 => new(box.xMin, box.yMin + cut),
                2 => new(box.xMin, box.yMax - cut),
                3 => new(box.xMin + cut, box.yMax),
                4 => new(box.xMax - cut, box.yMax),
                5 => new(box.xMax, box.yMax - cut),
                6 => new(box.xMax, box.yMin + cut),
                _ => new(box.xMax - cut, box.yMin)
            };
            mesh.AddVert(corner, Color.Lerp(bottom, top, (corner.y - box.yMin) / box.height) * color, Vector2.zero);
        }
    }
}
