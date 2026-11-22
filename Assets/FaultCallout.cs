using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// A flat info panel that floats in the scene, like a sign. It is a real object: it stays where
// it is put, so orbiting the camera changes how it looks, other parts can hide it, and the camera
// can pass straight through it. Seen from behind it shows the same text the right way round, and it
// fades out when seen exactly edge-on.
// A 3D arrow runs from each faulty part to the nearest edge of the panel.
// Each time the fault changes, the panel is placed again to the left of the framed part, face-on to
// where the camera starts. Put this on any object, for example Motor_state.
// The texts are in the table below. Edit them to match how your dataset was made.
[DefaultExecutionOrder(100)]   // run after FaultVisualizer has moved the camera
public class FaultCallout : MonoBehaviour
{
    [Header("Look")]
    public bool showCallout = true;
    [Tooltip("Keep the panel square-on to the camera and on the screen's left as you orbit")]
    public bool faceCamera = true;
    [Tooltip("Seconds the panel takes to fade in when the fault changes")]
    public float fadeSeconds = 0.3f;
    [Range(0.05f, 0.6f)] public float boxTint = 0.28f;
    [Range(0.05f, 0.6f)]
    [Tooltip("Panel width as a fraction of the screen width at the starting view (same on-screen size for every fault; it shrinks only if there is no room left of the motor)")]
    public float panelScreenWidth = 0.3f;
    [Range(0f, 0.2f)]
    [Tooltip("Gap between motor and panel, as a fraction of half the screen width (0 = touching)")]
    public float panelGap = 0.03f;
    [Range(-0.5f, 0.8f)]
    [Tooltip("Moves the panel up (+) or down (-), as a fraction of half the screen height")]
    public float panelUp = 0.2f;
    [Range(0.2f, 1f)]
    [Tooltip("The panel is never taller than this fraction of the screen height")]
    public float panelMaxHeight = 0.7f;
    [Tooltip("Text size inside the panel")]
    public int fontSize = 28;
    [Tooltip("Arrow thickness as a fraction of the framed part's radius")]
    public float arrowThickness = 0.01f;

    const float PanelPixels = 560f;   // panel width in canvas pixels; the height follows the text
    const float Pad = 28f;
    const float EdgePixels = 4f;

    // code -> { title, what it is, what to expect, criticality }
    static readonly Dictionary<string, string[]> Info = new Dictionary<string, string[]>
    {
        { "H-H", new[] {
            "Healthy motor",
            "No fault. The motor runs normally.",
            "This is the reference. Its vibration and sound are what every fault is compared against.",
            "None. Normal operation." } },

        { "R-U", new[] {
            "Rotor unbalance",
            "Weight is uneven around the rotor, so its heavy side is off the centre line of the shaft.",
            "A strong shake once for every turn of the rotor. It grows as the motor speeds up.",
            "Moderate. Vibration grows with speed; wears bearings if left." } },

        { "R-M", new[] {
            "Rotor misalignment",
            "The shaft is not lined up straight with the machine it drives.",
            "Extra shake at once and twice the rotor speed, often along the shaft as well as across it.",
            "Moderate to high. Wears bearings and coupling steadily." } },

        { "B-R", new[] {
            "Bowed rotor",
            "The shaft is slightly bent.",
            "A shake once per turn, much like unbalance. A bad bend can also make the rotor rub on the stator.",
            "High. Can cause rotor-stator rub and severe damage." } },

        { "K-A", new[] {
            "Broken rotor bar",
            "One or more bars in the rotor cage are cracked or broken.",
            "The magnetic pull becomes uneven. This adds extra vibration and a small regular wobble in speed, and it shows most when the motor is loaded.",
            "Moderate at first, high as more bars fail. Plan repair." } },

        { "S-W", new[] {
            "Stator winding fault",
            "The insulation on the stator coils has failed, so some turns of the winding touch each other.",
            "The magnetic field becomes uneven. Expect a louder hum, extra vibration at twice the supply frequency, and a rise in temperature.",
            "High. Can progress to a winding short and motor burnout." } },

        { "V-U", new[] {
            "Voltage unbalance",
            "The three supply voltages are not equal.",
            "The motor pulls unevenly. Expect a stronger hum, extra vibration at twice the supply frequency, and extra heating.",
            "Moderate. Causes overheating and shortens insulation life." } },

        { "F-B", new[] {
            "Bearing fault",
            "A bearing is damaged, for example worn, pitted or cracked.",
            "Each time a ball rolls over the damaged spot it makes a small knock. This gives sharp, high-pitched vibration and noise. The data does not say which bearing it is, so both are marked.",
            "High. Can fail suddenly and seize the shaft." } },

        // manual views from the dropdown; the dataset never produces these
        { "F-B_DE", new[] {
            "Bearing fault (drive end)",
            "A bearing is damaged, for example worn, pitted or cracked.",
            "Sharp, high-pitched vibration and noise. This is a manual view of one side; the dataset does not separate the two bearings.",
            "High. Can fail suddenly and seize the shaft." } },

        { "F-B_NDE", new[] {
            "Bearing fault (non-drive end)",
            "A bearing is damaged, for example worn, pitted or cracked.",
            "Sharp, high-pitched vibration and noise. This is a manual view of one side; the dataset does not separate the two bearings.",
            "High. Can fail suddenly and seize the shaft." } },
    };

    class Face
    {
        public GameObject go;
        public CanvasGroup group;
        public RectTransform rect, textRect;
        public Image tint;
        public Image[] edges = new Image[4];
        public Text text;
    }

    FaultVisualizer fv;
    Transform panelRoot;
    Face front, back;
    readonly List<LineRenderer> arrows = new List<LineRenderer>();
    Material lineMaterial;

    string lastCode;
    string currentText = "";
    float panelHeightPx = 300f;
    float appear;
    int relayout;
    float lastWidth, lastGap, lastMaxH, lastUp;
    int lastFont;

    void Start()
    {
        fv = FindFirstObjectByType<FaultVisualizer>();

        var root = new GameObject("FaultPanel");
        panelRoot = root.transform;
        Font font = LoadFont();
        front = BuildFace(panelRoot, "Front", font, 0f, false);
        back = BuildFace(panelRoot, "Back", font, 0.5f, true);   // the same panel turned round, for the other side
        root.SetActive(false);
    }

    void OnDestroy()
    {
        if (panelRoot != null) Destroy(panelRoot.gameObject);
        foreach (var a in arrows) if (a != null) Destroy(a.gameObject);
        if (lineMaterial != null) Destroy(lineMaterial);
    }

    static Font LoadFont()
    {
        Font f = null;
        try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
        if (f == null) f = Font.CreateDynamicFontFromOSFont("Arial", 28);
        return f;
    }

    static Image NewImage(Transform parent, string imageName)
    {
        var go = new GameObject(imageName, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.GetComponent<Image>();
        img.raycastTarget = false;
        return img;
    }

    static void SetEdge(Image img, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size)
    {
        var rt = img.rectTransform;
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
    }

    Face BuildFace(Transform parent, string faceName, Font font, float zOffset, bool flipped)
    {
        var f = new Face();
        f.go = new GameObject(faceName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        f.go.transform.SetParent(parent, false);
        f.go.transform.localRotation = flipped ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
        f.go.transform.localPosition = new Vector3(0f, 0f, zOffset);

        var canvas = f.go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        f.go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 2f;   // sharper text
        f.group = f.go.GetComponent<CanvasGroup>();
        f.group.blocksRaycasts = false;
        f.group.interactable = false;
        f.rect = (RectTransform)f.go.transform;
        f.rect.pivot = new Vector2(0.5f, 0.5f);

        // dark layer first so the text reads well over any background, then the colour on top
        var dark = NewImage(f.go.transform, "Dark");
        dark.color = new Color(0f, 0f, 0f, 0.5f);
        f.tint = NewImage(f.go.transform, "Tint");

        for (int i = 0; i < 4; i++) f.edges[i] = NewImage(f.go.transform, "Edge" + i);
        SetEdge(f.edges[0], new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1f), new Vector2(0f, EdgePixels));   // top
        SetEdge(f.edges[1], new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0f), new Vector2(0f, EdgePixels));   // bottom
        SetEdge(f.edges[2], new Vector2(0, 0), new Vector2(0, 1), new Vector2(0f, 0.5f), new Vector2(EdgePixels, 0f));   // left
        SetEdge(f.edges[3], new Vector2(1, 0), new Vector2(1, 1), new Vector2(1f, 0.5f), new Vector2(EdgePixels, 0f));   // right

        var tgo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        tgo.transform.SetParent(f.go.transform, false);
        f.textRect = (RectTransform)tgo.transform;
        f.textRect.anchorMin = f.textRect.anchorMax = new Vector2(0.5f, 0.5f);
        f.textRect.pivot = new Vector2(0.5f, 0.5f);
        f.text = tgo.GetComponent<Text>();
        f.text.font = font;
        f.text.color = Color.white;
        f.text.alignment = TextAnchor.UpperLeft;
        f.text.horizontalOverflow = HorizontalWrapMode.Wrap;
        f.text.verticalOverflow = VerticalWrapMode.Overflow;
        f.text.supportRichText = true;
        f.text.raycastTarget = false;
        return f;
    }

    // Sets the text on both sides and sizes the panel to fit it.
    void Layout(string text)
    {
        float textW = PanelPixels - 2f * Pad;
        foreach (var f in new[] { front, back })
        {
            f.text.fontSize = fontSize;
            f.text.text = text;
            f.textRect.sizeDelta = new Vector2(textW, 100f);
        }

        float h = front.text.preferredHeight;
        panelHeightPx = h + 2f * Pad;

        foreach (var f in new[] { front, back })
        {
            f.rect.sizeDelta = new Vector2(PanelPixels, panelHeightPx);
            f.textRect.sizeDelta = new Vector2(textW, h);
            f.textRect.anchoredPosition = Vector2.zero;
        }
    }

    Renderer[] motorRenderers;
    readonly List<Bounds> restBounds = new List<Bounds>();   // taken when the fault changes, so shaking does not move the panel
    float panelW, baseHalfW, baseHalfH, baseMargin;

    // How far left of the framed centre the visible motor reaches, as seen from a camera
    // with rotation rot at distance dist (perspective included).
    float LeftExtent(Quaternion rot, float dist)
    {
        Vector3 right = rot * Vector3.right, fwd = rot * Vector3.forward;
        float minX = 0f;
        foreach (var b in restBounds)
        {
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3(
                    (i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = corner - fv.BaseCenter;
                float depth = Mathf.Max(dist + Vector3.Dot(p, fwd), 0.05f);
                minX = Mathf.Min(minX, Vector3.Dot(p, right) * dist / depth);
            }
        }
        return -minX;
    }

    void CacheBounds()
    {
        if (motorRenderers == null) motorRenderers = fv.GetComponentsInChildren<Renderer>(true);
        restBounds.Clear();
        foreach (var r in motorRenderers)
            if (r != null && r.enabled) restBounds.Add(r.bounds);
    }

    // Sets the panel size and puts it just left of the visible motor, face-on to where the camera starts.
    void Place()
    {
        var cam = fv.ViewCamera;
        float halfH = fv.BaseDistance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        baseHalfH = halfH;
        baseHalfW = halfH * cam.aspect;
        baseMargin = baseHalfW * 0.04f;

        CacheBounds();
        Quaternion rot = Quaternion.Euler(fv.BasePitch, fv.BaseYaw, 0f);
        float left = LeftExtent(rot, fv.BaseDistance);

        // as wide as asked, but narrower if the screen is narrow, so it stays in view next to the motor
        float room = baseHalfW - left - baseHalfW * panelGap - baseMargin;
        float byHeight = halfH * 2f * panelMaxHeight * PanelPixels / Mathf.Max(panelHeightPx, 1f);
        panelW = Mathf.Clamp(Mathf.Min(baseHalfW * 2f * panelScreenWidth, Mathf.Min(room, byHeight)), baseHalfW * 0.05f, baseHalfW * 2f);
        panelRoot.localScale = Vector3.one * (panelW / PanelPixels);

        PositionPanel(rot, fv.BaseDistance, true);
    }

    // Puts the panel on the left of the motor as seen with rotation rot at distance dist.
    void PositionPanel(Quaternion rot, float dist, bool clampToScreen)
    {
        float nearX = LeftExtent(rot, dist) + baseHalfW * panelGap + panelW * 0.5f;
        if (clampToScreen) nearX = Mathf.Min(nearX, baseHalfW - baseMargin - panelW * 0.5f);
        panelRoot.SetPositionAndRotation(fv.BaseCenter + rot * new Vector3(-nearX, baseHalfH * panelUp, 0f), rot);
    }

    static Material MakeLineMaterial()
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        return new Material(shader);
    }

    void EnsureArrows(int count)
    {
        while (arrows.Count < count)
        {
            if (lineMaterial == null) lineMaterial = MakeLineMaterial();
            var go = new GameObject("FaultArrow");
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = lineMaterial;
            lr.positionCount = 2;
            lr.useWorldSpace = true;
            lr.alignment = LineAlignment.View;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            arrows.Add(lr);
        }
        for (int i = 0; i < arrows.Count; i++) arrows[i].gameObject.SetActive(i < count);
    }

    // Thin shaft with a wider head that comes to a point at the panel end.
    static AnimationCurve ArrowCurve(float headFraction)
    {
        float t = 1f - headFraction;
        var keys = new[]
        {
            new Keyframe(0f, 1f),
            new Keyframe(t, 1f),
            new Keyframe(Mathf.Min(t + 0.001f, 0.999f), 3.2f),
            new Keyframe(1f, 0f),
        };
        for (int i = 0; i < keys.Length - 1; i++)   // straight segments, no smoothing
        {
            float slope = (keys[i + 1].value - keys[i].value) / Mathf.Max(1e-4f, keys[i + 1].time - keys[i].time);
            keys[i].outTangent = slope;
            keys[i + 1].inTangent = slope;
        }
        return new AnimationCurve(keys);
    }

    void SetVisible(bool visible)
    {
        if (panelRoot != null && panelRoot.gameObject.activeSelf != visible) panelRoot.gameObject.SetActive(visible);
        if (!visible) EnsureArrows(0);
    }

    void LateUpdate()
    {
        if (panelRoot == null) return;
        if (!showCallout || fv == null || fv.ViewCamera == null) { SetVisible(false); return; }

        string code = fv.ShownCode;
        if (string.IsNullOrEmpty(code) || !Info.TryGetValue(code, out var info))
        {
            lastCode = code;
            SetVisible(false);
            return;
        }
        SetVisible(true);

        Color c = fv.FaultColor;

        // Inspector values changed in Play mode: lay out again straight away
        if (lastWidth != panelScreenWidth || lastGap != panelGap || lastMaxH != panelMaxHeight || lastUp != panelUp || lastFont != fontSize)
        {
            lastWidth = panelScreenWidth; lastGap = panelGap; lastMaxH = panelMaxHeight; lastUp = panelUp; lastFont = fontSize;
            relayout = Mathf.Max(relayout, 1);
        }

        if (code != lastCode)
        {
            lastCode = code;
            appear = 0f;
            currentText = "<size=" + Mathf.RoundToInt(fontSize * 1.3f) + "><b>" + info[0] + "</b></size>"
                          + "\n\n<b>What it is:</b> " + info[1]
                          + "\n\n<b>What to expect:</b> " + info[2]
                          + "\n\n<b>Criticality:</b> " + info[3];
            Layout(currentText);
            Place();
            relayout = 2;   // measure the text again on the next frames, in case the font was not ready
        }
        else if (relayout > 0)
        {
            relayout--;
            Layout(currentText);
            Place();
        }

        appear = Mathf.MoveTowards(appear, 1f, fadeSeconds > 0.001f ? Time.unscaledDeltaTime / fadeSeconds : 1f);

        // follow the camera: stay square-on and on the screen's left
        if (faceCamera)
        {
            var ct = fv.ViewCamera.transform;
            float d = Mathf.Max(0.05f, Vector3.Distance(ct.position, fv.BaseCenter));
            PositionPanel(ct.rotation, d, false);
        }

        // colours
        var edgeColor = new Color(c.r, c.g, c.b, 0.9f);
        foreach (var f in new[] { front, back })
        {
            f.tint.color = new Color(c.r, c.g, c.b, boxTint);
            foreach (var e in f.edges) e.color = edgeColor;
        }

        // which side faces the camera? The side you can read is the one you look at along the panel's forward.
        var cam = fv.ViewCamera;
        Vector3 toCam = (cam.transform.position - panelRoot.position).normalized;
        float facing = -Vector3.Dot(panelRoot.forward, toCam);
        float frontShow = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.08f, 0.4f, facing));
        float backShow = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.08f, 0.4f, -facing));
        front.group.alpha = appear * frontShow;
        back.group.alpha = appear * backShow;

        UpdateArrows(c);
    }

    void UpdateArrows(Color c)
    {
        var groups = fv.AnchorGroups;
        EnsureArrows(groups.Count);

        float R = Mathf.Max(0.02f, fv.FrameRadius);
        float halfW = PanelPixels * 0.5f;
        float halfH = panelHeightPx * 0.5f;
        var color = new Color(c.r, c.g, c.b, 0.95f * appear);

        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var lr = arrows[i];
            if (group == null || group.Length == 0) { lr.gameObject.SetActive(false); continue; }

            Bounds b = group[0].bounds;
            for (int k = 1; k < group.Length; k++) b.Encapsulate(group[k].bounds);

            // nearest point on the panel edge to the part (worked out on the panel's own flat surface)
            Vector3 local = panelRoot.InverseTransformPoint(b.center);
            var edge = new Vector3(Mathf.Clamp(local.x, -halfW, halfW), Mathf.Clamp(local.y, -halfH, halfH), 0f);
            Vector3 tip = panelRoot.TransformPoint(edge);

            // tail on the part's own surface, facing the panel
            Vector3 tail = b.ClosestPoint(tip);

            float length = Vector3.Distance(tail, tip);
            lr.SetPosition(0, tail);
            lr.SetPosition(1, tip);
            lr.widthMultiplier = R * arrowThickness;
            lr.widthCurve = ArrowCurve(Mathf.Clamp(R * 0.12f / Mathf.Max(length, 1e-4f), 0.1f, 0.5f));
            lr.startColor = color;
            lr.endColor = color;
        }
    }
}