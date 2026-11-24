using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Connects to the Python feed server and passes each message on.
//   "readings" -> MotorState   (fault, speed, loaded, temperature)
//   "shake"    -> MotorShaker  (accelerometer values)
//   "audio"    -> MotorSoundPlayer (microphone sound)
// Network work happens on a background thread; messages wait in a queue and are
// applied in Update, because Unity only lets the main thread touch scene objects.
// If the server is not running or drops, it keeps retrying.
public class MotorFeedClient : MonoBehaviour
{
    [Header("Server")]
    public string url = "ws://127.0.0.1:8765";
    [Tooltip("Seconds to wait before trying to connect again")]
    public float retrySeconds = 2f;

    [Header("Targets (found automatically if empty)")]
    public MotorState state;
    public MotorShaker shaker;
    public MotorSoundPlayer soundPlayer;
    public MotorModelRunner modelRunner;

    readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
    CancellationTokenSource cts;
    volatile bool connected;
    volatile string lastError;
    bool wasConnected;

    void OnEnable()
    {
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Task.Run(() => RunAsync(token));
    }

    void OnDisable()
    {
        if (cts != null) cts.Cancel();
    }

    void Start()
    {
        if (shaker == null) shaker = FindFirstObjectByType<MotorShaker>();
        if (soundPlayer == null) soundPlayer = FindFirstObjectByType<MotorSoundPlayer>();
        if (modelRunner == null) modelRunner = FindFirstObjectByType<MotorModelRunner>();
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using (var ws = new ClientWebSocket())
            {
                try
                {
                    await ws.ConnectAsync(new Uri(url), ct);
                    connected = true;
                    await ReceiveLoop(ws, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { lastError = e.Message; }
                finally { connected = false; }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), ct); }
            catch (OperationCanceledException) { }
        }
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    return;
                }
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            inbox.Enqueue(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    void Update()
    {
        if (state == null) state = MotorState.Instance;

        if (connected != wasConnected)
        {
            wasConnected = connected;
            if (connected) Debug.Log("MotorFeedClient: connected to " + url);
            else Debug.Log("MotorFeedClient: not connected, retrying every " + retrySeconds + " s"
                           + (string.IsNullOrEmpty(lastError) ? "" : " (" + lastError + ")"));
        }

        while (inbox.TryDequeue(out var text))
            Apply(text);
    }

    static bool IsNumber(JToken t)
    {
        return t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer);
    }

    void Apply(string text)
    {
        JObject j;
        try { j = JObject.Parse(text); }
        catch (Exception) { Debug.LogWarning("MotorFeedClient: could not read a message"); return; }

        if (state != null) state.MarkAlive();   // any message counts as live

        try
        {
            string type = (string)j["type"];
            /*if (type == "shake") ApplyShake(j);
            else if (type == "audio") ApplyAudio(j);
            else ApplyReadings(j);*/
            if (type == "shake") ApplyShake(j);
            else if (type == "audio") ApplyAudio(j);
            else if (type == "window") ApplyWindow(j);
            else ApplyReadings(j);
        }
        catch (Exception e)
        {
            Debug.LogWarning("MotorFeedClient: bad message (" + e.Message + ")");
        }
    }

    void ApplyReadings(JObject j)
    {
        if (state == null) return;

        var fault = j["fault"];
        if (fault != null && fault.Type == JTokenType.String) state.SetFault((string)fault);

        var speed = j["speed_hz"];
        if (IsNumber(speed)) state.SetSpeed((float)speed);

        var loaded = j["loaded"];
        if (loaded != null && loaded.Type == JTokenType.Boolean) state.SetLoaded((bool)loaded);

        var temp = j["temperature_c"];
        if (IsNumber(temp)) state.SetTemperature((float)temp);

        if (fault != null && fault.Type == JTokenType.String && !state.modelControlsFault)
            state.SetFault((string)fault);
    }

    void ApplyShake(JObject j)
    {
        if (shaker == null) return;
        var x = j["x"]?.ToObject<float[]>();
        var y = j["y"]?.ToObject<float[]>();
        var z = j["z"]?.ToObject<float[]>();
        if (x == null || y == null || z == null) return;
        float rate = IsNumber(j["rate"]) ? (float)j["rate"] : 100f;
        shaker.Push(rate, x, y, z);
    }

    void ApplyAudio(JObject j)
    {
        if (soundPlayer == null) return;
        string b64 = (string)j["pcm16"];
        if (string.IsNullOrEmpty(b64)) return;
        int rate = IsNumber(j["rate"]) ? (int)j["rate"] : 16000;

        // 16-bit little-endian sound, turned into numbers between -1 and 1
        byte[] bytes = Convert.FromBase64String(b64);
        int n = bytes.Length / 2;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            short v = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
            samples[i] = v / 32768f;
        }
        soundPlayer.Push(rate, samples);
    }
        // One window of raw sensor values for the model:
    // {"type":"window","data":"<base64 of 4 x 21000 float32, little-endian, channel by channel>"}
    void ApplyWindow(JObject j)
    {
        if (modelRunner == null) return;
        string b64 = (string)j["data"];
        if (string.IsNullOrEmpty(b64)) return;

        byte[] bytes = Convert.FromBase64String(b64);
        int count = bytes.Length / 4;
        if (count != MotorModelRunner.Channels * MotorModelRunner.Samples)
        {
            Debug.LogWarning("MotorFeedClient: window has " + count + " values, expected "
                             + MotorModelRunner.Channels * MotorModelRunner.Samples);
            return;
        }
        var window = new float[count];
        Buffer.BlockCopy(bytes, 0, window, 0, bytes.Length);
        modelRunner.Push(window);
    }
}