using UnityEngine;
using Unity.InferenceEngine;   // if this line shows an error, see the note below the code

// Runs motor_fault_classifier.onnx on each sensor window that arrives
// and writes the answer (fault + confidence) into MotorState.
// Input:  [1, 4, 21000]  channels: Accel 1, Accel 2, Accel 3, Microphone (raw values, nothing removed)
// Output: [1, 8]         chances in this order: H-H, R-U, R-M, B-R, K-A, S-W, V-U, F-B
// Put this on Motor_state and drag the .onnx file into Model Asset.
public class MotorModelRunner : MonoBehaviour
{
    [Header("Model")]
    public ModelAsset modelAsset;
    public BackendType backend = BackendType.CPU;   // try GPUCompute if CPU is too slow

    [Header("Steadiness")]
    [Tooltip("A new fault must win this many windows in a row before the motor changes. 1 = change at once.")]
    [Min(1)] public int windowsInARow = 2;

    [Header("Info (read only)")]
    public float lastRunMilliseconds;
    public int windowsRun;

    public const int Channels = 4;
    public const int Samples = 21000;
    static readonly string[] Labels = { "H-H", "R-U", "R-M", "B-R", "K-A", "S-W", "V-U", "F-B" };

    Worker worker;
    float[] pending;        // newest window waiting to be run (older ones are dropped)
    string candidate;
    int candidateCount;

    void Start()
    {
        if (modelAsset == null)
        {
            Debug.LogWarning("MotorModelRunner: no model asset set");
            return;
        }
        var model = ModelLoader.Load(modelAsset);
        worker = new Worker(model, backend);
    }

    void OnDestroy()
    {
        if (worker != null) worker.Dispose();
    }

    // Called by MotorFeedClient on the main thread.
    public void Push(float[] window)
    {
        pending = window;
    }

    void Update()
    {
        if (pending == null) return;
        var data = pending;
        pending = null;

        var state = MotorState.Instance;
        if (worker == null || state == null || !state.modelControlsFault) return;
        if (data.Length != Channels * Samples)
        {
            Debug.LogWarning("MotorModelRunner: window has " + data.Length + " values, expected " + Channels * Samples);
            return;
        }

        float t0 = Time.realtimeSinceStartup;
        float[] probs;
        using (var input = new Tensor<float>(new TensorShape(1, Channels, Samples), data))
        {
            worker.Schedule(input);
            var output = worker.PeekOutput() as Tensor<float>;
            probs = output.DownloadToArray();
        }
        lastRunMilliseconds = (Time.realtimeSinceStartup - t0) * 1000f;
        windowsRun++;

        if (probs == null || probs.Length != Labels.Length)
        {
            Debug.LogWarning("MotorModelRunner: model gave " + (probs == null ? 0 : probs.Length) + " values, expected " + Labels.Length);
            return;
        }

        int best = 0;
        for (int i = 1; i < probs.Length; i++) if (probs[i] > probs[best]) best = i;
        string code = Labels[best];
        float sure = probs[best];

        if (state.HasFault && state.fault == code)
        {
            candidate = null;
            candidateCount = 0;
            state.SetPrediction(code, sure);
        }
        else
        {
            if (code == candidate) candidateCount++;
            else { candidate = code; candidateCount = 1; }

            if (candidateCount >= windowsInARow)
            {
                candidate = null;
                candidateCount = 0;
                state.SetPrediction(code, sure);
            }
        }
    }
}