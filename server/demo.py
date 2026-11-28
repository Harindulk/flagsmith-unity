"""Plays a scripted Live Tuning sequence against the local demo, for recording a clip.

Start the game first (Play in Unity, or the WebGL build with ?autoplay), then run:
    python3 demo.py
Every change shows up in the running game within the 2-second refresh. Values are put back at the end.
"""
import time

from flag import set_flag, states

STEPS = [  # (seconds to wait first, flag, new value)
    (4, "run_speed", "11"),
    (5, "player_skin", "pink"),
    (5, "winter_event", "on"),
    (6, "spike_walls", "off"),
    (5, "coin_value", "5"),
    (6, "winter_event", "off"),
    (4, "run_speed", "7"),
]

if __name__ == "__main__":
    before = {name: s for name, s in states().items()}
    try:
        for wait, name, value in STEPS:
            time.sleep(wait)
            set_flag(name, value)
            print(f"{time.strftime('%H:%M:%S')}  {name} -> {value}")
        time.sleep(4)
    finally:
        for name in {n for _, n, _ in STEPS}:
            s = before[name]
            set_flag(name, ("on" if s["enabled"] else "off") if s["feature_state_value"] is None else str(s["feature_state_value"]))
            if s["feature_state_value"] is not None and not s["enabled"]:
                set_flag(name, "off")
        print("Restored the starting values.")
