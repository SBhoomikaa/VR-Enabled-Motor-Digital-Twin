import argparse
import asyncio
import base64
import json
import math
import random
import re
import struct
import time
from pathlib import Path

import numpy as np
import websockets
from scipy.signal import resample_poly

# Dataset labels. The dataset has one bearing fault (F-B), not a DE and NDE version.
FAULTS = ["H-H", "R-U", "R-M", "B-R", "K-A", "S-W", "V-U", "F-B"]
FAULT_LOOKUP = {re.sub(r"[^A-Z]", "", f): f for f in FAULTS}   # "FB" -> "F-B"

SHAKE_RATE = 100        # shake values per second sent to Unity
AUDIO_RATE = 16000      # sound samples per second sent to Unity
BLOCK_SECONDS = 0.1     # how much data goes into each shake/sound message
RAW_RATE = 1000         # rate of the pretend accelerometer in sim mode

DATA_RATE = 42000       # sample rate of the dataset files and of the model input
WINDOW_SAMPLES = 21000  # model window: 0.5 s
WINDOW_STEP = 0.25      # a new window is sent every 0.25 s
FILE_SPEEDS = {1: (15, 15), 2: (30, 30), 3: (45, 45), 4: (60, 60),
               5: (15, 45), 6: (30, 60), 7: (45, 15), 8: (60, 30)}

# Starting readings. Temperature stays out of the messages until you set it.
state = {"fault": "H-H", "speed_hz": 15.0, "loaded": False, "temperature_c": None}

clients = set()
ramp_task = None
sim_task = None
win_task = None
play_task = None
audio_gain = 5.0        # how much the sound is turned up before it is sent
data_dir = Path(".")    # where the dataset csv files are (searched in all sub folders)


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
        async for _ in ws:            # nothing is expected from Unity yet
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


# ---------- speed ramp ----------

def stop_ramp():
    global ramp_task
    if ramp_task and not ramp_task.done():
        ramp_task.cancel()
    ramp_task = None


async def run_ramp(start, end, seconds, step=0.1):
    t0 = time.monotonic()
    while True:
        frac = min((time.monotonic() - t0) / seconds, 1.0)
        state["speed_hz"] = round(start + (end - start) * frac, 3)
        await broadcast()
        if frac >= 1.0:
            break
        await asyncio.sleep(step)
    print(f"\nRamp finished at {state['speed_hz']} Hz")


def start_ramp(start, end, seconds):
    global ramp_task
    stop_ramp()
    ramp_task = asyncio.create_task(run_ramp(start, end, seconds))


# ---------- signal tools ----------

class OffsetRemover:
    """Subtracts a slowly moving average, so a constant offset (like gravity) disappears."""

    def __init__(self, rate, seconds=1.0):
        self.alpha = 1.0 / max(1.0, rate * seconds)
        self.mean = None

    def process(self, samples):
        out = []
        for s in samples:
            if self.mean is None:
                self.mean = s            # start from the first value, so there is no jump at the start
            self.mean += self.alpha * (s - self.mean)
            out.append(s - self.mean)
        return out


def average_down(samples, factor):
    """Averages each group of `factor` values into one, so the rate drops by that factor."""
    return [sum(samples[i:i + factor]) / factor for i in range(0, len(samples) - factor + 1, factor)]


def encode_pcm16(samples, gain):
    ints = [max(-32768, min(32767, int(s * gain * 32767))) for s in samples]
    return base64.b64encode(struct.pack("<%dh" % len(ints), *ints)).decode("ascii")


def encode_pcm16_np(samples, gain):
    ints = np.clip(np.asarray(samples) * gain * 32767, -32768, 32767).astype("<i2")
    return base64.b64encode(ints.tobytes()).decode("ascii")


def window_message(window):
    """window: array of shape (4, 21000) = Accel 1, Accel 2, Accel 3, Microphone, raw values."""
    data = base64.b64encode(np.ascontiguousarray(window, dtype="<f4").tobytes()).decode("ascii")
    return json.dumps({"type": "window", "data": data})


# ---------- made-up signals for testing (sim mode) ----------

class TestSignals:
    """Pretend accelerometer and microphone. They follow the speed and get stronger with a fault."""

    def __init__(self):
        self.shaft = 0.0
        self.hum = 0.0

    def accel(self, n, speed_hz, strength):
        step = 2 * math.pi * speed_hz / RAW_RATE
        xs, ys, zs = [], [], []
        for _ in range(n):
            self.shaft = (self.shaft + step) % (2 * math.pi)
            s1 = math.sin(self.shaft)
            c1 = math.cos(self.shaft)
            s2 = math.sin(2 * self.shaft + 0.7)
            # the constant parts (-4, 8, -5) are there on purpose, like the real files
            xs.append(-4.0 + strength * (1.0 * s1 + 0.4 * s2) + random.gauss(0, 0.3))
            ys.append(8.0 + strength * (0.7 * c1 + 0.3 * s2) + random.gauss(0, 0.3))
            zs.append(-5.0 + strength * (0.5 * s1 + 0.5 * s2) + random.gauss(0, 0.3))
        return xs, ys, zs

    def mic(self, n, speed_hz, strength):
        step = 2 * math.pi * speed_hz / AUDIO_RATE
        out = []
        for _ in range(n):
            self.hum = (self.hum + step) % (2 * math.pi)
            tone = 0.020 * math.sin(2 * self.hum) + 0.010 * math.sin(4 * self.hum) + 0.005 * math.sin(6 * self.hum)
            # the constant -0.25 is there on purpose, like the real files
            out.append(-0.25 + strength * tone + strength * random.gauss(0, 0.004))
        return out


async def stream_loop():
    gen = TestSignals()
    accel_fix = [OffsetRemover(RAW_RATE) for _ in range(3)]
    mic_fix = OffsetRemover(AUDIO_RATE)
    n_raw = int(RAW_RATE * BLOCK_SECONDS)
    n_audio = int(AUDIO_RATE * BLOCK_SECONDS)
    factor = RAW_RATE // SHAKE_RATE
    next_t = time.monotonic()

    while True:
        strength = 1.0 if state["fault"] == "H-H" else 3.0
        speed = state["speed_hz"]

        raw = gen.accel(n_raw, speed, strength)
        shake = {"type": "shake", "rate": SHAKE_RATE}
        for name, values, fix in zip("xyz", raw, accel_fix):
            shake[name] = [round(v, 4) for v in average_down(fix.process(values), factor)]

        audio = {
            "type": "audio",
            "rate": AUDIO_RATE,
            "pcm16": encode_pcm16(mic_fix.process(gen.mic(n_audio, speed, strength)), audio_gain),
        }

        await send_all(json.dumps(shake))
        await send_all(json.dumps(audio))

        next_t += BLOCK_SECONDS
        now = time.monotonic()
        if now - next_t > 1.0:          # fell far behind: start again from now
            next_t = now
        await asyncio.sleep(max(0.0, next_t - now))


def stop_sim():
    global sim_task
    if sim_task and not sim_task.done():
        sim_task.cancel()
    sim_task = None


def start_sim():
    global sim_task
    stop_play()
    if sim_task and not sim_task.done():
        return
    sim_task = asyncio.create_task(stream_loop())


# ---------- test windows for the model (made-up values) ----------

def made_up_window(speed_hz, strength, rng):
    """4 x 21000 made-up values. They only test that the model runs, not that it is right."""
    t = np.arange(WINDOW_SAMPLES) / DATA_RATE
    shaft = 2 * np.pi * speed_hz * t + rng.uniform(0, 2 * np.pi)
    w = np.empty((4, WINDOW_SAMPLES), dtype=np.float32)
    for i, (dc, a1, a2) in enumerate([(-4.0, 1.0, 0.4), (8.0, 0.7, 0.3), (-5.0, 0.5, 0.5)]):
        w[i] = dc + strength * (a1 * np.sin(shaft) + a2 * np.sin(2 * shaft + 0.7)) + rng.normal(0, 0.3, WINDOW_SAMPLES)
    w[3] = (-0.25 + strength * (0.020 * np.sin(2 * shaft) + 0.010 * np.sin(4 * shaft) + 0.005 * np.sin(6 * shaft))
            + strength * rng.normal(0, 0.004, WINDOW_SAMPLES))
    return w


async def window_test_loop(fixed):
    rng = np.random.default_rng(1234 if fixed else None)
    fixed_msg = None
    if fixed:
        w = made_up_window(30.0, 1.0, rng)
        np.save("fixed_window.npy", w)      # for your teammate: same numbers, to run in Python
        fixed_msg = window_message(w)
        print("\nSaved fixed_window.npy (shape 4 x 21000, float32). Send this same file to your teammate.")
    next_t = time.monotonic()
    while True:
        if fixed:
            msg = fixed_msg
        else:
            strength = 1.0 if state["fault"] == "H-H" else 3.0
            msg = window_message(made_up_window(state["speed_hz"], strength, rng))
        await send_all(msg)
        next_t += WINDOW_STEP
        now = time.monotonic()
        if now - next_t > 1.0:
            next_t = now
        await asyncio.sleep(max(0.0, next_t - now))


def stop_win():
    global win_task
    if win_task and not win_task.done():
        win_task.cancel()
    win_task = None


def start_win(fixed):
    global win_task
    stop_play()
    stop_win()
    win_task = asyncio.create_task(window_test_loop(fixed))


# ---------- dataset player ----------

class Clip:
    """One dataset file, ready to play. Everything is worked out once, when it is loaded."""

    def __init__(self, path):
        m = re.match(r"^([A-Z])_([A-Z])_(\d)_(\d)$", path.stem.upper())
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


async def send_block(clip, k):
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
        "pcm16": encode_pcm16_np(clip.audio[b * per_audio:(b + 1) * per_audio], audio_gain),
    }
    await send_all(json.dumps(shake))
    await send_all(json.dumps(audio))


async def play_loop(clip):
    state["fault"] = clip.fault
    state["loaded"] = clip.loaded
    t0 = time.monotonic()
    k = 0      # next 0.1 s block of shake and sound
    j = 0      # next model window (window j covers file time 0.25*j to 0.25*j + 0.5, sent when it ends)
    while True:
        now = time.monotonic() - t0

        if now > k * BLOCK_SECONDS + 1.0:             # fell far behind: skip ahead
            k = int(now / BLOCK_SECONDS)
        while k * BLOCK_SECONDS <= now:
            await send_block(clip, k)
            k += 1

        if now > 0.5 + WINDOW_STEP * j + 1.0:
            j = max(j, int((now - 0.5) / WINDOW_STEP))
        while 0.5 + WINDOW_STEP * j <= now:
            start = int(round(WINDOW_STEP * j * DATA_RATE)) % clip.n
            if start + WINDOW_SAMPLES <= clip.n:      # skip the one window that would cross the loop point
                await send_all(window_message(clip.raw[:, start:start + WINDOW_SAMPLES]))
            j += 1

        await asyncio.sleep(0.02)


def stop_play():
    global play_task
    if play_task and not play_task.done():
        play_task.cancel()
    play_task = None


def find_file(key):
    for p in data_dir.rglob("*.csv"):
        if p.stem.upper() == key:
            return p
    return None


async def start_play(text):
    global play_task
    key = re.sub(r"[^A-Z0-9]+", "_", text.upper()).strip("_")
    path = find_file(key)
    if path is None:
        print(f"Could not find {key}.csv under {data_dir.resolve()}  (use: data <folder>)")
        return
    print(f"Loading {path.name} ...")
    loop = asyncio.get_running_loop()
    try:
        clip = await loop.run_in_executor(None, Clip, path)
    except Exception as e:
        print("Could not load the file: " + str(e))
        return
    stop_sim()
    stop_win()
    stop_play()
    stop_ramp()
    play_task = asyncio.create_task(play_loop(clip))
    print(f"Playing {clip.name}: {clip.fault}, {'loaded' if clip.loaded else 'unloaded'}, "
          f"{clip.speed_from} to {clip.speed_to} Hz, {clip.seconds:.0f} s, repeating")


# ---------- terminal commands ----------

HELP = """Commands:
  fault <code>              e.g. fault S-W   (codes: H-H R-U R-M B-R K-A S-W V-U F-B)
  speed <hz>                e.g. speed 45
  load on|off               loaded or unloaded
  temp <celsius>            e.g. temp 42.4
  ramp <from> <to> <secs>   speed rises or falls in a straight line, e.g. ramp 30 60 10
  demo                      like file S_W_6_0: stator winding fault, unloaded, 30 to 60 Hz over 10 s
  sim on|off                stream made-up shake and sound that follow the fault and speed
  gain <number>             how loud the sound is sent (default 5)
  data <folder>             where the dataset csv files are (all sub folders are searched)
  play <file>               play a dataset file at real speed, e.g. play S_W_6_0
                            sends readings, shake, sound and model windows, and repeats
  testwin on|off            send made-up model windows (only tests that the model runs)
  testwin fixed             send the same made-up window again and again (saves fixed_window.npy)
  stop                      stop play, sim and test windows
  show                      print the current readings
  help, quit"""


async def run_command(line):
    """Returns False when it is time to quit."""
    global audio_gain, data_dir
    parts = line.strip().split()
    if not parts:
        return True
    cmd, args = parts[0].lower(), parts[1:]

    try:
        if cmd in ("quit", "exit"):
            return False
        elif cmd == "help":
            print(HELP)
        elif cmd == "show":
            print(message())
        elif cmd == "fault":
            key = re.sub(r"[^A-Z]", "", args[0].upper())
            if key not in FAULT_LOOKUP:
                print("Unknown fault. Use one of: " + " ".join(FAULTS))
                return True
            state["fault"] = FAULT_LOOKUP[key]
        elif cmd == "speed":
            stop_ramp()
            state["speed_hz"] = float(args[0])
        elif cmd in ("load", "loaded"):
            if args[0].lower() not in ("on", "off"):
                print("Use: load on   or   load off")
                return True
            state["loaded"] = args[0].lower() == "on"
        elif cmd == "temp":
            state["temperature_c"] = float(args[0])
        elif cmd == "ramp":
            start, end, secs = float(args[0]), float(args[1]), float(args[2])
            if secs <= 0:
                print("Seconds must be more than 0")
                return True
            start_ramp(start, end, secs)
        elif cmd == "demo":
            state["fault"] = "S-W"
            state["loaded"] = False
            start_ramp(30.0, 60.0, 10.0)
        elif cmd == "sim":
            if args[0].lower() == "on":
                start_sim()
            elif args[0].lower() == "off":
                stop_sim()
            else:
                print("Use: sim on   or   sim off")
                return True
        elif cmd == "gain":
            audio_gain = float(args[0])
        elif cmd == "data":
            p = Path(" ".join(args).strip('"'))
            if not p.is_dir():
                print("That folder does not exist.")
                return True
            data_dir = p
            print(f"Looking for csv files under {p.resolve()}")
        elif cmd == "play":
            await start_play(" ".join(args))
            return True
        elif cmd == "testwin":
            if args[0].lower() == "on":
                start_win(False)
            elif args[0].lower() == "fixed":
                start_win(True)
            elif args[0].lower() == "off":
                stop_win()
            else:
                print("Use: testwin on   or   testwin off   or   testwin fixed")
                return True
        elif cmd == "stop":
            stop_play()
            stop_sim()
            stop_win()
            stop_ramp()
        else:
            print("Unknown command. Type help.")
            return True
    except (IndexError, ValueError):
        print("Could not read that. Type help.")
        return True

    await broadcast()
    print(message())
    return True


async def main(host, port, heartbeat_seconds):
    loop = asyncio.get_running_loop()
    # compression off: the model windows are big, and squeezing them every 0.25 s only wastes time
    async with websockets.serve(handler, host, port, compression=None):
        print(f"Feed server running on ws://{host}:{port}")
        print(HELP)
        hb = asyncio.create_task(heartbeat(heartbeat_seconds))
        try:
            while True:
                line = await loop.run_in_executor(None, input, "> ")
                if not await run_command(line):
                    break
        except EOFError:
            pass
        finally:
            stop_ramp()
            stop_sim()
            stop_win()
            stop_play()
            hb.cancel()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--heartbeat", type=float, default=1.0, help="seconds between repeats of the current readings")
    ap.add_argument("--data", default=".", help="folder with the dataset csv files (sub folders are searched too)")
    a = ap.parse_args()
    data_dir = Path(a.data)
    try:
        asyncio.run(main(a.host, a.port, a.heartbeat))
    except KeyboardInterrupt:
        print("\nStopped")