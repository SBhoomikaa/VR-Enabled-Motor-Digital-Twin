using UnityEngine;

// Holds the latest readings. The network script writes here (main thread only);
// RotorSpin and FaultVisualizer read from here every frame.
// Nothing is ever cleared, so the motor keeps running on the last readings
// when messages stop arriving.
public class MotorState : MonoBehaviour
{
    public static MotorState Instance { get; private set; }

    [Header("Latest readings (filled by the network script)")]
    public string fault = "";      // same codes as fault_map.json, e.g. "S-W", "F-B_DE"
    public float speedHz;
    public bool loaded;
    public float temperatureC;

    [Header("Old data")]
    [Tooltip("Seconds without a message before the readings count as old")]
    public float staleAfterSeconds = 5f;
    public bool showTag = true;

    public bool HasFault { get; private set; }
    public bool HasSpeed { get; private set; }
    public bool EverReceived { get; private set; }

    float lastUpdate = float.NegativeInfinity;

    public bool IsLive => Time.unscaledTime - lastUpdate <= staleAfterSeconds;
    public float SecondsSinceUpdate => Time.unscaledTime - lastUpdate;

    void Awake()
    {
        Instance = this;
    }

    void Touch()
    {
        lastUpdate = Time.unscaledTime;
        EverReceived = true;
    }

    // The setters below must be called from Unity's main thread.
    public void SetFault(string code)
    {
        if (string.IsNullOrEmpty(code)) return;
        fault = code;
        HasFault = true;
        Touch();
    }

    public void SetSpeed(float hz)
    {
        speedHz = Mathf.Max(0f, hz);
        HasSpeed = true;
        Touch();
    }

    public void SetLoaded(bool value)
    {
        loaded = value;
        Touch();
    }

    public void SetTemperature(float celsius)
    {
        temperatureC = celsius;
        Touch();
    }

    // Call this for any message that arrives, even if it carries no new value.
    public void MarkAlive()
    {
        Touch();
    }

    void OnGUI()
    {
        if (!showTag || IsLive) return;
        string text = EverReceived
            ? "No live data (last update " + Mathf.RoundToInt(SecondsSinceUpdate) + " s ago)"
            : "Waiting for live data";
        GUI.Label(new Rect(10, 10, 320, 24), text);
    }
}