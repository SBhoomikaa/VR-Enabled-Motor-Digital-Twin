using UnityEngine;

// Builds a simple factory backdrop around the motor at runtime.
// Attach to an empty GameObject, assign the motor root. Optionally assign an HDRI skybox material.
public class FactoryEnvironment : MonoBehaviour
{
    public Transform motor;
    public Material skybox;          // optional HDRI (Skybox/Panoramic) material
    public float roomSize = 14f;     // keep larger than your camera orbit radius
    public float wallHeight = 6f;
    public bool buildWalls = false;  // off so the skybox is visible

    static readonly Color Concrete = new Color(0.35f, 0.36f, 0.38f);
    static readonly Color Wall      = new Color(0.45f, 0.47f, 0.50f);
    static readonly Color Steel     = new Color(0.25f, 0.27f, 0.30f);
    static readonly Color Crate     = new Color(0.40f, 0.33f, 0.22f);

    void Start()
    {
        Bounds b = GetBounds(motor);
        float baseH = 0.15f;
        float floorY = b.min.y - baseH;
        Vector3 c = b.center;
        float h = roomSize * 0.5f;

        if (skybox)
        {
            RenderSettings.skybox = skybox;
            DynamicGI.UpdateEnvironment();
        }

        // Floor and motor base plate
        Make(PrimitiveType.Cube, "Floor", new Vector3(c.x, floorY - 0.05f, c.z),
             new Vector3(roomSize, 0.1f, roomSize), Concrete, 0f, 0.3f);
        Make(PrimitiveType.Cube, "BasePlate", new Vector3(c.x, b.min.y - baseH * 0.5f, c.z),
             new Vector3(b.size.x * 1.4f, baseH, b.size.z * 1.4f), Steel, 0.8f, 0.5f);

        // Walls (back, left, right)
        float wy = floorY + wallHeight * 0.5f;
        if (buildWalls)
        {
            Make(PrimitiveType.Cube, "WallBack",  new Vector3(c.x, wy, c.z + h), new Vector3(roomSize, wallHeight, 0.2f), Wall, 0f, 0.1f);
            Make(PrimitiveType.Cube, "WallLeft",  new Vector3(c.x - h, wy, c.z), new Vector3(0.2f, wallHeight, roomSize), Wall, 0f, 0.1f);
            Make(PrimitiveType.Cube, "WallRight", new Vector3(c.x + h, wy, c.z), new Vector3(0.2f, wallHeight, roomSize), Wall, 0f, 0.1f);
        }

        // Pipes along the back wall
        for (int i = 0; i < 2; i++)
        {
            var pipe = Make(PrimitiveType.Cylinder, "Pipe" + i,
                new Vector3(c.x, floorY + 3.5f + i * 0.6f, c.z + h - 0.4f),
                new Vector3(0.25f, h, 0.25f), Steel, 0.9f, 0.6f);
            pipe.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
        }

        // Crates
        Vector3[] crates = {
            new Vector3(-4f, 0.5f, 4.5f), new Vector3(-3f, 0.5f, 5.2f), new Vector3(4.5f, 0.5f, 4f)
        };
        foreach (var p in crates)
            Make(PrimitiveType.Cube, "Crate", new Vector3(c.x + p.x, floorY + p.y, c.z + p.z),
                 Vector3.one, Crate, 0f, 0.2f);
    }

    GameObject Make(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Color color, float metallic, float smooth)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(transform, false);
        go.transform.position = pos;
        go.transform.localScale = scale;
        Destroy(go.GetComponent<Collider>());

        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh) { color = color };
        m.SetFloat("_Metallic", metallic);
        m.SetFloat("_Smoothness", smooth);   // URP
        m.SetFloat("_Glossiness", smooth);   // Built-in
        go.GetComponent<Renderer>().sharedMaterial = m;
        return go;
    }

    static Bounds GetBounds(Transform root)
    {
        var rs = root.GetComponentsInChildren<Renderer>();
        Bounds b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return b;
    }
}