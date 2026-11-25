using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class FaultVisualizer : MonoBehaviour
{
    public enum Fault
    {
        H_H, R_U, R_M, B_R, K_A, S_W, V_U, F_B_DE, F_B_NDE, F_B
    }

    [System.Serializable]
    public class Profile
    {
        public string code;
        public string[] hide = new string[0];
        public string[] frame = new string[0];
        public Vector2 pitchYaw = new Vector2(20, 35);
    }

    static Profile P(string code, float pitch, float yaw,
                     string[] hide, string[] frame = null)
    {
        return new Profile
        {
            code = code,
            pitchYaw = new Vector2(pitch, yaw),
            hide = hide,
            frame = frame ?? new string[0]
        };
    }

    static readonly string[] Shell =
        { "Frame_Housing", "Stator_Core", "Fan_Cover" };

    static readonly string[] ShellEnds =
        { "Frame_Housing", "EndShield_DE", "EndShield_NDE",
          "Stator_Core", "Fan_Cover" };

    static readonly string[] BearingsOpen =
        { "Frame_Housing", "EndShield_DE", "EndShield_NDE",
          "Fan_Cover", "Fan" };

    static readonly string[] None = new string[0];

    [Header("Setup")]
    public TextAsset json;
    public Camera cam;

    [Header("Fault source")]
    public bool useDropdown = false;
    public Fault fault;

    [Header("Per-fault view")]
    public bool useCodeDefaults = true;
    public Profile[] profiles;

    static Profile[] Defaults() => new[]
    {
        P("H-H",     20,  35, None),
        P("V-U",     25,  35, None,
            new[] { "Frame_Housing", "Terminal_Box" }),
        P("S-W",     20,  35, Shell),
        P("R-U",     15,  20, Shell,
            new[] { "Rotor_Core", "Rotor_End_Ring_DE",
                    "Rotor_End_Ring_NDE" }),
        P("B-R",     15,  20, ShellEnds,
            new[] { "Shaft", "Rotor_Core" }),
        P("K-A",     15,  20, Shell,
            new[] { "Rotor_Core", "Rotor_End_Ring_DE",
                    "Rotor_End_Ring_NDE" }),
        P("R-M",     15,  60, ShellEnds,
            new[] { "Shaft", "Coupling_Hub" }),
        P("F-B",     15,  20, BearingsOpen),
        P("F-B_DE",  15,  70,
            new[] { "Frame_Housing", "EndShield_DE" }),
        P("F-B_NDE", 15, -70,
            new[] { "Frame_Housing", "EndShield_NDE",
                    "Fan_Cover", "Fan" })
    };

    [Header("Camera")]
    public float padding = 1.4f;
    public float minDistance = 0.4f;
    public float smooth = 4f;
    public float heightAboveFloor = 0.05f;

    [Header("Interaction")]
    public float orbitSpeed = 0.3f;
    public float zoomSpeed = 0.1f;
    public float minPitch = 10f;
    public float maxPitch = 80f;

    [Header("Hide menu")]
    public float menuButtonSize = 70f;
    public bool lockFaultParts = false;

    Vector3 center, baseCenter;
    float yaw, pitch, dist, baseYaw, basePitch, baseDist, panLimit;

    Dictionary<string, string[]> faultMap;
    Dictionary<string, string> colors;

    // Used for fault detection, hiding and highlighting.
    readonly Dictionary<string, Transform> byName =
        new Dictionary<string, Transform>();

    // Used ONLY for camera framing.
    // This dictionary excludes the Assembly branch.
    readonly Dictionary<string, Transform> framingByName =
        new Dictionary<string, Transform>();

    Transform assemblyRoot;

    Renderer[] all;
    Renderer[] framingRenderers;

    HashSet<Renderer> framingSet;

    string shownCode;

    Vector3 tPos;
    Quaternion tRot;
    bool hasT;

    // Hide menu state.
    readonly List<Renderer> userHidden = new List<Renderer>();
    HashSet<Renderer> faultyParts = new HashSet<Renderer>();
    bool menuOpen;
    Vector2 menuScroll, downPos;
    bool downOnUI;
    Rect buttonRect, menuRect;
    float rowH;
    GUIStyle btnStyle, itemStyle, titleStyle;

    public string ShownCode => shownCode;
    public Camera ViewCamera => cam;
    public Color FaultColor { get; private set; } = Color.white;
    public List<Renderer[]> AnchorGroups { get; } =
        new List<Renderer[]>();

    public Vector3 BaseCenter => baseCenter;
    public float BaseYaw => baseYaw;
    public float BasePitch => basePitch;
    public float BaseDistance => baseDist;
    public float FrameRadius => panLimit;

    static string Code(Fault f)
    {
        switch (f)
        {
            case Fault.F_B_DE:  return "F-B_DE";
            case Fault.F_B_NDE: return "F-B_NDE";
            default: return f.ToString().Replace("_", "-");
        }
    }

    string WantedCode()
    {
        var state = MotorState.Instance;

        if (!useDropdown && state != null && state.HasFault)
            return state.fault;

        return Code(fault);
    }

    void Start()
    {
        if (!cam)
            cam = Camera.main;

        if (!cam)
        {
            Debug.LogError("FaultVisualizer: No camera assigned or found.");
            return;
        }

        if (!json)
        {
            Debug.LogError("FaultVisualizer: JSON file is not assigned.");
            return;
        }

        if (useCodeDefaults || profiles == null || profiles.Length == 0)
            profiles = Defaults();

        cam.nearClipPlane = 0.02f;

        // Load fault data.
        var j = JObject.Parse(json.text);

        faultMap =
            j["fault_map"].ToObject<Dictionary<string, string[]>>();

        colors =
            j["colors"].ToObject<Dictionary<string, string>>();

        // Combine both bearing faults if required.
        if (!faultMap.ContainsKey("F-B")
            && faultMap.TryGetValue("F-B_DE", out var de)
            && faultMap.TryGetValue("F-B_NDE", out var nde))
        {
            faultMap["F-B"] = de.Union(nde).ToArray();
        }

        // Find the Assembly child.
        // It is excluded from camera framing only.
        assemblyRoot = null;

        foreach (Transform child in transform)
        {
            if (child.name == "Assembly")
            {
                assemblyRoot = child;
                break;
            }
        }

        if (assemblyRoot != null)
        {
            Debug.Log(
                "FaultVisualizer: Found Assembly. " +
                "It will be excluded from camera framing."
            );
        }
        else
        {
            Debug.LogWarning(
                "FaultVisualizer: Could not find a direct child named " +
                "'Assembly'. All renderers will be considered for framing."
            );
        }

        // Build the full hierarchy name dictionary.
        // This includes Assembly for fault detection/highlighting.
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (!byName.ContainsKey(t.name))
                byName[t.name] = t;
        }

        // Build a separate dictionary for camera framing.
        // Everything inside Assembly is excluded.
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (IsInsideAssembly(t))
                continue;

            if (!framingByName.ContainsKey(t.name))
                framingByName[t.name] = t;
        }

        // All renderers, including Assembly.
        all = GetComponentsInChildren<Renderer>(true);

        // Add colliders so parts can be clicked.
        foreach (var r in all)
        {
            var mf = r.GetComponent<MeshFilter>();

            if (mf && mf.sharedMesh && !r.GetComponent<Collider>())
                r.gameObject.AddComponent<MeshCollider>();
        }

        // Camera framing renderers, excluding Assembly.
        framingRenderers = all
            .Where(r => r != null && !IsInsideAssembly(r.transform))
            .ToArray();

        framingSet = new HashSet<Renderer>(framingRenderers);

        shownCode = WantedCode();

        Apply(shownCode);

        UpdateTarget();

        if (hasT)
            cam.transform.SetPositionAndRotation(tPos, tRot);
    }

    bool IsInsideAssembly(Transform t)
    {
        if (assemblyRoot == null || t == null)
            return false;

        return t == assemblyRoot || t.IsChildOf(assemblyRoot);
    }

    void Update()
    {
        if (faultMap == null)
            return;

        string wanted = WantedCode();

        if (wanted == shownCode)
            return;

        shownCode = wanted;
        Apply(wanted);
    }

    public void Apply(string code)
    {
        if (!faultMap.ContainsKey(code))
        {
            Debug.LogWarning(
                "FaultVisualizer: Unknown fault code '" + code + "'"
            );
            return;
        }

        // New fault = new default view. Clear the user's choices.
        userHidden.Clear();

        var p = profiles.FirstOrDefault(x => x.code == code)
                ?? new Profile();

        // FULL hierarchy: Assembly is included.
        var faulty = RenderersOf(faultMap[code]);
        var hidden = RenderersOf(p.hide);

        faultyParts = faulty;

        Color col = Color.white;

        colors.TryGetValue(code.Split('_')[0], out var hex);

        if (hex != null)
            ColorUtility.TryParseHtmlString(hex, out col);

        var mpb = new MaterialPropertyBlock();
        mpb.SetColor("_BaseColor", col);

        FaultColor = col;

        AnchorGroups.Clear();

        if (code == "F-B"
            && faultMap.ContainsKey("F-B_DE")
            && faultMap.ContainsKey("F-B_NDE"))
        {
            AnchorGroups.Add(
                RenderersOf(faultMap["F-B_DE"]).ToArray()
            );

            AnchorGroups.Add(
                RenderersOf(faultMap["F-B_NDE"]).ToArray()
            );
        }
        else if (faulty.Count > 0)
        {
            AnchorGroups.Add(faulty.ToArray());
        }

        // Fault highlighting and default hiding.
        foreach (var r in all)
        {
            bool isFaulty = faulty.Contains(r);

            r.enabled = isFaulty || !hidden.Contains(r);

            r.SetPropertyBlock(isFaulty ? mpb : null);
        }

        // ------------------------------------------------
        // CAMERA FRAMING
        // ------------------------------------------------

        HashSet<Renderer> framed;

        if (p.frame != null && p.frame.Length > 0)
        {
            framed = FramingRenderersOf(p.frame);
        }
        else
        {
            framed = new HashSet<Renderer>(
                faulty.Where(r => framingSet.Contains(r))
            );
        }

        if (framed.Count == 0)
        {
            framed = new HashSet<Renderer>(
                framingRenderers.Where(r => r != null && r.enabled)
            );
        }

        if (framed.Count == 0)
        {
            framed = new HashSet<Renderer>(
                framingRenderers.Where(r => r != null)
            );
        }

        if (framed.Count == 0)
        {
            Debug.LogWarning(
                "FaultVisualizer: No renderers available for camera framing."
            );
            hasT = false;
            return;
        }

        var b = framed.First().bounds;

        foreach (var r in framed)
            b.Encapsulate(r.bounds);

        Frame(b, p.pitchYaw);
    }

    // Used by fault detection/highlighting.
    // Includes Assembly.
    HashSet<Renderer> RenderersOf(string[] names)
    {
        var set = new HashSet<Renderer>();

        foreach (var n in names)
        {
            if (byName.TryGetValue(n, out var t))
            {
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                    set.Add(r);
            }
        }

        return set;
    }

    // Used only by camera framing.
    // Resolves names exclusively outside Assembly.
    HashSet<Renderer> FramingRenderersOf(string[] names)
    {
        var set = new HashSet<Renderer>();

        foreach (var n in names)
        {
            if (framingByName.TryGetValue(n, out var t))
            {
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (r != null && framingSet.Contains(r))
                        set.Add(r);
                }
            }
        }

        return set;
    }

    void Frame(Bounds b, Vector2 pitchYaw)
    {
        float d =
            b.extents.magnitude * padding /
            Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

        baseDist = Mathf.Max(d, minDistance);

        basePitch = pitchYaw.x;
        baseYaw = pitchYaw.y;

        baseCenter = b.center;

        panLimit = b.extents.magnitude;

        ResetView();

        hasT = true;
    }

    void ResetView()
    {
        center = baseCenter;
        yaw = baseYaw;
        pitch = basePitch;
        dist = baseDist;
    }

    void UpdateTarget()
    {
        tRot = Quaternion.Euler(pitch, yaw, 0);

        tPos = center - tRot * Vector3.forward * dist;

        var scene = MotorScene.Instance;

        if (scene != null)
        {
            tPos.y = Mathf.Max(
                tPos.y,
                scene.FloorTopY + heightAboveFloor
            );
        }
    }

    void LateUpdate()
    {
        if (!hasT || !cam)
            return;

        UpdateTarget();

        float t = 1f - Mathf.Exp(-smooth * Time.deltaTime);

        cam.transform.SetPositionAndRotation(
            Vector3.Lerp(cam.transform.position, tPos, t),
            Quaternion.Slerp(cam.transform.rotation, tRot, t)
        );
    }

    // ------------------------------------------------
    // INPUT + HIDE MENU
    // ------------------------------------------------

    void OnGUI()
    {
        if (!hasT)
            return;

        InitStyles();
        ComputeRects();

        var e = Event.current;

        bool overUI = buttonRect.Contains(e.mousePosition)
                   || (menuOpen && menuRect.Contains(e.mousePosition));

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            downPos = e.mousePosition;
            downOnUI = overUI;
        }

        if (e.type == EventType.MouseDrag && !downOnUI)
        {
            if (e.button == 0)
            {
                yaw += e.delta.x * orbitSpeed;

                pitch = Mathf.Clamp(
                    pitch + e.delta.y * orbitSpeed,
                    minPitch,
                    maxPitch
                );
            }
            else
            {
                center += (
                    tRot * (
                        Vector3.left * e.delta.x +
                        Vector3.up * e.delta.y
                    )
                ) * dist * 0.002f;

                center = baseCenter +
                    Vector3.ClampMagnitude(
                        center - baseCenter,
                        panLimit
                    );
            }
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            // A click = tiny movement, not on the menu, not a double click.
            if (!downOnUI && !overUI && e.clickCount < 2
                && (e.mousePosition - downPos).sqrMagnitude < 25f)
            {
                TryHideAt(e.mousePosition);
            }
        }
        else if (e.type == EventType.ScrollWheel && !overUI)
        {
            dist = Mathf.Clamp(
                dist * (1f + e.delta.y * zoomSpeed),
                baseDist * 0.4f,
                baseDist * 2.5f
            );
        }
        else if (
            (e.type == EventType.KeyDown && e.keyCode == KeyCode.R) ||
            (e.type == EventType.MouseDown && e.clickCount == 2 && !overUI)
        )
        {
            ResetView();
        }

        DrawHideMenu();
    }

    void InitStyles()
    {
        if (btnStyle != null) return;

        btnStyle = new GUIStyle(GUI.skin.button)
        {
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        itemStyle = new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleLeft
        };

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
    }

    void ComputeRects()
    {
        float s = Mathf.Max(menuButtonSize, Screen.height * 0.06f);
        float m = 10f;
        rowH = s * 0.55f;

        btnStyle.fontSize = (int)(s * 0.45f);
        itemStyle.fontSize = (int)(rowH * 0.5f);
        titleStyle.fontSize = (int)(rowH * 0.5f);

        // Bottom-right corner.
        buttonRect = new Rect(Screen.width - s - m,
                              Screen.height - s - m, s, s);

        float w = Mathf.Max(260f, s * 4f);
        int rows = Mathf.Max(userHidden.Count, 1);
        float h = Mathf.Min(rowH * (rows + 2), Screen.height * 0.5f);

        // Menu opens upward, above the button.
        menuRect = new Rect(Screen.width - w - m,
                            buttonRect.y - 6f - h, w, h);
    }

    void DrawHideMenu()
    {
        var old = GUI.backgroundColor;
        GUI.backgroundColor = Color.red;

        string label = userHidden.Count > 0 ? "H " + userHidden.Count : "H";

        if (GUI.Button(buttonRect, label, btnStyle))
            menuOpen = !menuOpen;

        GUI.backgroundColor = old;

        if (!menuOpen) return;

        GUI.Box(menuRect, GUIContent.none);

        GUI.Label(
            new Rect(menuRect.x + 8, menuRect.y, menuRect.width - 16, rowH),
            "Hidden parts", titleStyle);

        var listRect = new Rect(
            menuRect.x, menuRect.y + rowH,
            menuRect.width, menuRect.height - 2 * rowH);

        if (userHidden.Count == 0)
        {
            GUI.Label(
                new Rect(listRect.x + 8, listRect.y,
                         listRect.width - 16, rowH),
                "Nothing hidden. Click a part to hide it.", titleStyle);
            return;
        }

        Renderer toRestore = null;

        var view = new Rect(0, 0, listRect.width - 18,
                            userHidden.Count * rowH);

        menuScroll = GUI.BeginScrollView(listRect, menuScroll, view);

        for (int i = 0; i < userHidden.Count; i++)
        {
            var r = userHidden[i];
            string n = r ? r.gameObject.name : "(missing)";

            if (GUI.Button(new Rect(0, i * rowH, view.width, rowH),
                           n, itemStyle))
                toRestore = r;
        }

        GUI.EndScrollView();

        if (GUI.Button(
                new Rect(menuRect.x, menuRect.yMax - rowH,
                         menuRect.width, rowH),
                "Show all", itemStyle))
        {
            foreach (var r in userHidden.ToArray())
                Restore(r);
        }

        if (toRestore != null)
            Restore(toRestore);
    }

    void TryHideAt(Vector2 guiPos)
    {
        var ray = cam.ScreenPointToRay(
            new Vector3(guiPos.x, Screen.height - guiPos.y));

        var hits = Physics.RaycastAll(ray, 1000f);

        System.Array.Sort(hits,
            (a, b) => a.distance.CompareTo(b.distance));

        foreach (var h in hits)
        {
            var r = h.collider.GetComponent<Renderer>();

            // Skip parts that are already hidden (by default or by user).
            // Their colliders stay active, so we look past them.
            if (!r || !r.enabled)
                continue;

            // Optional: fault parts cannot be hidden.
            if (lockFaultParts && faultyParts.Contains(r))
                return;

            r.enabled = false;
            userHidden.Add(r);
            return;
        }
    }

    void Restore(Renderer r)
    {
        userHidden.Remove(r);

        // Only parts that were visible can be in this list,
        // so it is always safe to show them again.
        if (r)
            r.enabled = true;
    }
}