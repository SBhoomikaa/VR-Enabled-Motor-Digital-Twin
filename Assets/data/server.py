"""
Motor feed server: sends readings, shake and sound to the Unity twin over a WebSocket.
Python is the server, Unity is the client.

Run:   pip install websockets
       python motor_feed_server.py
Unity connects to: ws://127.0.0.1:8765

Three kinds of message, each one line of JSON:
  readings  {"type": "readings", "fault": "S-W", "speed_hz": 45.0, "loaded": false, "temperature_c": 42.4}
            Fields not set yet are left out. Repeated every second so Unity knows we are alive.
  shake     {"type": "shake", "rate": 100, "x": [...], "y": [...], "z": [...]}
            Accelerometer values with the constant offset removed, 100 per second, 10 per message.
  audio     {"type": "audio", "rate": 16000, "pcm16": "<base64 of 16-bit sound>"}
            Microphone sound with the constant offset removed, 0.1 seconds per message.

Type "sim on" to stream made-up vibration and sound that follow the fault and speed.
A dataset player can later replace the made-up signals and use the same functions
(remove offset, average down to 100 per second, resample sound to 16000 per second).
"""
import argparse
import asyncio
import base64
import json
import math
import random
import re
import struct
import time

import websockets

# Dataset labels. The dataset has one bearing fault (F-B), not a DE and NDE version.
FAULTS = ["H-H", "R-U", "R-M", "B-R", "K-A", "S-W", "V-U", "F-B"]
FAULT_LOOKUP = {re.sub(r"[^A-Z]", "", f): f for f in FAULTS}   # "FB" -> "F-B"

SHAKE_RATE = 100        # shake values per second sent to Unity
AUDIO_RATE = 16000      # sound samples per second sent to Unity
BLOCK_SECONDS = 0.1     # how much data goes into each message
RAW_RATE = 1000         # rate of the pretend accelerometer in test mode

# Starting readings. Temperature stays out of the messages until you set it.
state = {"fault": "H-H", "speed_hz": 15.0, "loaded": False, "temperature_c": None}

clients = set()
ramp_task = None
sim_task = None
audio_gain = 5.0        # how much the sound is turned up before it is sent


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


# ---------- signal tools (the dataset player will use these too) ----------

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


# ---------- made-up signals for testing ----------

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
    if sim_task and not sim_task.done():
        return
    sim_task = asyncio.create_task(stream_loop())


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
  show                      print the current readings
  help, quit"""


async def run_command(line):
    """Returns False when it is time to quit."""
    global audio_gain
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
    async with websockets.serve(handler, host, port):
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
            hb.cancel()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--heartbeat", type=float, default=1.0, help="seconds between repeats of the current readings")
    a = ap.parse_args()
    try:
        asyncio.run(main(a.host, a.port, a.heartbeat))
    except KeyboardInterrupt:
        print("\nStopped")