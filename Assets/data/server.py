import argparse
import asyncio
import base64
import json
import math
import random
import re
import time
from pathlib import Path

import numpy as np
import websockets
from scipy.signal import resample_poly

# ---------- settings you may want to change ----------
DATA_FOLDER = r"D:\2_CSV_Data_Files"  # folder with the dataset csv files (sub folders are searched)
HEALTHY_SECONDS = 40                           # how long each healthy file plays
FAULT_SECONDS = 20                             # how long each fault file plays
AUDIO_GAIN = 0.3                               # how loud the sound is sent (raise or lower to taste)

SHAKE_RATE = 100        # shake values per second sent to Unity
AUDIO_RATE = 16000      # sound samples per second sent to Unity
BLOCK_SECONDS = 0.1     # how much data goes into each shake/sound message
DATA_RATE = 42000       # sample rate of the dataset files and of the model input
WINDOW_SAMPLES = 21000  # model window: 0.5 s
WINDOW_STEP = 0.25      # a new window is sent every 0.25 s
FILE_SPEEDS = {1: (15, 15), 2: (30, 30), 3: (45, 45), 4: (60, 60),
               5: (15, 45), 6: (30, 60), 7: (45, 15), 8: (60, 30)}
FILE_PATTERN = re.compile(r"^([A-Z])_([A-Z])_(\d)_(\d)$")   # e.g. S_W_6_0

# Current readings. Temperature stays out of the messages until a file gives one.
state = {"fault": "H-H", "speed_hz": 15.0, "loaded": False, "temperature_c": None}
clients = set()


def message():
    body = {"type": "readings"}
    body.update({k: v for k, v in state.items() if v is not None})
    return json.dumps(body)


async def send_all(text):
    if clients:
        await asyncio.gather(*(c.send(text) for c in list(clients)), return_exceptions=True)


async def broadcast():
    await send_all(message())


async def handler(ws):
    clients.add(ws)
    print(f"\nUnity connected ({len(clients)} connected)")
    try:
        await ws.send(message())      # new clients get the current readings straight away
        async for _ in ws:            # nothing is expected from Unity
            pass
    except websockets.ConnectionClosed:
        pass
    finally:
        clients.discard(ws)
        print(f"\nUnity disconnected ({len(clients)} connected)")


async def heartbeat(seconds):
    while True:
        await asyncio.sleep(seconds)
        await broadcast()


# ---------- encoding ----------

def encode_pcm16_np(samples, gain):
    ints = np.clip(np.asarray(samples) * gain * 32767, -32768, 32767).astype("<i2")
    return base64.b64encode(ints.tobytes()).decode("ascii")


def window_message(window):
    """window: array of shape (4, 21000) = Accel 1, Accel 2, Accel 3, Microphone, raw values."""
    data = base64.b64encode(np.ascontiguousarray(window, dtype="<f4").tobytes()).decode("ascii")
    return json.dumps({"type": "window", "data": data})


# ---------- one dataset file, ready to play ----------

class Clip:
    """Everything is worked out once, when the file is loaded."""

    def __init__(self, path):
        m = FILE_PATTERN.match(path.stem.upper())
        if not m:
            raise ValueError("File name should look like S_W_6_0, got " + path.stem)
        self.name = path.stem.upper()
        self.fault = m.group(1) + "-" + m.group(2)
        self.speed_from, self.speed_to = FILE_SPEEDS[int(m.group(3))]
        self.loaded = m.group(4) == "1"

        with open(path) as f:
            first = f.readline()
        try:
            [float(x) for x in first.strip().split(",")]
            skip = 0
        except ValueError:
            skip = 1                                  # first line is a header
        data = np.loadtxt(path, delimiter=",", skiprows=skip, dtype=np.float64)

        per_block = int(DATA_RATE * BLOCK_SECONDS)    # 4200 samples
        self.n = (data.shape[0] // per_block) * per_block
        self.n_blocks = self.n // per_block
        self.seconds = self.n / DATA_RATE
        data = data[:self.n]

        # for the model: Accel 1, Accel 2, Accel 3, Mic, raw (columns 0, 2, 3, 1)
        self.raw = np.ascontiguousarray(data[:, [0, 2, 3, 1]].T, dtype=np.float32)

        # for the shaking: offset removed, averaged down to SHAKE_RATE
        factor = DATA_RATE // SHAKE_RATE
        self.shake = {}
        for axis, col in zip("xyz", (0, 2, 3)):
            a = data[:, col] - data[:, col].mean()
            self.shake[axis] = a.reshape(-1, factor).mean(axis=1)

        # for the sound: offset removed, then 42 000 -> 16 000 per second
        mic = data[:, 1] - data[:, 1].mean()
        self.audio = resample_poly(mic, AUDIO_RATE // 2000, DATA_RATE // 2000)

        self.temp = data[:, 4] if data.shape[1] >= 5 else None

    def speed_at(self, t):
        if self.speed_from == self.speed_to:
            return float(self.speed_from)
        frac = min(max(t / self.seconds, 0.0), 1.0)
        return self.speed_from + (self.speed_to - self.speed_from) * frac


async def send_block(clip, k, gain):
    b = k % clip.n_blocks
    per_shake = int(SHAKE_RATE * BLOCK_SECONDS)       # 10
    per_audio = int(AUDIO_RATE * BLOCK_SECONDS)       # 1600
    per_raw = int(DATA_RATE * BLOCK_SECONDS)          # 4200

    state["speed_hz"] = round(clip.speed_at(b * BLOCK_SECONDS), 3)
    if clip.temp is not None:
        t = float(clip.temp[b * per_raw:(b + 1) * per_raw].mean())
        if not math.isnan(t):
            state["temperature_c"] = round(t, 2)
    await broadcast()

    shake = {"type": "shake", "rate": SHAKE_RATE}
    for axis in "xyz":
        shake[axis] = [round(float(v), 4) for v in clip.shake[axis][b * per_shake:(b + 1) * per_shake]]
    audio = {
        "type": "audio",
        "rate": AUDIO_RATE,
        "pcm16": encode_pcm16_np(clip.audio[b * per_audio:(b + 1) * per_audio], gain),
    }
    await send_all(json.dumps(shake))
    await send_all(json.dumps(audio))


async def play(clip, seconds, gain):
    """Plays the file at real speed (repeating it if it is shorter than `seconds`), then returns."""
    state["fault"] = clip.fault
    state["loaded"] = clip.loaded
    t0 = time.monotonic()
    k = 0      # next 0.1 s block of shake and sound
    j = 0      # next model window (window j covers file time 0.25*j to 0.25*j + 0.5, sent when it ends)
    while True:
        now = time.monotonic() - t0
        if now >= seconds:
            return

        if now > k * BLOCK_SECONDS + 1.0:             # fell far behind: skip ahead
            k = int(now / BLOCK_SECONDS)
        while k * BLOCK_SECONDS <= now:
            await send_block(clip, k, gain)
            k += 1

        if now > 0.5 + WINDOW_STEP * j + 1.0:
            j = max(j, int((now - 0.5) / WINDOW_STEP))
        while 0.5 + WINDOW_STEP * j <= now:
            start = int(round(WINDOW_STEP * j * DATA_RATE)) % clip.n
            if start + WINDOW_SAMPLES <= clip.n:      # skip the one window that would cross the loop point
                await send_all(window_message(clip.raw[:, start:start + WINDOW_SAMPLES]))
            j += 1

        await asyncio.sleep(0.02)


# ---------- picking files ----------

def scan_files(folder):
    healthy, faulty = [], []
    for p in Path(folder).rglob("*.csv"):
        m = FILE_PATTERN.match(p.stem.upper())
        if not m:
            continue
        code = m.group(1) + "-" + m.group(2)
        (healthy if code == "H-H" else faulty).append((p, code))
    return healthy, faulty


def pick(pool, avoid_path=None, avoid_code=None):
    options = [x for x in pool if x[0] != avoid_path and x[1] != avoid_code] or pool
    return random.choice(options)


async def run_show(healthy, faulty, healthy_seconds, fault_seconds, gain):
    loop = asyncio.get_running_loop()
    print("Waiting for Unity to connect ...")
    while not clients:
        await asyncio.sleep(0.2)

    last = {"healthy": None, "fault": None}

    def choose(kind):
        if kind == "healthy":
            item = pick(healthy, avoid_path=last["healthy"])
            last["healthy"] = item[0]
        else:
            item = pick(faulty, avoid_code=last["fault"])
            last["fault"] = item[1]
        return item

    kind = "healthy"
    item = choose(kind)
    loading = loop.run_in_executor(None, Clip, item[0])

    while True:
        try:
            clip = await loading
        except Exception as e:
            print(f"Could not load {item[0].name} ({e}), picking another")
            item = choose(kind)
            loading = loop.run_in_executor(None, Clip, item[0])
            continue

        seconds = healthy_seconds if kind == "healthy" else fault_seconds

        # load the next file while this one plays, so there is no gap
        next_kind = "fault" if kind == "healthy" else "healthy"
        next_item = choose(next_kind)
        next_loading = loop.run_in_executor(None, Clip, next_item[0])

        what = "healthy motor (no fault)" if clip.fault == "H-H" else clip.fault + " fault"
        print(f"Picked {clip.name} with {what} "
              f"({'loaded' if clip.loaded else 'unloaded'}, {clip.speed_from} to {clip.speed_to} Hz) "
              f"- playing for {seconds:.0f} s")
        await play(clip, seconds, gain)

        kind, item, loading = next_kind, next_item, next_loading


async def main(args):
    healthy, faulty = scan_files(args.data)
    if not healthy or not faulty:
        print(f"Could not find the dataset files under {Path(args.data).resolve()}")
        print("Set DATA_FOLDER at the top of this script, or start with:  python motor_feed_server.py --data <folder>")
        return
    print(f"Found {len(healthy)} healthy files and {len(faulty)} fault files")

    # compression off: the model windows are big, and squeezing them every 0.25 s only wastes time
    async with websockets.serve(handler, args.host, args.port, compression=None):
        print(f"Feed server running on ws://{args.host}:{args.port}  (Ctrl+C to stop)")
        hb = asyncio.create_task(heartbeat(args.heartbeat))
        try:
            await run_show(healthy, faulty, args.healthy_seconds, args.fault_seconds, args.gain)
        finally:
            hb.cancel()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--heartbeat", type=float, default=1.0, help="seconds between repeats of the current readings")
    ap.add_argument("--data", default=DATA_FOLDER, help="folder with the dataset csv files")
    ap.add_argument("--healthy-seconds", type=float, default=HEALTHY_SECONDS)
    ap.add_argument("--fault-seconds", type=float, default=FAULT_SECONDS)
    ap.add_argument("--gain", type=float, default=AUDIO_GAIN, help="how loud the sound is sent")
    a = ap.parse_args()
    try:
        asyncio.run(main(a))
    except KeyboardInterrupt:
        print("\nStopped")