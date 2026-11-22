/*using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class FaultVisualizer : MonoBehaviour
{
    public enum Fault { H_H, R_U, R_M, B_R, K_A, S_W, V_U, F_B_DE, F_B_NDE }

    [System.Serializable]
    public class Profile
    {
        public string code;
        public string[] hide = new string[0];    // parts hidden for this fault
        public string[] frame = new string[0];   // camera frames these; empty = colored parts, or whole motor if none
        public Vector2 pitchYaw = new Vector2(20, 35);
    }

    static Profile P(string code, float pitch, float yaw, string[] hide, string[] frame = null)
    {
        return new Profile { code = code, pitchYaw = new Vector2(pitch, yaw), hide = hide, frame = frame ?? new string[0] };
    }

    /*static readonly string[] Stator = { "Frame_Housing", "Stator_Core", "Stator_Winding_U", "Stator_Winding_V", "Stator_Winding_W" };
    static readonly string[] None = new string[0];*/
    /*
    static readonly string[] Shell = { "Frame_Housing", "Stator_Core","Fan_Cover" };
    static readonly string[] Frm   = { "Frame_Housing" };
    static readonly string[] ShellEnds   = { "Frame_Housing" , "EndShield_DE","EndShield_NDE","Stator_Core","Fan_Cover" };
    static readonly string[] None  = new string[0];

    [Header("Setup")]
    public TextAsset json;
    public Camera cam;

    [Header("Fault (change in Play mode)")]
    public Fault fault;

    [Header("Per-fault view (edit here)")]
    public bool useCodeDefaults = true;   // untick to use the Inspector table instead
    public Profile[] profiles;

    static Profile[] Defaults() => new[]
    {
        P("H-H",    20,  35, None),
        P("V-U",    25,  35, None, new[] { "Frame_Housing", "Terminal_Box" }),
        P("S-W",    20,  35, Shell),
        P("R-U",    15,  20, Shell, new[] { "Rotor_Core", "Rotor_End_Ring_DE", "Rotor_End_Ring_NDE" }),
        P("B-R",    15,  20, ShellEnds, new[] { "Shaft", "Rotor_Core" }),
        P("K-A",    15,  20, Shell, new[] { "Rotor_Core", "Rotor_End_Ring_DE", "Rotor_End_Ring_NDE" }),
        P("R-M",    15,  60, ShellEnds, new[] { "Shaft", "Coupling_Hub" }),
        P("F-B_DE", 15,  70, new[] { "Frame_Housing", "EndShield_DE" }),
        P("F-B_NDE",15, -70, new[] { "Frame_Housing", "EndShield_NDE" ,"Fan_Cover","Fan"}),
    };
    [Header("Camera")]
    public float padding = 1.4f;
    public float minDistance = 0.4f;
    public float smooth = 4f;
    [Header("Interaction")]
    public float orbitSpeed = 0.3f;
    public float zoomSpeed = 0.1f;
    public float minPitch = -5f, maxPitch = 80f;

Vector3 center, baseCenter;
float yaw, pitch, dist, baseYaw, basePitch, baseDist, panLimit;
    Dictionary<string, string[]> faultMap;
    Dictionary<string, string> colors;
    readonly Dictionary<string, Transform> byName = new Dictionary<string, Transform>();
    Renderer[] all;
    Fault last;
    Vector3 tPos;
    Quaternion tRot;
    bool hasT;

    static string Code(Fault f)
    {
        switch (f)
        {
            case Fault.F_B_DE:  return "F-B_DE";
            case Fault.F_B_NDE: return "F-B_NDE";
            default:            return f.ToString().Replace("_", "-");
        }
    }

    void Start()
    {
        if (!cam) cam = Camera.main;
        if (useCodeDefaults || profiles == null || profiles.Length == 0) profiles = Defaults();
        cam.nearClipPlane = 0.02f;   // stops big parts being sliced when the camera is close

        var j = JObject.Parse(json.text);
        faultMap = j["fault_map"].ToObject<Dictionary<string, string[]>>();
        colors   = j["colors"].ToObject<Dictionary<string, string>>();

        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (!byName.ContainsKey(t.name)) byName[t.name] = t;
        all = GetComponentsInChildren<Renderer>(true);

        last = fault;
        Apply(Code(fault));
        cam.transform.SetPositionAndRotation(tPos, tRot);
    }

    void Update()
    {
        if (faultMap == null || fault == last) return;
        last = fault;
        Apply(Code(fault));
    }

    public void Apply(string code)
    {
        if (!faultMap.ContainsKey(code)) return;
        var p = profiles.FirstOrDefault(x => x.code == code) ?? new Profile();

        var faulty = RenderersOf(faultMap[code]);
        var hidden = RenderersOf(p.hide);

        Color col = Color.white;
        colors.TryGetValue(code.Split('_')[0], out var hex);
        if (hex != null) ColorUtility.TryParseHtmlString(hex, out col);
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor("_BaseColor", col);

        foreach (var r in all)
        {
            bool isFaulty = faulty.Contains(r);
            r.enabled = isFaulty || !hidden.Contains(r);
            r.SetPropertyBlock(isFaulty ? mpb : null);
        }

        HashSet<Renderer> framed = p.frame.Length > 0 ? RenderersOf(p.frame) : faulty;
        if (framed.Count == 0) framed = new HashSet<Renderer>(all.Where(r => r.enabled));

        var b = framed.First().bounds;
        foreach (var r in framed) b.Encapsulate(r.bounds);
        Frame(b, p.pitchYaw);
    }

    HashSet<Renderer> RenderersOf(string[] names)
    {
        var set = new HashSet<Renderer>();
        foreach (var n in names)
            if (byName.TryGetValue(n, out var t))
                foreach (var r in t.GetComponentsInChildren<Renderer>(true)) set.Add(r);
        return set;
    }

    /*void Frame(Bounds b, Vector2 pitchYaw)
    {
        tRot = Quaternion.Euler(pitchYaw.x, pitchYaw.y, 0);
        float dist = b.extents.magnitude * padding / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        dist = Mathf.Max(dist, minDistance);
        tPos = b.center - tRot * Vector3.forward * dist;
        hasT = true;
    }

    void LateUpdate()
    {
        if (!hasT) return;
        float t = 1f - Mathf.Exp(-smooth * Time.deltaTime);
        cam.transform.SetPositionAndRotation(
            Vector3.Lerp(cam.transform.position, tPos, t),
            Quaternion.Slerp(cam.transform.rotation, tRot, t));
    }*/
     /*   void Frame(Bounds b, Vector2 pitchYaw)
    {
        float d = b.extents.magnitude * padding / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
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
        center = baseCenter; yaw = baseYaw; pitch = basePitch; dist = baseDist;
    }

    void LateUpdate()
    {
        if (!hasT) return;
        tRot = Quaternion.Euler(pitch, yaw, 0);
        tPos = center - tRot * Vector3.forward * dist;
        float t = 1f - Mathf.Exp(-smooth * Time.deltaTime);
        cam.transform.SetPositionAndRotation(
            Vector3.Lerp(cam.transform.position, tPos, t),
            Quaternion.Slerp(cam.transform.rotation, tRot, t));
    }

    void OnGUI()
    {
        if (!hasT) return;
        var e = Event.current;

        if (e.type == EventType.MouseDrag)
        {
            if (e.button == 0)
            {
                yaw += e.delta.x * orbitSpeed;
                pitch = Mathf.Clamp(pitch + e.delta.y * orbitSpeed, minPitch, maxPitch);
            }
            else
            {
                center += (tRot * (Vector3.left * e.delta.x + Vector3.up * e.delta.y)) * dist * 0.002f;
                center = baseCenter + Vector3.ClampMagnitude(center - baseCenter, panLimit);
            }
        }
        else if (e.type == EventType.ScrollWheel)
            dist = Mathf.Clamp(dist * (1f + e.delta.y * zoomSpeed), baseDist * 0.4f, baseDist * 2.5f);
        else if ((e.type == EventType.KeyDown && e.keyCode == KeyCode.R) ||
                 (e.type == EventType.MouseDown && e.clickCount == 2))
            ResetView();
    }
}*/
/*
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class FaultVisualizer : MonoBehaviour
{
    // F_B is the dataset's single bearing fault (both bearings). F_B_DE and F_B_NDE
    // stay as manual views of one side. F_B is last so saved dropdown values do not shift.
    public enum Fault { H_H, R_U, R_M, B_R, K_A, S_W, V_U, F_B_DE, F_B_NDE, F_B }

    [System.Serializable]
    public class Profile
    {
        public string code;
        public string[] hide = new string[0];    // parts hidden for this fault
        public string[] frame = new string[0];   // camera frames these; empty = colored parts, or whole motor if none
        public Vector2 pitchYaw = new Vector2(20, 35);
    }

    static Profile P(string code, float pitch, float yaw, string[] hide, string[] frame = null)
    {
        return new Profile { code = code, pitchYaw = new Vector2(pitch, yaw), hide = hide, frame = frame ?? new string[0] };
    }

    static readonly string[] Shell        = { "Frame_Housing", "Stator_Core", "Fan_Cover" };
    static readonly string[] ShellEnds    = { "Frame_Housing", "EndShield_DE", "EndShield_NDE", "Stator_Core", "Fan_Cover" };
    static readonly string[] BearingsOpen = { "Frame_Housing", "EndShield_DE", "EndShield_NDE", "Fan_Cover", "Fan" };
    static readonly string[] None         = new string[0];

    [Header("Setup")]
    public TextAsset json;
    public Camera cam;

    [Header("Fault source")]
    [Tooltip("Tick to ignore live data and use the Fault dropdown below")]
    public bool useDropdown = false;
    [Tooltip("Shown before any live data arrives, or always when Use Dropdown is ticked")]
    public Fault fault;

    [Header("Per-fault view (edit here)")]
    public bool useCodeDefaults = true;   // untick to use the Inspector table instead
    public Profile[] profiles;

    static Profile[] Defaults() => new[]
    {
        P("H-H",    20,  35, None),
        P("V-U",    25,  35, None, new[] { "Frame_Housing", "Terminal_Box" }),
        P("S-W",    20,  35, Shell),
        P("R-U",    15,  20, Shell, new[] { "Rotor_Core", "Rotor_End_Ring_DE", "Rotor_End_Ring_NDE" }),
        P("B-R",    15,  20, ShellEnds, new[] { "Shaft", "Rotor_Core" }),
        P("K-A",    15,  20, Shell, new[] { "Rotor_Core", "Rotor_End_Ring_DE", "Rotor_End_Ring_NDE" }),
        P("R-M",    15,  60, ShellEnds, new[] { "Shaft", "Coupling_Hub" }),
        P("F-B",    15,  20, BearingsOpen),   // both bearings coloured, camera frames them both
        P("F-B_DE", 15,  70, new[] { "Frame_Housing", "EndShield_DE" }),
        P("F-B_NDE",15, -70, new[] { "Frame_Housing", "EndShield_NDE", "Fan_Cover", "Fan" }),
    };

    [Header("Camera")]
    public float padding = 1.4f;
    public float minDistance = 0.4f;
    public float smooth = 4f;

    [Header("Interaction")]
    public float orbitSpeed = 0.3f;
    public float zoomSpeed = 0.1f;
    public float minPitch = -5f, maxPitch = 80f;

    Vector3 center, baseCenter;
    float yaw, pitch, dist, baseYaw, basePitch, baseDist, panLimit;
    Dictionary<string, string[]> faultMap;
    Dictionary<string, string> colors;
    readonly Dictionary<string, Transform> byName = new Dictionary<string, Transform>();
    Renderer[] all;
    string shownCode;
    Vector3 tPos;
    Quaternion tRot;
    bool hasT;

    static string Code(Fault f)
    {
        switch (f)
        {
            case Fault.F_B_DE:  return "F-B_DE";
            case Fault.F_B_NDE: return "F-B_NDE";
            default:            return f.ToString().Replace("_", "-");
        }
    }

    // Live fault from MotorState if there is one, otherwise the dropdown.
    string WantedCode()
    {
        var state = MotorState.Instance;
        if (!useDropdown && state != null && state.HasFault) return state.fault;
        return Code(fault);
    }

    void Start()
    {
        if (!cam) cam = Camera.main;
        if (useCodeDefaults || profiles == null || profiles.Length == 0) profiles = Defaults();
        cam.nearClipPlane = 0.02f;   // stops big parts being sliced when the camera is close

        var j = JObject.Parse(json.text);
        faultMap = j["fault_map"].ToObject<Dictionary<string, string[]>>();
        colors   = j["colors"].ToObject<Dictionary<string, string>>();

        // The dataset has one bearing fault. Build it from the two sides, so the json
        // does not need changing and Blender does not need to be run again.
        if (!faultMap.ContainsKey("F-B")
            && faultMap.TryGetValue("F-B_DE", out var de)
            && faultMap.TryGetValue("F-B_NDE", out var nde))
            faultMap["F-B"] = de.Union(nde).ToArray();

        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (!byName.ContainsKey(t.name)) byName[t.name] = t;
        all = GetComponentsInChildren<Renderer>(true);

        shownCode = WantedCode();
        Apply(shownCode);
        UpdateTarget();
        cam.transform.SetPositionAndRotation(tPos, tRot);
    }

    void Update()
    {
        if (faultMap == null) return;
        string wanted = WantedCode();
        if (wanted == shownCode) return;
        shownCode = wanted;
        Apply(wanted);
    }

    public void Apply(string code)
    {
        if (!faultMap.ContainsKey(code))
        {
            Debug.LogWarning("FaultVisualizer: unknown fault code '" + code + "'");
            return;
        }
        var p = profiles.FirstOrDefault(x => x.code == code) ?? new Profile();

        var faulty = RenderersOf(faultMap[code]);
        var hidden = RenderersOf(p.hide);

        Color col = Color.white;
        colors.TryGetValue(code.Split('_')[0], out var hex);
        if (hex != null) ColorUtility.TryParseHtmlString(hex, out col);
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor("_BaseColor", col);

        foreach (var r in all)
        {
            bool isFaulty = faulty.Contains(r);
            r.enabled = isFaulty || !hidden.Contains(r);
            r.SetPropertyBlock(isFaulty ? mpb : null);
        }

        HashSet<Renderer> framed = p.frame.Length > 0 ? RenderersOf(p.frame) : faulty;
        if (framed.Count == 0) framed = new HashSet<Renderer>(all.Where(r => r.enabled));

        var b = framed.First().bounds;
        foreach (var r in framed) b.Encapsulate(r.bounds);
        Frame(b, p.pitchYaw);
    }

    HashSet<Renderer> RenderersOf(string[] names)
    {
        var set = new HashSet<Renderer>();
        foreach (var n in names)
            if (byName.TryGetValue(n, out var t))
                foreach (var r in t.GetComponentsInChildren<Renderer>(true)) set.Add(r);
        return set;
    }

    void Frame(Bounds b, Vector2 pitchYaw)
    {
        float d = b.extents.magnitude * padding / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
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
        center = baseCenter; yaw = baseYaw; pitch = basePitch; dist = baseDist;
    }

    void UpdateTarget()
    {
        tRot = Quaternion.Euler(pitch, yaw, 0);
        tPos = center - tRot * Vector3.forward * dist;
    }

    void LateUpdate()
    {
        if (!hasT) return;
        UpdateTarget();
        float t = 1f - Mathf.Exp(-smooth * Time.deltaTime);
        cam.transform.SetPositionAndRotation(
            Vector3.Lerp(cam.transform.position, tPos, t),
            Quaternion.Slerp(cam.transform.rotation, tRot, t));
    }

    void OnGUI()
    {
        if (!hasT) return;
        var e = Event.current;

        if (e.type == EventType.MouseDrag)
        {
            if (e.button == 0)
            {
                yaw += e.delta.x * orbitSpeed;
                pitch = Mathf.Clamp(pitch + e.delta.y * orbitSpeed, minPitch, maxPitch);
            }
            else
            {
                center += (tRot * (Vector3.left * e.delta.x + Vector3.up * e.delta.y)) * dist * 0.002f;
                center = baseCenter + Vector3.ClampMagnitude(center - baseCenter, panLimit);
            }
        }
        else if (e.type == EventType.ScrollWheel)
            dist = Mathf.Clamp(dist * (1f + e.delta.y * zoomSpeed), baseDist * 0.4f, baseDist * 2.5f);
        else if ((e.type == EventType.KeyDown && e.keyCode == KeyCode.R) ||
                 (e.type == EventType.MouseDown && e.clickCount == 2))
            ResetView();
    }
}*/
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class FaultVisualizer : MonoBehaviour
{
    // F_B is the dataset's single bearing fault (both bearings). F_B_DE and F_B_NDE
    // stay as manual views of one side. F_B is last so saved dropdown values do not shift.
    public enum Fault { H_H, R_U, R_M, B_R, K_A, S_W, V_U, F_B_DE, F_B_NDE, F_B }

    [System.Serializable]
    public class Profile
    {
        public string code;
        public string[] hide = new string[0];    // parts hidden for this fault
        public string[] frame = new string[0];   // camera frames these; empty = colored parts, or whole motor if none
        public Vector2 pitchYaw = new Vector2(20, 35);
    }

    static Profile P(string code, float pitch, float yaw, string[] hide, string[] frame = null)
    {
        return new Profile { code = code, pitchYaw = new Vector2(pitch, yaw), hide = hide, frame = frame ?? new string[0] };
    }

    static readonly string[] Shell        = { "Frame_Housing", "Stator_Core", "Fan_Cover" };
    static readonly string[] ShellEnds    = { "Frame_Housing", "EndShield_DE", "EndShield_NDE", "Stator_Core", "Fan_Cover" };
    static readonly string[] BearingsOpen = { "Frame_Housing", "EndShield_DE", "EndShield_NDE", "Fan_Cover", "Fan" };
    static readonly string[] None         = new string[0];

    [Header("Setup")]
    public TextAsset json;
    public Camera cam;

    [Header("Fault source")]
    [Tooltip("Tick to ignore live data and use the Fault dropdown below")]
    public bool useDropdown = false;
    [Tooltip("Shown before any live data arrives, or always when Use Dropdown is ticked")]
    public Fault fault;

    [Header("Per-fault view (edit here)")]
    public bool useCodeDefaults = true;   // untick to use the Inspector table instead
    public Profile[] profiles;

    static Profile[] Defaults() => new[]
    {
        P("H-H",    20,  35, None),
        P("V-U",    25,  35, None, new[] { "Frame_Housing", "Terminal_Box" }),
        P("S-W",    20,  35, Shell),
        P("R-U",    15,  20, Shell, new[] { "Rotor_Core", "Rotor_End_Ring_DE", "Rotor_End_Ring_NDE" }),
        P("B-R",    15,  20, ShellEnds, new[] { "Shaft", "Rotor_Core" }),
        P("K-A",    15,  20, Shell, new[] { "Rotor_Core", "Rotor_End_Ring_DE", "Rotor_End_Ring_NDE" }),
        P("R-M",    15,  60, ShellEnds, new[] { "Shaft", "Coupling_Hub" }),
        P("F-B",    15,  20, BearingsOpen),   // both bearings coloured, camera frames them both
        P("F-B_DE", 15,  70, new[] { "Frame_Housing", "EndShield_DE" }),
        P("F-B_NDE",15, -70, new[] { "Frame_Housing", "EndShield_NDE", "Fan_Cover", "Fan" }),
    };

    [Header("Camera")]
    public float padding = 1.4f;
    public float minDistance = 0.4f;
    public float smooth = 4f;
    [Tooltip("Lowest the camera may go above the floor (needs MotorScene)")]
    public float heightAboveFloor = 0.05f;

    [Header("Interaction")]
    public float orbitSpeed = 0.3f;
    public float zoomSpeed = 0.1f;
    public float minPitch = 10f, maxPitch = 80f;

    Vector3 center, baseCenter;
    float yaw, pitch, dist, baseYaw, basePitch, baseDist, panLimit;
    Dictionary<string, string[]> faultMap;
    Dictionary<string, string> colors;
    readonly Dictionary<string, Transform> byName = new Dictionary<string, Transform>();
    Renderer[] all;
    string shownCode;
    Vector3 tPos;
    Quaternion tRot;
    bool hasT;

    // Read by FaultCallout
    public string ShownCode => shownCode;
    public Camera ViewCamera => cam;
    public Color FaultColor { get; private set; } = Color.white;
    // parts to point arrows at: one group normally, two for F-B (one per bearing), none for healthy
    public List<Renderer[]> AnchorGroups { get; } = new List<Renderer[]>();
    // where the camera starts for this fault (used to place the info panel)
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
            default:            return f.ToString().Replace("_", "-");
        }
    }

    // Live fault from MotorState if there is one, otherwise the dropdown.
    string WantedCode()
    {
        var state = MotorState.Instance;
        if (!useDropdown && state != null && state.HasFault) return state.fault;
        return Code(fault);
    }

    void Start()
    {
        if (!cam) cam = Camera.main;
        if (useCodeDefaults || profiles == null || profiles.Length == 0) profiles = Defaults();
        cam.nearClipPlane = 0.02f;   // stops big parts being sliced when the camera is close

        var j = JObject.Parse(json.text);
        faultMap = j["fault_map"].ToObject<Dictionary<string, string[]>>();
        colors   = j["colors"].ToObject<Dictionary<string, string>>();

        // The dataset has one bearing fault. Build it from the two sides, so the json
        // does not need changing and Blender does not need to be run again.
        if (!faultMap.ContainsKey("F-B")
            && faultMap.TryGetValue("F-B_DE", out var de)
            && faultMap.TryGetValue("F-B_NDE", out var nde))
            faultMap["F-B"] = de.Union(nde).ToArray();

        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (!byName.ContainsKey(t.name)) byName[t.name] = t;
        all = GetComponentsInChildren<Renderer>(true);

        shownCode = WantedCode();
        Apply(shownCode);
        UpdateTarget();
        cam.transform.SetPositionAndRotation(tPos, tRot);
    }

    void Update()
    {
        if (faultMap == null) return;
        string wanted = WantedCode();
        if (wanted == shownCode) return;
        shownCode = wanted;
        Apply(wanted);
    }

    public void Apply(string code)
    {
        if (!faultMap.ContainsKey(code))
        {
            Debug.LogWarning("FaultVisualizer: unknown fault code '" + code + "'");
            return;
        }
        var p = profiles.FirstOrDefault(x => x.code == code) ?? new Profile();

        var faulty = RenderersOf(faultMap[code]);
        var hidden = RenderersOf(p.hide);

        Color col = Color.white;
        colors.TryGetValue(code.Split('_')[0], out var hex);
        if (hex != null) ColorUtility.TryParseHtmlString(hex, out col);
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor("_BaseColor", col);

        FaultColor = col;
        AnchorGroups.Clear();
        if (code == "F-B" && faultMap.ContainsKey("F-B_DE") && faultMap.ContainsKey("F-B_NDE"))
        {
            AnchorGroups.Add(RenderersOf(faultMap["F-B_DE"]).ToArray());
            AnchorGroups.Add(RenderersOf(faultMap["F-B_NDE"]).ToArray());
        }
        else if (faulty.Count > 0)
            AnchorGroups.Add(faulty.ToArray());

        foreach (var r in all)
        {
            bool isFaulty = faulty.Contains(r);
            r.enabled = isFaulty || !hidden.Contains(r);
            r.SetPropertyBlock(isFaulty ? mpb : null);
        }

        HashSet<Renderer> framed = p.frame.Length > 0 ? RenderersOf(p.frame) : faulty;
        if (framed.Count == 0) framed = new HashSet<Renderer>(all.Where(r => r.enabled));

        var b = framed.First().bounds;
        foreach (var r in framed) b.Encapsulate(r.bounds);
        Frame(b, p.pitchYaw);
    }

    HashSet<Renderer> RenderersOf(string[] names)
    {
        var set = new HashSet<Renderer>();
        foreach (var n in names)
            if (byName.TryGetValue(n, out var t))
                foreach (var r in t.GetComponentsInChildren<Renderer>(true)) set.Add(r);
        return set;
    }

    void Frame(Bounds b, Vector2 pitchYaw)
    {
        float d = b.extents.magnitude * padding / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
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
        center = baseCenter; yaw = baseYaw; pitch = basePitch; dist = baseDist;
    }

    void UpdateTarget()
    {
        tRot = Quaternion.Euler(pitch, yaw, 0);
        tPos = center - tRot * Vector3.forward * dist;

        // never let the camera go under the floor, even when panning down
        var scene = MotorScene.Instance;
        if (scene != null) tPos.y = Mathf.Max(tPos.y, scene.FloorTopY + heightAboveFloor);
    }

    void LateUpdate()
    {
        if (!hasT) return;
        UpdateTarget();
        float t = 1f - Mathf.Exp(-smooth * Time.deltaTime);
        cam.transform.SetPositionAndRotation(
            Vector3.Lerp(cam.transform.position, tPos, t),
            Quaternion.Slerp(cam.transform.rotation, tRot, t));
    }

    void OnGUI()
    {
        if (!hasT) return;
        var e = Event.current;

        if (e.type == EventType.MouseDrag)
        {
            if (e.button == 0)
            {
                yaw += e.delta.x * orbitSpeed;
                pitch = Mathf.Clamp(pitch + e.delta.y * orbitSpeed, minPitch, maxPitch);
            }
            else
            {
                center += (tRot * (Vector3.left * e.delta.x + Vector3.up * e.delta.y)) * dist * 0.002f;
                center = baseCenter + Vector3.ClampMagnitude(center - baseCenter, panLimit);
            }
        }
        else if (e.type == EventType.ScrollWheel)
            dist = Mathf.Clamp(dist * (1f + e.delta.y * zoomSpeed), baseDist * 0.4f, baseDist * 2.5f);
        else if ((e.type == EventType.KeyDown && e.keyCode == KeyCode.R) ||
                 (e.type == EventType.MouseDown && e.clickCount == 2))
            ResetView();
    }
}