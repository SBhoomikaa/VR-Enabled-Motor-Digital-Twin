using UnityEngine;

// Plays the motor sound that Python streams in small chunks.
// Chunks wait in a buffer; Unity's audio system pulls from it at a steady pace.
// If the buffer runs dry it plays silence and waits until it has a little cushion again.
[RequireComponent(typeof(AudioSource))]
public class MotorSoundPlayer : MonoBehaviour
{
    [Range(0f, 1f)] public float volume = 0.6f;

    [Tooltip("Seconds of sound to collect before playing, so it does not crackle")]
    public float prebufferSeconds = 0.2f;
    [Tooltip("Most sound kept waiting. Older sound is dropped if more arrives")]
    public float maxBufferSeconds = 1f;

    AudioSource src;
    AudioClip clip;
    int rate;
    int prebufferSamples;
    float[] ring;
    int readPos, writePos, count;
    bool playing;
    readonly object gate = new object();

    void Awake()
    {
        src = GetComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = true;
        src.spatialBlend = 0f;   // plain stereo, not placed in the 3D scene
    }

    void Update()
    {
        src.volume = volume;
    }

    void OnDestroy()
    {
        if (clip != null) Destroy(clip);
    }

    // Called by MotorFeedClient on the main thread.
    public void Push(int sampleRate, float[] samples)
    {
        if (sampleRate <= 0) return;
        if (sampleRate != rate) StartClip(sampleRate);

        lock (gate)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                if (count == ring.Length)    // full: drop the oldest sample
                {
                    readPos = (readPos + 1) % ring.Length;
                    count--;
                }
                ring[writePos] = samples[i];
                writePos = (writePos + 1) % ring.Length;
                count++;
            }
        }
    }

    void StartClip(int sampleRate)
    {
        src.Stop();
        lock (gate)
        {
            rate = sampleRate;
            ring = new float[Mathf.Max(1, Mathf.CeilToInt(rate * maxBufferSeconds))];
            prebufferSamples = Mathf.Clamp(Mathf.CeilToInt(rate * prebufferSeconds), 1, ring.Length);
            readPos = writePos = count = 0;
            playing = false;
        }

        if (clip != null) Destroy(clip);
        clip = AudioClip.Create("MotorFeed", rate, 1, rate, true, OnRead);
        src.clip = clip;
        src.Play();
    }

    // Runs on Unity's audio thread.
    void OnRead(float[] data)
    {
        lock (gate)
        {
            if (!playing && count >= prebufferSamples) playing = true;

            for (int i = 0; i < data.Length; i++)
            {
                if (playing && count > 0)
                {
                    data[i] = ring[readPos];
                    readPos = (readPos + 1) % ring.Length;
                    count--;
                }
                else
                {
                    data[i] = 0f;
                }
            }

            if (count == 0) playing = false;   // ran dry: wait for a cushion again
        }
    }
}