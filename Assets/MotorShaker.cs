using System.Collections.Generic;
using UnityEngine;

// Shakes an object by tiny amounts, following accelerometer values sent from Python.
// Put this on the top object of the motor (the one that holds all the parts).
// The values arrive in small groups, so they wait in a queue and are played back
// at the steady rate Python says they were made at.
public class MotorShaker : MonoBehaviour
{
    [Header("What to shake (this object if empty)")]
    public Transform target;

    [Header("Strength")]
    public bool shakeEnabled = true;
    [Tooltip("Scene units (metres) of movement for each 1 m/s^2 of vibration. Turn up to see more shake")]
    public float metersPerUnit = 0.001f;
    [Tooltip("Biggest movement allowed in any direction")]
    public float maxOffset = 0.005f;

    [Header("Smoothness")]
    [Tooltip("Seconds of data to collect before shaking starts, so it does not stutter")]
    public float prebufferSeconds = 0.2f;
    [Tooltip("Seconds the shake takes to fade out when data stops")]
    public float fadeOutSeconds = 0.2f;

    readonly Queue<Vector3> queue = new Queue<Vector3>();
    Vector3 rest;
    Vector3 current;
    float rate = 100f;
    float accumulator;
    bool playing;

    void Start()
    {
        if (target == null) target = transform;
        rest = target.localPosition;
    }

    void OnDisable()
    {
        if (target != null) target.localPosition = rest;
    }

    // Called by MotorFeedClient on the main thread.
    public void Push(float sampleRate, float[] x, float[] y, float[] z)
    {
        rate = Mathf.Max(1f, sampleRate);
        int n = Mathf.Min(x.Length, Mathf.Min(y.Length, z.Length));
        for (int i = 0; i < n; i++)
            queue.Enqueue(new Vector3(x[i], y[i], z[i]));

        int most = Mathf.CeilToInt(rate);   // keep at most one second waiting
        while (queue.Count > most) queue.Dequeue();
    }

    void Update()
    {
        int prebuffer = Mathf.Max(1, Mathf.CeilToInt(rate * prebufferSeconds));
        if (!playing && queue.Count >= prebuffer) playing = true;
        if (playing && queue.Count == 0) { playing = false; accumulator = 0f; }

        if (playing)
        {
            accumulator += rate * Time.deltaTime;
            while (accumulator >= 1f && queue.Count > 0)
            {
                current = queue.Dequeue();
                accumulator -= 1f;
            }
        }
        else
        {
            // No data: the shake fades out. (The rotor keeps turning, because its speed is a
            // reading that stays. Shaking is a live stream, so it stops when the stream stops.)
            float k = fadeOutSeconds > 0.001f ? 1f - Mathf.Exp(-Time.deltaTime / fadeOutSeconds) : 1f;
            current = Vector3.Lerp(current, Vector3.zero, k);
        }

        Vector3 offset = shakeEnabled ? current * metersPerUnit : Vector3.zero;
        offset = Vector3.ClampMagnitude(offset, maxOffset);
        target.localPosition = rest + offset;
    }
}