"""Compare the standalone recording mod with a no-recorder control on the same native game.

Windows, Python 3.10+, .NET 9. Run on an idle desktop and leave the benchmark game
focused. Detailed per-arm data is private under .local, along with the isolated saves.
"""
import argparse
import csv
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import statistics
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
METRICS = ("p50Ms", "p95Ms", "p99Ms", "maxMs", "wallMs", "medianWorkingSetBytes", "peakWorkingSetBytes")


def write_json(path, obj):
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2), encoding="utf-8")


def install_mod(project, manifest, name, work, runtime, data, version, dotnet):
    output = work / "build" / name
    subprocess.run([dotnet, "build", str(project), "-c", "Release",
                    "--artifacts-path", str(output), f"-p:GameDataDir={data}",
                    f"-p:GameVersion={version}", "--nologo"], cwd=ROOT, check=True)
    # Select the runtime output; ref/ and refint/ contain reference-only assemblies.
    dll = next((output / "bin").glob(f"*/release/{name}.dll"))
    return stage_mod(dll, manifest, name, runtime)


def stage_mod(dll, manifest, name, runtime):
    if json.loads(manifest.read_text(encoding="utf-8-sig")).get("id") != name:
        raise ValueError(f"Unexpected mod identity in {manifest}")
    dest = runtime / "mods" / name
    dest.mkdir(parents=True, exist_ok=True)
    shutil.copy2(dll, dest)
    shutil.copy2(manifest, dest / "mod_manifest.json")
    return hashlib.sha256(dll.read_bytes()).hexdigest()


def renderer_evidence(log, expected_index):
    active = re.search(r"^D3D12[^\r\n]*?Using Device #(\d+):\s*([^\r\n]+)", log, re.MULTILINE)
    reported = re.search(r"^Rendering device name:\s*([^\r\n]+)", log, re.MULTILINE)
    if not active or not reported:
        raise RuntimeError("Game log did not identify the active D3D12 GPU")
    actual = int(active[1])
    if expected_index is not None and actual != expected_index:
        raise RuntimeError(f"Requested GPU {expected_index}, but game selected {actual}: {active[2]}")
    return dict(index=actual, name=reported[1].strip(), deviceLine=active.group().strip())


def parse_cpu_mask(value):
    try:
        mask = int(value, 0)
    except ValueError:
        raise argparse.ArgumentTypeError("Use a hexadecimal or decimal affinity mask") from None
    if mask < 1 or mask.bit_length() > ctypes.sizeof(ctypes.c_size_t) * 8:
        raise argparse.ArgumentTypeError("The affinity mask must fit a nonzero native pointer-sized integer")
    return mask


def run_with_affinity(argv, mask, timeout, **popen_options):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    bits = ctypes.c_size_t
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.SetProcessAffinityMask.argtypes = [wintypes.HANDLE, bits]
    kernel.GetProcessAffinityMask.argtypes = [wintypes.HANDLE, ctypes.POINTER(bits), ctypes.POINTER(bits)]

    def read(handle):
        effective, group = bits(), bits()
        if not kernel.GetProcessAffinityMask(handle, ctypes.byref(effective), ctypes.byref(group)):
            raise ctypes.WinError(ctypes.get_last_error())
        return effective.value, group.value

    with subprocess.Popen(argv, **popen_options) as child:
        handle = None
        try:
            # Only this invocation's child gets an affinity change. The probe begins sampling
            # in the native main menu, well after this immediate startup readback.
            handle = kernel.OpenProcess(0x0200 | 0x1000, False, child.pid)  # SET_INFORMATION | QUERY_LIMITED_INFORMATION
            if not handle:
                raise ctypes.WinError(ctypes.get_last_error())
            inherited, group = read(handle)
            if mask & ~inherited or mask & ~group:
                raise ValueError(f"Requested {mask:#x} is outside child mask {inherited:#x} or group mask {group:#x}")
            if not kernel.SetProcessAffinityMask(handle, mask):
                raise ctypes.WinError(ctypes.get_last_error())
            checks = 0
            deadline = time.monotonic() + timeout
            while True:
                try:
                    effective, group = read(handle)
                except OSError:
                    if checks and child.poll() is not None:
                        break
                    raise
                if effective != mask:
                    raise RuntimeError(f"Expected child CPU mask {mask:#x}, observed {effective:#x}")
                checks += 1
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise subprocess.TimeoutExpired(argv, timeout)
                try:
                    child.wait(timeout=min(1.0, remaining))
                    break
                except subprocess.TimeoutExpired:
                    pass
            return child.returncode, dict(requestedMask=hex(mask), actualMask=hex(effective),
                                          logicalProcessors=effective.bit_count(), processorGroupMask=hex(group),
                                          readbackChecks=checks)
        finally:
            if handle:
                kernel.CloseHandle(handle)
            if child.poll() is None:
                child.kill()
                child.wait()


def run_arm(work, runtime, profile_name, key, arm, gpu_index, cpu_mask):
    sample = work / key
    sample.mkdir()
    env = os.environ.copy()
    for var in ("APPDATA", "LOCALAPPDATA", "TEMP", "TMP"):
        folder = sample / var.lower()
        folder.mkdir()
        env[var] = str(folder)
    profile = Path(env["APPDATA"]) / profile_name / "default" / "1"
    profile.mkdir(parents=True)
    mods = [dict(id="ReplayPerf", is_enabled=True, source="mods_directory"),
            dict(id="SpireReplay", is_enabled=(arm == "on"), source="mods_directory")]
    write_json(profile / "settings.save",
               dict(schema_version=8, seen_ea_disclaimer=True, skip_intro_logo=True, volume_master=0.0,
                    fullscreen=False, window_size={"X": 1280, "Y": 720},
                    language="zhs", mod_settings=dict(mods_enabled=True, mod_list=mods)))
    output = sample / "frames.json"
    env.update(SPIRE_REPLAY_PERF=arm, SPIRE_REPLAY_PERF_OUTPUT=str(output))
    log = sample / "game.log"
    args = [str(runtime / "SlayTheSpire2.exe"), "--path", str(runtime), "--force-steam", "off",
            "--audio-driver", "Dummy", "--windowed", "--resolution", "1280x720", "--max-fps", "60",
            "--rendering-driver", "d3d12", "--log-file", str(log)]
    if gpu_index is not None:
        args.extend(["--gpu-index", str(gpu_index)])
    print(f"{key}: {arm} …", flush=True)
    with (sample / "process.log").open("w", encoding="utf-8") as console:
        options = dict(cwd=runtime, env=env, stdout=console, stderr=subprocess.STDOUT)
        if cpu_mask is None:
            exit_code = subprocess.run(args, **options, timeout=240).returncode
        else:
            exit_code, affinity = run_with_affinity(args, cpu_mask, 240, **options)
    notes = log.read_text(encoding="utf-8", errors="replace") if log.is_file() else ""
    if exit_code or "REPLAY_PERF_PASS" not in notes or "REPLAY_PERF_FAILURE" in notes or not output.exists():
        raise RuntimeError(f"Native performance trial failed. See {log}")
    trial = json.loads(output.read_text(encoding="utf-8"))
    trial["gpu"] = renderer_evidence(notes, gpu_index)
    if cpu_mask is not None:
        trial["cpuAffinity"] = affinity
    write_json(output, trial)
    return trial


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game", type=Path, help="read-only local game folder")
    parser.add_argument("--pairs", type=int, default=2)
    parser.add_argument("--candidate", type=Path, help="Measure the exact DLL from this packaged plugin directory")
    parser.add_argument("--gpu-index", type=int, help="Godot D3D12 adapter index (see the game log)")
    parser.add_argument("--cpu-mask", type=parse_cpu_mask, help="Windows affinity mask for each test game, e.g. 0x30000")
    parser.add_argument("--dotnet", default="dotnet", help="path to a .NET 9 SDK executable")
    args = parser.parse_args()
    if os.name != "nt" or not 1 <= args.pairs <= 10:
        parser.error("Run on Windows with 1 to 10 pairs")
    if args.gpu_index is not None and args.gpu_index < 0:
        parser.error("--gpu-index must be nonnegative")
    processes = subprocess.check_output(["tasklist", "/FO", "CSV", "/NH"], text=True, errors="replace")
    if any(row and row[0].lower() == "slaythespire2.exe" for row in csv.reader(processes.splitlines())):
        parser.error("Close the running game to measure one instance at a time")
    game = args.game.resolve()
    release = json.loads((game / "release_info.json").read_text(encoding="utf-8-sig"))
    version = release["version"].lstrip("v")
    name = "perf-" + uuid.uuid4().hex[:10]
    # Reuse the isolated, no-Steam copy procedure, then give every measured process a fresh profile.
    subprocess.run([sys.executable, str(ROOT / "scripts" / "native-test.py"), str(game),
                    "prepare", "--name", name], cwd=ROOT, check=True)
    work = ROOT / ".local" / name / version
    runtime = work / "runtime"
    data = game / "data_sts2_windows_x86_64"
    if args.candidate:
        package = args.candidate.resolve()
        mod_hash = stage_mod(package / "SpireReplay.dll", package / "mod_manifest.json", "SpireReplay", runtime)
    else:
        mod_hash = install_mod(ROOT / "src" / "SpireReplay.csproj", ROOT / "mod_manifest.json",
                               "SpireReplay", work, runtime, data, version, args.dotnet)
    probe_hash = install_mod(HERE / "ReplayPerf.csproj", HERE / "mod_manifest.json",
                             "ReplayPerf", work, runtime, data, version, args.dotnet)
    profile_name = "ReplayAcceptance-" + version
    trials = []
    reference = None
    gpu = None
    measured_render = None
    for i in range(args.pairs):
        pair = {}
        for arm in (("off", "on") if i % 2 == 0 else ("on", "off")):
            sample = run_arm(work, runtime, profile_name, f"pair-{i+1}-{arm}", arm, args.gpu_index, args.cpu_mask)
            if gpu is not None and gpu != sample["gpu"]:
                raise RuntimeError("Different rendering devices across benchmark trials")
            gpu = sample["gpu"]
            if sample["windowSize"] != [1280, 720] or min(sample["renderSize"]) <= 0:
                raise RuntimeError("Unexpected display dimensions")
            if measured_render is not None and sample["renderSize"] != measured_render:
                raise RuntimeError("Actual viewport dimensions changed between benchmark trials")
            measured_render = sample["renderSize"]
            identity = (sample["decisions"], sample["signature"])
            if reference is None:
                reference = identity
            if identity != reference:
                raise RuntimeError("The decision path or resulting state differed; see the raw trials")
            pair[arm] = sample
        trials.append(pair)
    summary = {}
    for stage in trials[0]["off"]["stages"]:
        summary[stage] = {}
        for metric in METRICS:
            diffs = [p["on"]["stages"][stage][metric] - p["off"]["stages"][stage][metric] for p in trials
                     if all(p[a]["stages"][stage][metric] is not None for a in ("off", "on"))]
            summary[stage][metric] = statistics.median(diffs) if diffs else None
    focus = [p[a]["stages"][s]["focusRatio"] for p in trials for a in ("off", "on") for s in ("idle", "combat")]
    result = dict(game=release, host=platform.platform(), pairs=args.pairs, windowSize=[1280, 720], renderSize=measured_render, maxFps=60,
                  driver="d3d12", gpu=gpu, seed="REPLAYPERF", decisionPath=reference[0], signature=reference[1],
                  modSha256=mod_hash, probeSha256=probe_hash, minimumFocusRatio=min(focus),
                  archiveBytesOn=[p["on"]["archiveBytes"] for p in trials], medianPairedDelta=summary)
    if args.cpu_mask is not None:
        observed = {(a["cpuAffinity"]["actualMask"], a["cpuAffinity"]["logicalProcessors"])
                    for pair in trials for a in pair.values()}
        if len(observed) != 1:
            raise RuntimeError("CPU affinity differed across benchmark arms")
        actual_mask, count = observed.pop()
        result["cpuAffinity"] = dict(requestedMask=hex(args.cpu_mask), actualMask=actual_mask,
                                     logicalProcessors=count, readbackChecksPerPair=[
                                         {arm: a["cpuAffinity"]["readbackChecks"] for arm, a in pair.items()} for pair in trials])
    write_json(work / "comparison.json", result)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    print(work / "comparison.json")


if __name__ == "__main__":
    main()
