using System.Collections.Generic;
using UnityEngine;

// Builds the set around the motor when Play starts, so nothing needs lining up by hand:
//   - a base plate under the motor feet and a big floor under that, both placed from the
//     motor's own size (they stay still while the motor shakes)
//   - soft shadows from the main light (a light is added if the scene has none)
//   - a small on-screen readout of speed, load and temperature (FaultCallout shows the fault)
// Put this on any object, for example Motor_state. Runs before FaultVisualizer.
[DefaultExecutionOrder(-50)]
public class MotorScene : MonoBehaviour
{
    public static MotorScene Instance { get; private set; }

    [Header("Motor (found automatically if empty)")]
    public Transform motor;

    [Header("Floor and base plate")]
    public bool buildFloor = true;
    public Color floorColor = new Color(0.20f, 0.21f, 0.23f);
    public Color baseColor = new Color(0.10f, 0.11f, 0.13f);
    [Tooltip("How far the base plate sticks out past the motor, as a fraction of the motor size")]
    public float baseOverhang = 0.2f;
    [Tooltip("Base plate thickness, as a fraction of the motor's longest side")]
    public float baseThickness = 0.05f;
    [Tooltip("Floor width, as a multiple of the motor's longest side")]
    public float floorSize = 60f;

    [Header("Light")]
    public bool softShadows = true;

    [Header("Readout")]
    public bool showReadout = true;

    // Top surface of the floor. FaultVisualizer keeps the camera above this.
    public float FloorTopY { get; private set; } = float.NegativeInfinity;

    GUIStyle boxStyle, labelStyle;

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Start()
    {
        if (motor == null)
        {
            var fv = FindFirstObjectByType<FaultVisualizer>();
            if (fv != null) motor = fv.transform;
        }

        if (buildFloor && motor != null) BuildFloor();
        if (softShadows) SetUpLight();
    }

    void BuildFloor()
    {
        // Active parts only: renderers on switched-off objects can report a box at the origin.
        var rends = motor.GetComponentsInChildren<Renderer>(false);
        if (rends.Length == 0) return;

        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);

        float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        Debug.Log("MotorScene: motor size " + b.size.x.ToString("0.00") + " x " + b.size.y.ToString("0.00")
                  + " x " + b.size.z.ToString("0.00") + " units");

        // base plate: its top face touches the lowest point of the motor
        float plateT = longest * baseThickness;
        var plateSize = new Vector3(b.size.x * (1f + 2f * baseOverhang), plateT, b.size.z * (1f + 2f * baseOverhang));
        var plateCenter = new Vector3(b.center.x, b.min.y - plateT * 0.5f, b.center.z);
        MakeBlock("BasePlate", plateSize, plateCenter, baseColor, 0.35f);

        // floor: its top face touches the underside of the base plate
        float floorT = longest * 0.05f;
        float floorTop = b.min.y - plateT;
        var floorDim = new Vector3(longest * floorSize, floorT, longest * floorSize);
        var floorCenter = new Vector3(b.center.x, floorTop - floorT * 0.5f, b.center.z);
        MakeBlock("Floor", floorDim, floorCenter, floorColor, 0.1f);

        FloorTopY = floorTop;
    }

    static GameObject MakeBlock(string blockName, Vector3 size, Vector3 center, Color color, float smoothness)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = blockName;
        go.transform.position = center;
        go.transform.localScale = size;

        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);   // nothing needs to collide with these

        var m = go.GetComponent<Renderer>().material;
        m.color = color;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        return go;
    }

    static void SetUpLight()
    {
        Light sun = null;
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional) { sun = l; break; }

        if (sun == null)
        {
            var go = new GameObject("Sun");
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
        }
        sun.shadows = LightShadows.Soft;
    }

    void OnGUI()
    {
        if (!showReadout) return;
        var state = MotorState.Instance;
        if (state == null) return;

        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.UpperLeft };
            boxStyle = new GUIStyle(GUI.skin.box);
        }

        int size = Mathf.Max(12, Mathf.RoundToInt(Screen.height * 0.022f));
        labelStyle.fontSize = size;

        string speed = state.HasSpeed ? state.speedHz.ToString("0.0") + " Hz" : "--";
        string load = state.EverReceived ? (state.loaded ? "Loaded" : "Unloaded") : "--";

        string text = "Speed: " + speed + "\nLoad: " + load;
        int lines = 2;
        if (state.temperatureC > 0f)
        {
            text += "\nTemperature: " + state.temperatureC.ToString("0.0") + " \u00B0C";
            lines = 3;
        }

        float w = size * 15f;
        float h = size * 1.45f * lines + size * 0.8f;
        var rect = new Rect(Screen.width - w - 12f, 12f, w, h);
        GUI.Box(rect, GUIContent.none, boxStyle);
        GUI.Label(new Rect(rect.x + size * 0.5f, rect.y + size * 0.4f, rect.width - size, rect.height - size * 0.4f),
                  text, labelStyle);
    }
}