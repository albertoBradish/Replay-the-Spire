"""Measure a native 720p floor export on Windows. Creates a record fixture if needed.

python tests/Performance/export.py GAME --gpu-index 1 --name export-cost
Set SPIRE_REPLAY_FFMPEG to the FFmpeg executable used for native acceptance.
"""
import argparse
import ctypes as ct
from ctypes import wintypes as wt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
INTERVAL = .05


class ProcessEntry(ct.Structure):
    _fields_ = [(k, wt.DWORD) for k in ("size", "usage", "pid")] + [
        ("heap", ct.c_size_t), ("module", wt.DWORD), ("threads", wt.DWORD), ("parent", wt.DWORD),
        ("priority", wt.LONG), ("flags", wt.DWORD), ("name", wt.WCHAR * 260)]


class Memory(ct.Structure):
    _fields_ = [("size", wt.DWORD), ("faults", wt.DWORD)] + [
        (k, ct.c_size_t) for k in ("peak", "rss", "a", "b", "c", "d", "pagefile", "peakPagefile")]

class MachineMemory(ct.Structure):
    _fields_ = [("size", wt.DWORD), ("load", wt.DWORD)] + [
        (k, ct.c_ulonglong) for k in ("total", "available", "swap", "freeSwap", "virtual", "freeVirtual", "extended")]


def windows_api():
    lib = ct.WinDLL("kernel32", use_last_error=True)
    definitions = {
        "CreateToolhelp32Snapshot": (wt.HANDLE, [wt.DWORD, wt.DWORD]),
        "Process32FirstW": (wt.BOOL, [wt.HANDLE, ct.POINTER(ProcessEntry)]),
        "Process32NextW": (wt.BOOL, [wt.HANDLE, ct.POINTER(ProcessEntry)]),
        "OpenProcess": (wt.HANDLE, [wt.DWORD, wt.BOOL, wt.DWORD]),
        "K32GetProcessMemoryInfo": (wt.BOOL, [wt.HANDLE, ct.POINTER(Memory), wt.DWORD]),
        "GetProcessTimes": (wt.BOOL, [wt.HANDLE] + [ct.POINTER(wt.FILETIME)] * 4),
        "GetExitCodeProcess": (wt.BOOL, [wt.HANDLE, ct.POINTER(wt.DWORD)]),
        "GlobalMemoryStatusEx": (wt.BOOL, [ct.POINTER(MachineMemory)]),
        "CloseHandle": (wt.BOOL, [wt.HANDLE]),
        "CreateFileW": (wt.HANDLE, [wt.LPCWSTR, wt.DWORD, wt.DWORD, wt.LPVOID, wt.DWORD, wt.DWORD, wt.HANDLE]),
        "GetFileSizeEx": (wt.BOOL, [wt.HANDLE, ct.POINTER(ct.c_longlong)]),
    }
    for name, (result, params) in definitions.items():
        call = getattr(lib, name)
        call.restype, call.argtypes = result, params
    return lib


def encoders(lib, game):
    snap = lib.CreateToolhelp32Snapshot(2, 0)
    if snap == ct.c_void_p(-1).value:
        raise ct.WinError(ct.get_last_error())
    result = []
    try:
        entry = ProcessEntry(size=ct.sizeof(ProcessEntry))
        ok = lib.Process32FirstW(snap, ct.byref(entry))
        while ok:
            if entry.parent == game and entry.name.lower() == "ffmpeg.exe":
                result.append(entry.pid)
            ok = lib.Process32NextW(snap, ct.byref(entry))
    finally:
        lib.CloseHandle(snap)
    return result


def process_sample(lib, handle):
    times = [wt.FILETIME() for _ in range(4)]
    if not lib.GetProcessTimes(handle, *(ct.byref(t) for t in times)):
        raise ct.WinError(ct.get_last_error())
    cpu = sum(t.dwLowDateTime + (t.dwHighDateTime << 32) for t in times[2:]) / 10_000_000
    memory = Memory(size=ct.sizeof(Memory))
    ok = lib.K32GetProcessMemoryInfo(handle, ct.byref(memory), memory.size)
    return cpu, memory.rss if ok else 0


def measure(pid, folder):
    lib = windows_api()
    game = lib.OpenProcess(0x410, False, pid)
    if not game:
        raise ct.WinError(ct.get_last_error())
    children, files = {}, {}
    observed_names = set()
    samples = []
    previous = None
    deadline = time.monotonic() + 170
    try:
        machine = MachineMemory(size=ct.sizeof(MachineMemory))
        if not lib.GlobalMemoryStatusEx(ct.byref(machine)): raise ct.WinError(ct.get_last_error())
        while time.monotonic() < deadline:
            now = time.monotonic()
            cpu, rss = process_sample(lib, game)
            dirs = list(folder.glob(".video-*"))
            if not dirs:
                if samples:
                    break
                status = wt.DWORD()
                if lib.GetExitCodeProcess(game, ct.byref(status)) and status.value != 259:
                    raise RuntimeError("Game exited without starting an export")
                previous = (now, cpu, rss)
            else:
                if len(dirs) != 1: raise RuntimeError("Expected only one export staging folder")
                for child in encoders(lib, pid):
                    if child not in children:
                        handle = lib.OpenProcess(0x410, False, child)
                        if handle: children[child] = handle
                encoder_rss = sum(process_sample(lib, h)[1] for h in children.values())
                stage_bytes = 0
                for path in dirs[0].glob("*"):
                    observed_names.add(path.name)
                    try: stage_bytes += path.stat().st_size
                    except FileNotFoundError: continue
                    if path.name not in files:
                        # Share read/write/delete so the actual mux/atomic publish/cleanup can proceed.
                        handle = lib.CreateFileW(str(path), 0x80, 7, None, 3, 0, None)
                        if handle and handle != ct.c_void_p(-1).value: files[path.name] = handle
                samples.append((now, cpu, rss, encoder_rss, stage_bytes))
            time.sleep(INTERVAL)
        else: raise TimeoutError("Export staging folder did not enter and leave in time.")
        if previous is None or not samples or not children:
            raise RuntimeError("Unable to establish game baseline or observe encoder during native export")
        final_lengths = {}
        for name, handle in files.items():
            size = ct.c_longlong()
            if lib.GetFileSizeEx(handle, ct.byref(size)): final_lengths[name] = size.value
        return dict(sampleIntervalMs=1000*INTERVAL, samples=len(samples),
                    maxObservedSampleGapMs=max((1000*(b[0]-a[0]) for a,b in zip(samples, samples[1:])), default=0),
                    machinePhysicalMemoryBytes=machine.total, observedStageFiles=sorted(observed_names),
                    observedWallSeconds=now-previous[0], preExportGameWorkingSetBytes=previous[2],
                    peakGameWorkingSetBytes=max(s[2] for s in samples),
                    peakEncoderWorkingSetBytes=max(s[3] for s in samples),
                    peakCombinedWorkingSetBytes=max(s[2]+s[3] for s in samples),
                    gameCpuSeconds=cpu-previous[1],
                    encoderCpuSeconds=sum(process_sample(lib,h)[0] for h in children.values()),
                    observedEncoderPids=sorted(children), sampledPeakStageBytes=max(s[4] for s in samples),
                    finalStagedFileBytes=final_lengths)
    finally:
        for handle in [game, *children.values(), *files.values()]: lib.CloseHandle(handle)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game", type=Path)
    parser.add_argument("--name", default="export-cost")
    parser.add_argument("--gpu-index", type=int, required=True)
    parser.add_argument("--no-build", action="store_true", help="Use the already staged production and harness DLLs")
    args = parser.parse_args()
    if os.name != "nt" or not args.name.replace("-", "").isalnum() or args.gpu_index < 0:
        parser.error("Requires Windows, valid test name and nonnegative GPU index")
    if not Path(os.environ.get("SPIRE_REPLAY_FFMPEG", "")).is_file():
        parser.error("Set SPIRE_REPLAY_FFMPEG to a working FFmpeg binary")
    game = args.game.resolve()
    version = json.loads((game/"release_info.json").read_text(encoding="utf-8-sig"))["version"].lstrip("v")
    work = ROOT / ".local" / args.name / version
    library = work / "profile/appdata" / ("ReplayAcceptance-"+version) / "ReplayTheSpire"
    folder = library / "exports/video"
    tool = [sys.executable, str(ROOT/"scripts/native-test.py"), str(game)]
    flags = ["--name", args.name, "--gpu-index", str(args.gpu_index)]
    if not any(json.loads(p.read_text(encoding="utf-8")).get("Seed") == "REPLAYPROOF" for p in library.glob("*/run.json")):
        subprocess.run(tool + ["record", *flags] + (["--no-build"] if args.no_build else []), check=True)
    if any(folder.glob(".video-*")): raise RuntimeError("Staging folder already exists before this measurement")
    before = set(folder.glob("*.mp4"))
    process = subprocess.Popen(tool+["video-room", *flags, "--no-build" if args.no_build else "--refresh-plugin"],
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace")
    try:
        for line in process.stdout:
            print(line.rstrip(), flush=True)
            match = re.match(r"pid=(\d+) log=", line)
            if match: break
        else: raise RuntimeError("Native test did not start a game")
        result = measure(int(match[1]), folder)
    finally:
        # The native test owns the game's timeout and protected-save checks.
        print(process.communicate()[0], end="")
    evidence = work/"evidence"
    native = json.loads((evidence/"video-room.json").read_text(encoding="utf-8"))
    log = (evidence/native["log"]).read_text(encoding="utf-8", errors="replace")
    counter = re.search(r"VIDEO_FRAMES captured=(\d+) total=(\d+) source=(\d+x\d+) output=(\d+x\d+)", log)
    if not counter: raise RuntimeError("Staged mod lacks completed-export frame counters")
    captured, total = map(int, counter.groups()[:2])
    if not 1 <= captured <= total or counter[3] != "1280x720" or counter[4] != "1280x720":
        raise RuntimeError("Expected positive captured/total and native 1280x720 source/output: "+counter[0])
    output, = set(folder.glob("*.mp4")) - before
    hasher = hashlib.sha256()
    with output.open("rb") as source:
        for chunk in iter(lambda: source.read(1024*1024), b""): hasher.update(chunk)
    result.update(gpu=native["gpu"], game=version, nativeAccepted=process.returncode == 0, source=counter[3], output=counter[4], outputPath=str(output),
                  nativeLog=native["log"], framesCaptured=captured, framesWritten=total, framesRepeated=total-captured,
                  repeatPercent=round(100*(total-captured)/total, 3),
                  videoSeconds=total/30, outputBytes=output.stat().st_size, outputSha256=hasher.hexdigest())
    # The publishable file is an atomic move of ready.mp4. A fast remux can fit between polls.
    result["finalStagedFileBytes"]["ready.mp4"] = result["outputBytes"]
    missing = set(result["observedStageFiles"]) - result["finalStagedFileBytes"].keys()
    result["finalStageBytesBeforePublish"] = None if missing else sum(result["finalStagedFileBytes"].values())
    (evidence/"export-workload.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))
    if process.returncode: raise RuntimeError("Native acceptance failed; see the native report and protected-file diff")


if __name__ == "__main__":
    main()
