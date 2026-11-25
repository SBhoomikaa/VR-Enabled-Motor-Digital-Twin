
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

        var p = profiles.FirstOrDefault(x => x.code == code)
                ?? new Profile();

        // FULL hierarchy: Assembly is included.
        var faulty = RenderersOf(faultMap[code]);
        var hidden = RenderersOf(p.hide);

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

        // Fault highlighting and hiding still affect the FULL hierarchy.
        foreach (var r in all)
        {
            bool isFaulty = faulty.Contains(r);

            r.enabled = isFaulty || !hidden.Contains(r);

            r.SetPropertyBlock(isFaulty ? mpb : null);
        }

        // ------------------------------------------------
        // CAMERA FRAMING
        // ------------------------------------------------

        // Resolve profile frame names using the original motor
        // hierarchy only. Assembly names are not considered here.
        HashSet<Renderer> framed;

        if (p.frame != null && p.frame.Length > 0)
        {
            framed = FramingRenderersOf(p.frame);
        }
        else
        {
            // If no specific frame parts are provided,
            // use faulty parts that belong to the original motor.
            framed = new HashSet<Renderer>(
                faulty.Where(r => framingSet.Contains(r))
            );
        }

        // If no suitable fault parts exist in the original motor,
        // frame all visible original motor renderers.
        if (framed.Count == 0)
        {
            framed = new HashSet<Renderer>(
                framingRenderers.Where(r => r != null && r.enabled)
            );
        }

        // Last fallback: all original motor renderers.
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

        // Calculate bounds using ONLY original motor renderers.
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

    void OnGUI()
    {
        if (!hasT)
            return;

        var e = Event.current;

        if (e.type == EventType.MouseDrag)
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
        else if (e.type == EventType.ScrollWheel)
        {
            dist = Mathf.Clamp(
                dist * (1f + e.delta.y * zoomSpeed),
                baseDist * 0.4f,
                baseDist * 2.5f
            );
        }
        else if (
            (e.type == EventType.KeyDown && e.keyCode == KeyCode.R) ||
            (e.type == EventType.MouseDown && e.clickCount == 2)
        )
        {
            ResetView();
        }
    }
}
