using UnityEngine;

// Plant-room around the motor + pump assembly, built at runtime.
// Attach to an empty GameObject at the origin. Assign Motor (and optionally the 3 marker transforms).
[DefaultExecutionOrder(200)]   // run after MotorScene has built its floor
public class FactoryEnvironment : MonoBehaviour
{
    [Header("Setup")]
    public Transform motor;            // induction_motor root
    public Material skybox;            // optional HDRI
    public float floorGap = 0.15f;
    [Tooltip("Rotate the assembly around Y at start. Try 0/90/180/270 until the fan faces the side wall.")]
    public float motorYaw = 0f;

    [Header("Optional markers (empty GameObjects placed on the model)")]
    public Transform topPipeEnd;       // centre of the open end of the model's top horizontal pipe
    public Transform inletPipeEnd;     // centre of the open end of the model's inlet pipe
    public Transform motorTerminal;    // motor terminal box -> wires end here; also decides the fan-side wall
    public float topPipeRadius = 0.14f;
    public float inletPipeRadius = 0.12f;

    [Header("Room")]
    public bool sideWallOnRight = true;  // wall the motor FAN faces (auto-set if motorTerminal is assigned)
    public float wallHeight = 6f;
    public float sideGap = 3f;           // assembly -> fan-side wall
    public float farGap = 3.5f;          // assembly -> opposite wall
    public float backGap = 2.5f;
    public float frontGap = 4f;          // open side, just sets floor depth
    public float blockSize = 0.8f;

    [Header("Details")]
    public bool wallPipes = true;
    public bool controlBox = true;
    public bool floorStripes = true;
    public float stripeMargin = 1.2f;

    static readonly Color FloorColor  = new Color(0.17f, 0.17f, 0.19f);
    static readonly Color Yellow      = new Color(0.95f, 0.74f, 0.05f);
    static readonly Color BlockCream  = new Color(0.82f, 0.80f, 0.72f);
    static readonly Color BlockMortar = new Color(0.62f, 0.60f, 0.54f);
    static readonly Color Baseboard   = new Color(0.14f, 0.15f, 0.17f);
    static readonly Color PipeWhite   = new Color(0.90f, 0.91f, 0.92f);
    static readonly Color PipeModel   = new Color(0.42f, 0.52f, 0.62f);  // blue-grey, matches model pipes
    static readonly Color FlangeBlue  = new Color(0.12f, 0.35f, 0.70f);
    static readonly Color BracketDark = new Color(0.20f, 0.21f, 0.23f);
    static readonly Color BoxBeige    = new Color(0.62f, 0.61f, 0.55f);
    static readonly Color ScreenDark  = new Color(0.08f, 0.12f, 0.18f);
    static readonly Color ConduitGrey = new Color(0.55f, 0.57f, 0.60f);

    float xMin, xMax, zBack, zFront, groundY, sign, centerX;

    void Start()
    {
        if (motor == null) { Debug.LogError("FactoryEnvironment: assign the Motor field."); return; }

        if (motorYaw != 0f)
            motor.RotateAround(GetBounds(motor).center, Vector3.up, motorYaw);

        Bounds b = GetBounds(motor);
        centerX = b.center.x;
        if (motorTerminal) sideWallOnRight = motorTerminal.position.x > centerX;  // fan-side wall = motor's side

        float floorY = b.min.y - floorGap;
        groundY = floorY;
        var scene = MotorScene.Instance;
        if (scene != null && scene.FloorTopY > floorY) groundY = scene.FloorTopY;

        if (skybox) { RenderSettings.skybox = skybox; DynamicGI.UpdateEnvironment(); }

        sign = sideWallOnRight ? 1f : -1f;
        float sideX = sideWallOnRight ? b.max.x + sideGap : b.min.x - sideGap;
        float farX  = sideWallOnRight ? b.min.x - farGap  : b.max.x + farGap;
        xMin = Mathf.Min(sideX, farX);
        xMax = Mathf.Max(sideX, farX);
        zBack = b.max.z + backGap;
        zFront = b.min.z - frontGap;

        // floor fits the room
        Make(PrimitiveType.Cube, "Floor",
             new Vector3((xMin + xMax) * 0.5f, floorY - 0.05f, (zBack + zFront) * 0.5f),
             new Vector3(xMax - xMin, 0.1f, zBack - zFront), FloorColor, 0f, 0.55f);

        if (floorStripes) BuildStripes(b);
        BuildWalls();
        if (wallPipes) BuildPipes();
        if (controlBox) BuildControlBox(b, sideX);
    }

    // ---------- floor lines ----------
    void BuildStripes(Bounds b)
    {
        float m = stripeMargin, w = 0.1f, th = 0.008f;
        float x0 = b.min.x - m, x1 = b.max.x + m, z0 = b.min.z - m, z1 = b.max.z + m;
        float y = groundY + th * 0.5f, cx = (x0 + x1) * 0.5f, cz = (z0 + z1) * 0.5f;
        float lx = x1 - x0 + w, lz = z1 - z0 + w;
        Make(PrimitiveType.Cube, "StripeFront", new Vector3(cx, y, z0), new Vector3(lx, th, w), Yellow, 0f, 0.3f);
        Make(PrimitiveType.Cube, "StripeBack",  new Vector3(cx, y, z1), new Vector3(lx, th, w), Yellow, 0f, 0.3f);
        Make(PrimitiveType.Cube, "StripeLeft",  new Vector3(x0, y, cz), new Vector3(w, th, lz), Yellow, 0f, 0.3f);
        Make(PrimitiveType.Cube, "StripeRight", new Vector3(x1, y, cz), new Vector3(w, th, lz), Yellow, 0f, 0.3f);
    }

    // ---------- walls: back + both sides ----------
    void BuildWalls()
    {
        const float t = 0.2f, bh = 0.15f;
        Texture2D blocks = MakeBlockTexture();
        float wy = groundY + wallHeight * 0.5f;
        float width = xMax - xMin, depth = zBack - zFront, zc = (zBack + zFront) * 0.5f;

        Make(PrimitiveType.Cube, "WallBack", new Vector3((xMin + xMax) * 0.5f, wy, zBack + t * 0.5f),
             new Vector3(width + 2f * t, wallHeight, t), Color.white, 0f, 0.1f, blocks,
             new Vector2(width / blockSize, wallHeight / blockSize));

        foreach (float x in new[] { xMin - t * 0.5f, xMax + t * 0.5f })
            Make(PrimitiveType.Cube, "WallSide", new Vector3(x, wy, zc),
                 new Vector3(t, wallHeight, depth), Color.white, 0f, 0.1f, blocks,
                 new Vector2(depth / blockSize, wallHeight / blockSize));

        Make(PrimitiveType.Cube, "BaseboardBack", new Vector3((xMin + xMax) * 0.5f, groundY + bh * 0.5f, zBack - 0.015f),
             new Vector3(width, bh, 0.03f), Baseboard, 0f, 0.3f);
        Make(PrimitiveType.Cube, "BaseboardL", new Vector3(xMin + 0.015f, groundY + bh * 0.5f, zc),
             new Vector3(0.03f, bh, depth), Baseboard, 0f, 0.3f);
        Make(PrimitiveType.Cube, "BaseboardR", new Vector3(xMax - 0.015f, groundY + bh * 0.5f, zc),
             new Vector3(0.03f, bh, depth), Baseboard, 0f, 0.3f);
    }

    // ---------- pipes ----------
    void BuildPipes()
    {
        float r = 0.14f;
        float pipeY = groundY + wallHeight * 0.82f;
        float pipeZ = zBack - 0.28f;

        // decorative main pipe along the back wall
        Pipe("PipeMain", new Vector3(xMin, pipeY, pipeZ), new Vector3(xMax, pipeY, pipeZ), r, PipeWhite, 0.1f, 0.5f);
        float fx = Mathf.Lerp(xMin, xMax, 0.3f);
        Pipe("PipeFlange", new Vector3(fx - 0.02f, pipeY, pipeZ), new Vector3(fx + 0.02f, pipeY, pipeZ), r + 0.07f, FlangeBlue, 0.3f, 0.5f);
        for (float x = xMin + 0.5f; x < xMax; x += 2f)
            Make(PrimitiveType.Cube, "PipeBracket", new Vector3(x, pipeY, zBack - 0.07f),
                 new Vector3(0.1f, 0.42f, 0.14f), BracketDark, 0.5f, 0.4f);

        // thin grey conduit lower on the back wall
        Pipe("PipeSmall", new Vector3(xMin, groundY + wallHeight * 0.5f, zBack - 0.08f),
             new Vector3(xMax, groundY + wallHeight * 0.5f, zBack - 0.08f), 0.05f, ConduitGrey, 0.8f, 0.5f);

        // extend the model's own open pipe ends into the nearest wall on their side
        if (topPipeEnd)   ExtendToWall("TopPipeExt", topPipeEnd.position, topPipeRadius);
        if (inletPipeEnd) ExtendToWall("InletPipeExt", inletPipeEnd.position, inletPipeRadius);
    }

    void ExtendToWall(string name, Vector3 p, float radius)
    {
        float wallX = p.x > centerX ? xMax : xMin;
        Pipe(name, p, new Vector3(wallX, p.y, p.z), radius, PipeModel, 0.3f, 0.5f);
    }

    // ---------- control box + wires to motor ----------
    void BuildControlBox(Bounds b, float sideX)
    {
        float depth = 0.22f;
        float boxY = groundY + 1.8f, boxZ = zBack - 1.8f;
        float boxX = sideX - sign * depth * 0.5f;
        float faceX = sideX - sign * (depth + 0.005f);

        Make(PrimitiveType.Cube, "ControlBox", new Vector3(boxX, boxY, boxZ), new Vector3(depth, 0.9f, 0.7f), BoxBeige, 0.2f, 0.4f);
        Make(PrimitiveType.Cube, "ControlScreen", new Vector3(faceX, boxY + 0.22f, boxZ), new Vector3(0.012f, 0.2f, 0.28f), ScreenDark, 0f, 0.9f);
        Make(PrimitiveType.Cube, "LightGreen", new Vector3(faceX, boxY - 0.05f, boxZ - 0.15f), new Vector3(0.012f, 0.05f, 0.05f), new Color(0.2f, 0.8f, 0.3f), 0f, 0.8f);
        Make(PrimitiveType.Cube, "LightRed", new Vector3(faceX, boxY - 0.05f, boxZ + 0.15f), new Vector3(0.012f, 0.05f, 0.05f), new Color(0.85f, 0.2f, 0.2f), 0f, 0.8f);

        // conduit up to the ceiling
        Pipe("ConduitUp", new Vector3(boxX, boxY + 0.45f, boxZ), new Vector3(boxX, groundY + wallHeight, boxZ), 0.025f, ConduitGrey, 0.8f, 0.5f);

        // two drooping flexible wires: box bottom -> motor terminal
        Vector3 term = motorTerminal ? motorTerminal.position
            : new Vector3(b.center.x + sign * b.size.x * 0.15f, b.max.y, b.center.z);
        foreach (float dz in new[] { -0.2f, 0.2f })
        {
            Vector3 p0 = new Vector3(boxX, boxY - 0.45f, boxZ + dz);
            Vector3 p3 = term + new Vector3(0f, 0f, dz * 0.25f);
            Vector3 p1 = p0 + new Vector3(0f, -(boxY - groundY) * 0.7f, 0f);
            Vector3 p2 = p3 + new Vector3(sign * 0.8f, 0.3f, 0f);
            Wire("Wire", p0, p1, p2, p3);
        }
    }

    void Wire(string name, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        const int n = 28;
        lr.positionCount = n + 1;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n, u = 1f - t;
            lr.SetPosition(i, u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3);
        }
        lr.startWidth = lr.endWidth = 0.045f;
        lr.numCapVertices = 4;
        lr.numCornerVertices = 4;
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        lr.sharedMaterial = new Material(sh) { color = ConduitGrey };
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // ---------- helpers ----------
    GameObject Pipe(string name, Vector3 a, Vector3 b, float radius, Color col, float met, float sm)
    {
        var go = Make(PrimitiveType.Cylinder, name, (a + b) * 0.5f,
                      new Vector3(radius * 2f, (b - a).magnitude * 0.5f, radius * 2f), col, met, sm);
        go.transform.rotation = Quaternion.FromToRotation(Vector3.up, b - a);
        return go;
    }

    Texture2D MakeBlockTexture()
    {
        const int W = 256, H = 256, cols = 2, rows = 4, mortar = 3;
        int bw = W / cols, bh = H / rows;
        var rng = new System.Random(7);
        float[] shade = new float[cols * rows];
        for (int i = 0; i < shade.Length; i++) shade[i] = 0.96f + (float)rng.NextDouble() * 0.08f;

        var px = new Color32[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool isMortar = (x % bw) < mortar || (y % bh) < mortar;
                float k = isMortar ? 1f : shade[(y / bh) * cols + (x / bw)];
                k *= 1f + ((float)rng.NextDouble() - 0.5f) * 0.03f;
                Color c = isMortar ? BlockMortar : BlockCream;
                px[y * W + x] = new Color(c.r * k, c.g * k, c.b * k, 1f);
            }

        var tex = new Texture2D(W, H, TextureFormat.RGBA32, true);
        tex.SetPixels32(px);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        tex.anisoLevel = 4;
        tex.Apply(true);
        return tex;
    }

    GameObject Make(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Color color,
                    float metallic, float smooth, Texture tex = null, Vector2 tiling = default)
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
        m.SetFloat("_Smoothness", smooth);
        m.SetFloat("_Glossiness", smooth);
        if (tex != null)
        {
            m.mainTexture = tex;
            m.mainTextureScale = tiling == default ? Vector2.one : tiling;
        }
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