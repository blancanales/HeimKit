using UnityEngine;
using UnityEngine.UI;

namespace HeimKit.Client;

public sealed class ControlPanelSelectionGlow : MaskableGraphic
{
    internal bool ScopeTab;
    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        Rect box = GetPixelAdjustedRect();
        if (box.width < 20 || box.height < 20) return;

        // Keep the bright core on the sprite rim. The wider orange falloff
        // lights the face of the button instead of forming a second border.
        if (ScopeTab)
        {
            // The wide tab artwork has a thicker rim and longer chamfers.
            box = Rect.MinMaxRect(box.xMin + .75f, box.yMin + .25f, box.xMax - .75f, box.yMax - .25f);
            AddContour(mesh, box, .75f, new Color(1f, .39f, .04f, 0f), 6.25f);
            AddContour(mesh, box, -.4f, new Color(1f, .46f, .05f, .33f), 6.25f);
            AddContour(mesh, box, -1.5f, new Color(1f, .79f, .28f, .95f), 6.25f);
            AddContour(mesh, box, -2.8f, new Color(1f, .43f, .06f, .49f), 6.25f);
            AddContour(mesh, box, -4.4f, new Color(1f, .33f, .03f, .16f), 6.25f);
            AddContour(mesh, box, -6.5f, new Color(1f, .26f, .02f, 0f), 6.25f);
        }
        else
        {
            AddContour(mesh, box, 1f, new Color(1f, .39f, .04f, 0f));
            AddContour(mesh, box, -.25f, new Color(1f, .46f, .05f, .28f));
            AddContour(mesh, box, -1.5f, new Color(1f, .79f, .28f, .95f));
            AddContour(mesh, box, -3.2f, new Color(1f, .43f, .06f, .54f));
            AddContour(mesh, box, -6f, new Color(1f, .33f, .03f, .20f));
            AddContour(mesh, box, -9f, new Color(1f, .26f, .02f, 0f));
        }

        for (int ring = 0; ring < 5; ring++)
        {
            int start = ring * 8;
            for (int i = 0; i < 8; i++)
            {
                int next = start + (i + 1) % 8;
                mesh.AddTriangle(start + i, next, next + 8);
                mesh.AddTriangle(start + i, next + 8, start + i + 8);
            }
        }
    }

    private static void AddContour(VertexHelper mesh, Rect box, float offset, Color tint, float cornerCut = 5f)
    {
        float left = box.xMin - offset, right = box.xMax + offset;
        float bottom = box.yMin - offset, top = box.yMax + offset;
        float cut = Mathf.Max(0, cornerCut + offset * .6f);
        mesh.AddVert(new Vector2(left + cut, bottom), tint, Vector2.zero);
        mesh.AddVert(new Vector2(left, bottom + cut), tint, Vector2.zero);
        mesh.AddVert(new Vector2(left, top - cut), tint, Vector2.zero);
        mesh.AddVert(new Vector2(left + cut, top), tint, Vector2.zero);
        mesh.AddVert(new Vector2(right - cut, top), tint, Vector2.zero);
        mesh.AddVert(new Vector2(right, top - cut), tint, Vector2.zero);
        mesh.AddVert(new Vector2(right, bottom + cut), tint, Vector2.zero);
        mesh.AddVert(new Vector2(right - cut, bottom), tint, Vector2.zero);
    }
}
