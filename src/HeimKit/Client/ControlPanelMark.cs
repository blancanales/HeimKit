using UnityEngine;
using UnityEngine.UI;

namespace HeimKit.Client;

public sealed class ControlPanelMark : MaskableGraphic
{
    [SerializeField] internal bool IsCheckmark;

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        Rect box = GetPixelAdjustedRect();
        Vector2 center = box.center;
        float radius = Mathf.Min(box.width, box.height) * .5f;
        var gold = new Color(1f, .78f, .32f);
        if (IsCheckmark)
        {
            var start = center + new Vector2(-.78f, -.02f) * radius;
            var joint = center + new Vector2(-.24f, -.55f) * radius;
            var end = center + new Vector2(.83f, .56f) * radius;
            AddStroke(mesh, start, joint, 2.9f, gold * color);
            AddStroke(mesh, joint, end, 2.9f, gold * color);
            return;
        }

        var tip = center + Vector2.up * radius;
        var right = center + Vector2.right * radius * .79f;
        var bottom = center + Vector2.down * radius;
        var left = center + Vector2.left * radius * .79f;
        var outline = new Color(.36f, .17f, .04f) * color;
        mesh.AddVert(tip, outline, Vector2.zero);
        mesh.AddVert(right, outline, Vector2.zero);
        mesh.AddVert(bottom, outline, Vector2.zero);
        mesh.AddVert(left, outline, Vector2.zero);
        mesh.AddTriangle(0, 1, 2);
        mesh.AddTriangle(0, 2, 3);

        mesh.AddVert(Vector2.Lerp(tip, center, .12f), new Color(1f, .93f, .65f) * color, Vector2.zero);
        mesh.AddVert(Vector2.Lerp(right, center, .12f), new Color(.77f, .39f, .08f) * color, Vector2.zero);
        mesh.AddVert(Vector2.Lerp(bottom, center, .12f), new Color(.67f, .31f, .05f) * color, Vector2.zero);
        mesh.AddVert(Vector2.Lerp(left, center, .12f), gold * color, Vector2.zero);
        mesh.AddVert(center + Vector2.up * radius * .12f, new Color(1f, .64f, .13f) * color, Vector2.zero);
        for (int i = 0; i < 4; i++) mesh.AddTriangle(4 + i, 4 + (i + 1) % 4, 8);
    }

    private static void AddStroke(VertexHelper mesh, Vector2 start, Vector2 end, float width, Color tint)
    {
        Vector2 slope = (end - start).normalized;
        var offset = new Vector2(-slope.y, slope.x) * (width * .5f);
        int i = mesh.currentVertCount;
        mesh.AddVert(start + offset, tint, Vector2.zero);
        mesh.AddVert(end + offset, tint, Vector2.zero);
        mesh.AddVert(end - offset, tint, Vector2.zero);
        mesh.AddVert(start - offset, tint, Vector2.zero);
        mesh.AddTriangle(i, i + 1, i + 2);
        mesh.AddTriangle(i, i + 2, i + 3);
    }
}
