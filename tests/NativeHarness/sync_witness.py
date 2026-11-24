"""Run video-sync with an unrelated process playing 3700 Hz into the same virtual speakers."""
import argparse
import ctypes as ct
import json
import os
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
FREQUENCY = 3700


class Meter(ct.Structure):
    _fields_ = [("samples", ct.c_int), ("peak", ct.c_float * 32),
                ("rms", ct.c_float * 32), ("channels", ct.c_short)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game", type=Path)
    parser.add_argument("--name", default="acceptance")
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    game = args.game.resolve()
    if not args.name.replace("-", "").isalnum():
        parser.error("Invalid test name")
    version = json.loads((game / "release_info.json").read_text(encoding="utf-8-sig"))["version"].lstrip("v")
    if version not in ("0.111.1", "0.113.1"):
        parser.error("Unsupported game")
    fmod = ct.WinDLL(str(game / "fmod.dll"))

    def call(name, *values):
        result = getattr(fmod, "FMOD_" + name)(*values)
        if result:
            raise RuntimeError(f"FMOD {name} returned {result}")

    system, oscillator, channel = ct.c_void_p(), ct.c_void_p(), ct.c_void_p()
    call("System_Create", ct.byref(system), 0x00020306)
    try:
        count = ct.c_int()
        call("System_GetNumDrivers", system, ct.byref(count))
        matches = []
        for index in range(count.value):
            name = ct.create_string_buffer(512)
            call("System_GetDriverInfo", system, index, name, len(name), None, None, None, None)
            if b"steam streaming speakers" in name.value.lower():
                matches.append((index, name.value.decode("utf-8")))
        if len(matches) != 1:
            raise RuntimeError("A unique Steam Streaming Speakers virtual device is required")
        driver, name = matches[0]
        call("System_SetDriver", system, driver)
        call("System_Init", system, 8, 0, None)
        actual = ct.c_int()
        call("System_GetDriver", system, ct.byref(actual))
        if actual.value != driver:
            raise RuntimeError("Foreign signal must go only to the virtual output")
        call("System_CreateDSPByType", system, 2, ct.byref(oscillator))
        call("DSP_SetParameterFloat", oscillator, 1, ct.c_float(FREQUENCY))
        call("DSP_SetMeteringEnabled", oscillator, 0, 1)
        call("System_PlayDSP", system, oscillator, None, 1, ct.byref(channel))
        call("Channel_SetVolume", channel, ct.c_float(.3))
        call("Channel_SetPaused", channel, 0)
        call("System_Update", system)
        time.sleep(.25)

        def verify_tone():
            call("System_Update", system)
            meter = Meter()
            call("DSP_GetMeteringInfo", oscillator, None, ct.byref(meter))
            if meter.samples <= 0 or max(meter.rms) < .1:
                raise RuntimeError("The independent test tone was silent")
            return max(meter.rms)

        start_rms = verify_tone()
        command = [sys.executable, str(ROOT / "scripts/native-test.py"), str(game),
                   "video-sync", "--name", args.name]
        if args.no_build:
            command.append("--no-build")
        env = dict(os.environ, SPIRE_REPLAY_WITNESS_PID=str(os.getpid()))
        outcome = subprocess.run(command, env=env)
        end_rms = verify_tone()
        report = dict(pid=os.getpid(), frequency_hz=FREQUENCY, virtual_output=name,
                      oscillator_rms_before=start_rms, oscillator_rms_after=end_rms)
        evidence = ROOT / ".local" / args.name / version / "evidence"
        evidence.mkdir(parents=True, exist_ok=True)
        (evidence / "sync-witness.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        return outcome.returncode
    finally:
        if channel.value:
            fmod.FMOD_Channel_Stop(channel)
        if oscillator.value:
            fmod.FMOD_DSP_Release(oscillator)
        fmod.FMOD_System_Release(system)


if __name__ == "__main__":
    sys.exit(main())
