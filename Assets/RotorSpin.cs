/*using UnityEngine;

public class RotorSpin : MonoBehaviour
{
    public enum ShaftAxis { X, Y, Z }

    [Header("Shaft")]
    public ShaftAxis axis = ShaftAxis.X;

    [Header("Speed")]
    [Tooltip("Supply frequency in Hz (15, 30, 45 or 60 in the dataset)")]
    public float speedHz = 15f;

    [Tooltip("Slows the picture so the eye can follow it. 1 = real speed")]
    public float slowMotion = 30f;

    [Header("Bearing balls")]
    public Transform bearingDECage;
    public Transform bearingNDECage;

    const float CageRatio = 0.398f;

    void Update()
    {
        // 2-pole motor: revolutions per second equal the supply frequency in Hz
        float shaftDegPerSec = speedHz * 360f / Mathf.Max(slowMotion, 0.01f);

        Vector3 a = Vector3.right;
        if (axis == ShaftAxis.Y) a = Vector3.up;
        if (axis == ShaftAxis.Z) a = Vector3.forward;

        transform.Rotate(a, shaftDegPerSec * Time.deltaTime, Space.Self);

        if (bearingDECage != null)
            bearingDECage.Rotate(a, shaftDegPerSec * CageRatio * Time.deltaTime, Space.Self);
        if (bearingNDECage != null)
            bearingNDECage.Rotate(a, shaftDegPerSec * CageRatio * Time.deltaTime, Space.Self);
    }
}
*/
using UnityEngine;
using UnityEngine.Serialization;

public class RotorSpin : MonoBehaviour
{
    public enum ShaftAxis { X, Y, Z }

    [Header("Shaft")]
    public ShaftAxis axis = ShaftAxis.X;

    [Header("Speed")]
    [Tooltip("Speed used before any live data arrives, or always when Use Manual Speed is ticked")]
    [FormerlySerializedAs("speedHz")]
    public float manualSpeedHz = 15f;

    [Tooltip("Tick to ignore live data and use the Manual Speed Hz box")]
    public bool useManualSpeed = false;

    [Tooltip("Seconds the rotor takes to settle on a new speed. 0 = jump at once")]
    public float easeSeconds = 0.5f;

    [Tooltip("Slows the picture so the eye can follow it. 1 = real speed")]
    public float slowMotion = 30f;

    [Header("Bearing balls")]
    public Transform bearingDECage;
    public Transform bearingNDECage;

    const float CageRatio = 0.398f;

    float currentHz;

    void Start()
    {
        var state = MotorState.Instance;
        currentHz = (!useManualSpeed && state != null && state.HasSpeed) ? state.speedHz : manualSpeedHz;
    }

    void Update()
    {
        // The latest reading stays in MotorState, so the rotor keeps turning
        // at the last known speed when new messages stop arriving.
        var state = MotorState.Instance;
        float target = manualSpeedHz;
        if (!useManualSpeed && state != null && state.HasSpeed)
            target = state.speedHz;

        if (easeSeconds > 0.001f)
            currentHz = Mathf.Lerp(currentHz, target, 1f - Mathf.Exp(-Time.deltaTime / easeSeconds));
        else
            currentHz = target;

        // 2-pole motor: revolutions per second equal the supply frequency in Hz
        float shaftDegPerSec = currentHz * 360f / Mathf.Max(slowMotion, 0.01f);

        Vector3 a = Vector3.right;
        if (axis == ShaftAxis.Y) a = Vector3.up;
        if (axis == ShaftAxis.Z) a = Vector3.forward;

        transform.Rotate(a, shaftDegPerSec * Time.deltaTime, Space.Self);

        if (bearingDECage != null)
            bearingDECage.Rotate(a, shaftDegPerSec * CageRatio * Time.deltaTime, Space.Self);
        if (bearingNDECage != null)
            bearingNDECage.Rotate(a, shaftDegPerSec * CageRatio * Time.deltaTime, Space.Self);
    }
}