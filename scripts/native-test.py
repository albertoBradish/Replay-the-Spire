"""Build and exercise a private copy of a Windows game installation with Steam disabled."""
import argparse
import hashlib
import json
import msvcrt
import os
from pathlib import Path
import re
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("game", type=Path)
parser.add_argument("phase", choices=["prepare", "record", "watch", "journey-record", "journey-watch", "video-room", "video-run", "video-cancel", "video-hd", "video-sync", "video-muted-mix", "video-muted", "timing", "timing-watch", "campaign-record", "campaign-watch", "ordinary-record", "ordinary-watch", "campaign-album", "campaign-film", "characters-record", "characters-watch", "modded-record", "modded-watch", "control", "choices-record", "choices-watch", "damage-record", "reward-record", "reward-watch", "facilities-record", "facilities-watch", "event-battle-record", "event-battle-watch", "event-grid-record", "event-grid-watch", "crystal-record", "crystal-watch", "courier-record", "courier-watch", "merchant-record", "merchant-watch", "resume-record", "resume-watch", "transfer", "foreign-transfer", "forum-album", "sample-album", "manual"])
parser.add_argument("--name", default="acceptance")
parser.add_argument("--candidate", type=Path, help="Stage and test this exact packaged plugin directory")
parser.add_argument("--extra-mod", type=Path, action="append", default=[], help="Copy an optional local mod into a new isolated test session")
parser.add_argument("--profile", default="", help="Independent user profile sharing the exact same staged game and plugins")
parser.add_argument("--gpu-index", type=int, help="Select and verify the Godot D3D12 renderer index")
parser.add_argument("--import-file", type=Path, help="Portable replay for the cross-profile transfer phase")
parser.add_argument("--no-build", action="store_true", help="Run the exact staged binaries again")
parser.add_argument("--refresh-plugin", action="store_true", help="Keep the staged native test driver and rebuild only the plugin")
args = parser.parse_args()
if args.gpu_index is not None and args.gpu_index < 0:
    parser.error("--gpu-index must be nonnegative")
if not args.name.replace("-", "").isalnum() or (args.profile and not args.profile.replace("-", "").isalnum()):
    raise SystemExit("Invalid test name")
game = args.game.resolve()
release = json.loads((game / "release_info.json").read_text(encoding="utf-8-sig"))
version = release["version"].lstrip("v")
if version not in ("0.111.1", "0.113.1"):
    raise SystemExit("Unsupported test build")
work = ROOT / ".local" / args.name / version
runtime = work / "runtime"
suffix = "-" + args.profile if args.profile else ""
evidence = work / ("evidence" + suffix)
evidence.mkdir(parents=True, exist_ok=True)
if runtime.resolve() == game or runtime.resolve().is_relative_to(game):
    raise SystemExit("The source game must be separate from the test copy")
# Only one native process uses the desktop for a test, even when separate invocations overlap.
lock = (ROOT / ".local/desktop.lock").open("a+b")
if lock.tell() == 0:
    lock.write(b"0")
    lock.flush()
lock.seek(0)
try:
    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
except OSError:
    raise SystemExit("Another isolated game test is running")
active = subprocess.check_output(["tasklist", "/FO", "CSV", "/NH"], errors="replace", text=True)
if any(row.lower().startswith('"slaythespire2.exe"') for row in active.splitlines()):
    raise SystemExit("A game instance is already running")
for file in game.rglob("*"):
    rel = file.relative_to(game)
    if rel.parts[0] in ("mods", "logs", "crashes") or rel.name == "override.cfg" or not file.is_file():
        continue
    target = runtime / rel
    if target.exists():
        continue
    target.parent.mkdir(parents=True, exist_ok=True)
    if file.suffix.lower() in (".dll", ".exe", ".pck"):
        try:
            os.link(file, target)
            continue
        except OSError:
            pass
    shutil.copy2(file, target)
profile_name = "ReplayAcceptance-" + version
(runtime / "override.cfg").write_text(
    '[application]\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="' + profile_name +
    '"\n[sentry]\nconfig/dsn=""\noptions/auto_init=false\n', encoding="utf-8")
env = os.environ.copy()
for key in ("APPDATA", "LOCALAPPDATA", "TEMP", "TMP"):
    loc = work / ("profile" + suffix) / key.lower()
    loc.mkdir(parents=True, exist_ok=True)
    env[key] = str(loc)
user = Path(env["APPDATA"]) / profile_name
profile = user / "default/1"
profile.mkdir(parents=True, exist_ok=True)
settings_file = profile / "settings.save"
settings = json.loads(settings_file.read_text(encoding="utf-8-sig")) if settings_file.exists() else {}
manual = args.phase == "manual"
extra_index = work / "extra-mods.json"
extra_ids = json.loads(extra_index.read_text(encoding="utf-8")) if extra_index.is_file() else []
if args.extra_mod:
    if extra_ids:
        raise SystemExit("Extras are already staged. Reuse them without --extra-mod, or choose a new session.")
    for item in args.extra_mod:
        source = item.resolve()
        candidates = [source / "mod_manifest.json", source / (source.name + ".json")]
        metadata = next((x for x in candidates if x.is_file()), None)
        if metadata is None:
            raise SystemExit("No installed mod manifest in " + str(source))
        identity = json.loads(metadata.read_text(encoding="utf-8-sig"))["id"]
        if identity in ("SpireReplay", "ReplayHarness", ".", "..") or not identity or any(
                not (c.isascii() and (c.isalnum() or c in "._-")) for c in identity):
            raise SystemExit("Invalid or reserved extra mod ID")
        destination = runtime / "mods" / identity
        if destination.exists():
            raise SystemExit("Extra mod destination exists: " + str(destination))
        shutil.copytree(source, destination)
        extra_ids.append(identity)
    extra_index.write_text(json.dumps(extra_ids), encoding="utf-8")
mods = list(extra_ids) + ["SpireReplay"]
# An existing recorded fixture can require this exact assembly fingerprint. In manual mode
# the driver stays inert because its activation variable is absent.
if not manual or (runtime / "mods/ReplayHarness/ReplayHarness.dll").is_file():
    mods.append("ReplayHarness")
settings.update(schema_version=8, seen_ea_disclaimer=True, skip_intro_logo=True, volume_master=0.0,
                fullscreen=False, window_size={"X": 1280, "Y": 720},
                language="zhs", mod_settings=dict(mods_enabled=True, mod_list=[
                    dict(id=n, is_enabled=True, source="mods_directory") for n in mods]))
settings_file.write_text(json.dumps(settings), encoding="utf-8")
if args.phase == "prepare":
    print(work)
    raise SystemExit(0)
data = game / "data_sts2_windows_x86_64"
build = work / "build"
target = "src/SpireReplay.csproj" if args.refresh_plugin or manual else "tests/NativeHarness/ReplayHarness.csproj"
if not args.no_build or args.refresh_plugin:
    subprocess.run(["dotnet", "build", str(ROOT / target), "-c", "Release", "--artifacts-path", str(build),
                    f"-p:GameDataDir={data}", f"-p:GameVersion={version}", "--nologo"], check=True)
for name, project in (("SpireReplay", ROOT / "src"), ("ReplayHarness", ROOT / "tests/NativeHarness")):
    if name not in mods or (manual and name == "ReplayHarness"):
        continue
    dest = runtime / "mods" / name
    dest.mkdir(parents=True, exist_ok=True)
    if (args.no_build and not args.refresh_plugin) or (args.refresh_plugin and name == "ReplayHarness"):
        if not (dest / (name + ".dll")).exists():
            raise SystemExit(f"Missing staged {name}; run record first")
        continue
    dll = build / "bin" / name / "release" / (name + ".dll")
    manifest = (ROOT if name == "SpireReplay" else project) / "mod_manifest.json"
    if name == "SpireReplay" and args.candidate:
        dll = args.candidate.resolve() / "SpireReplay.dll"
        manifest = dll.parent / "mod_manifest.json"
        if json.loads(manifest.read_text(encoding="utf-8-sig")).get("id") != "SpireReplay":
            raise SystemExit("The candidate is not the replay plugin.")
    shutil.copy2(dll, dest)
    shutil.copy2(manifest, dest)
env.update(SPIRE_REPLAY_TEST=str(evidence), SPIRE_REPLAY_PHASE=args.phase)
if args.phase in ("transfer", "foreign-transfer"):
    if args.import_file is None or not args.import_file.is_file() or not args.profile:
        raise SystemExit("For transfer supply --import-file and a fresh --profile name.")
    env["SPIRE_REPLAY_IMPORT_FILE"] = str(args.import_file.resolve())
if manual:
    env.pop("SPIRE_REPLAY_TEST")
argv = [str(runtime / "SlayTheSpire2.exe"), "--path", str(runtime), "--force-steam", "off",
        "--audio-driver", "Dummy", "--windowed", "--resolution", "1280x720", "--rendering-driver", "d3d12"]
if args.gpu_index is not None:
    argv.extend(["--gpu-index", str(args.gpu_index)])
def protected_files():
    files = (f for f in profile.rglob("*") if f.name not in ("settings.save", "settings.save.backup"))
    return {f.relative_to(profile).as_posix(): hashlib.sha256(f.read_bytes()).hexdigest()
            for f in files if f.is_file() and (f.suffix in (".save", ".run", ".mcr") or f.name.endswith(".save.backup"))}
# Native JSON is rewritten in canonical order at quit. Compare the actual values of
# existing settings, excluding the OS window position that the game saves at exit.
def preserved_settings():
    keys = ("volume_master", "volume_sfx", "volume_bgm", "volume_ambience", "language",
            "fullscreen", "window_size", "fps_limit", "vsync", "mod_settings", "controller_mapping", "keyboard_mapping")
    current = json.loads(settings_file.read_text(encoding="utf-8-sig"))
    return {k: current[k] for k in keys if k in current}

old_settings = preserved_settings()
before = protected_files()
before_bytes = {name: (profile / name).read_bytes() for name in before}

log = evidence / (args.phase + "-" + str(time.time_ns()) + ".log")
with log.open("wb") as stream:
    proc = subprocess.Popen(argv, cwd=runtime, env=env, stdout=stream, stderr=subprocess.STDOUT)
    print(f"pid={proc.pid} log={log}", flush=True)
    if manual:
        raise SystemExit(0)
    try:
        proc.wait(timeout=2700 if args.phase == "sample-album" or args.phase.startswith(("campaign-", "ordinary-")) else 900 if args.phase in ("video-run", "forum-album") else 260)
    except subprocess.TimeoutExpired:
        proc.terminate()
        proc.wait(timeout=15)
        raise
after = protected_files()
new_settings = preserved_settings()
changed_settings = [key for key, value in old_settings.items() if new_settings.get(key) != value]
diff = sorted(k for k in set(before) | set(after) if before.get(k) != after.get(k))
marker = "REPLAY_" + args.phase.upper().replace("-", "_") + "_PASS"
text = log.read_text(encoding="utf-8", errors="replace")
device = re.search(r"^D3D12[^\r\n]*Using Device #(\d+):\s*([^\r\n]+)", text, re.MULTILINE)
renderer = re.search(r"^Rendering device name:\s*([^\r\n]+)", text, re.MULTILINE)
gpu = dict(index=int(device[1]), name=(renderer[1] if renderer else device[2]).strip(), deviceLine=device[0].strip()) if device else None
if diff and args.phase != "record":
    comparison = evidence / (log.stem + "-profile")
    for name in diff:
        each = Path(name)
        for side, value in (("before", before_bytes.get(name)), ("after", (profile / name).read_bytes() if name in after else None)):
            if value is not None:
                saved = comparison / side / each
                saved.parent.mkdir(parents=True, exist_ok=True)
                saved.write_bytes(value)
report = dict(plugin_sha256=hashlib.sha256((runtime / "mods/SpireReplay/SpireReplay.dll").read_bytes()).hexdigest(),
              harness_sha256=hashlib.sha256((runtime / "mods/ReplayHarness/ReplayHarness.dll").read_bytes()).hexdigest(), game=version, phase=args.phase, game_commit=release["commit"], marker=marker in text,
              exit_code=proc.returncode, changed_profile_files=diff, changed_settings=changed_settings, gpu=gpu,
              protected_hashes=after, log=log.name, log_sha256=hashlib.sha256(log.read_bytes()).hexdigest())
(evidence / (args.phase + ".json")).write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, ensure_ascii=True))
if args.gpu_index is not None and (gpu is None or gpu["index"] != args.gpu_index):
    raise SystemExit(f"The game did not select requested GPU {args.gpu_index}: {gpu}")
if not report["marker"] or proc.returncode or "REPLAY_TEST_FAILURE" in text or ((args.phase.endswith("watch") or args.phase.startswith("video-") or args.phase in ("forum-album", "transfer", "foreign-transfer", "campaign-album", "campaign-film")) and (diff or changed_settings)):
    raise SystemExit(1)
