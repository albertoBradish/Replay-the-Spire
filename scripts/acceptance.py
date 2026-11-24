"""Exercise a fixed native plugin/harness pair on one Windows game build."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("game", type=Path)
parser.add_argument("--name", required=True, help="New isolated session name")
parser.add_argument("--candidate", type=Path, help="Use the exact DLL from this packaged plugin directory")
parser.add_argument("--ffmpeg", type=Path, help="Enable media tests with this FFmpeg executable")
parser.add_argument("--foreign-replay", type=Path, help="Verify read-only browsing of a file recorded on a different game build")
parser.add_argument("--campaign", action="store_true", help="Also play and verify a complete native campaign")
parser.add_argument("--ordinary", action="store_true", help="Measure a standard Ironclad run through its natural outcome")
parser.add_argument("--campaign-media", action="store_true", help="Also export and inspect the full campaign album and movie")
parser.add_argument("--validator", type=Path, help="Optional full-log validator accepting LOG --require MARKER --output JSON")
args = parser.parse_args()
if not args.name.replace("-", "").isalnum():
    parser.error("Invalid session name")
game = args.game.resolve()
release = json.loads((game / "release_info.json").read_text(encoding="utf-8-sig"))
version = release["version"].lstrip("v")
work = ROOT / ".local" / args.name / version
if (work / "evidence/record.json").exists():
    parser.error("Use a new session name. Each full suite starts without any prior recordings.")
env = os.environ.copy()
if args.ffmpeg:
    if not args.ffmpeg.is_file():
        parser.error("FFmpeg executable does not exist")
    env["SPIRE_REPLAY_FFMPEG"] = str(args.ffmpeg.resolve())
results = []

def native(phase, *, profile="", extra=(), witness=False):
    cli = ROOT / ("tests/NativeHarness/sync_witness.py" if witness else "scripts/native-test.py")
    command = [sys.executable, "-X", "utf8", str(cli), str(game)]
    if not witness:
        command.append(phase)
    command.extend(["--name", args.name])
    if results:
        command.append("--no-build")
    elif args.candidate:
        command.extend(["--candidate", str(args.candidate.resolve())])
    if profile:
        command.extend(["--profile", profile])
    command.extend(extra)
    print(f"\n{version} / {phase}", flush=True)
    completed = subprocess.run(command, cwd=ROOT, env=env)
    directory = work / ("evidence-" + profile if profile else "evidence")
    summary = directory / (phase + ".json")
    report = json.loads(summary.read_text(encoding="utf-8")) if summary.is_file() else {}
    passed = completed.returncode == 0 and report.get("marker", False)
    log = report.get("log")
    if log and args.validator:
        verify = subprocess.run([sys.executable, "-X", "utf8", str(args.validator.resolve()), str(directory / log),
                                 "--require", "REPLAY_PHASE_PASS phase=" + phase.replace("-", "_"),
                                 "--output", str(directory / (Path(log).stem + ".validation.json"))],
                                cwd=ROOT, capture_output=True)
        passed = passed and verify.returncode == 0
    if results and report.get("plugin_sha256") != results[0]["plugin"]:
        passed = False
    results.append(dict(phase=phase, profile=profile, passed=passed, log=log, plugin=report.get("plugin_sha256")))
    (work / "suite.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    return passed

def record_and_watch(prefix):
    if native(prefix + "-record"):
        native("control")
        native(prefix + "-watch")

if not native("record"):
    raise SystemExit("Cannot continue without the first native recording.")
native("control")
native("watch")
if args.ffmpeg:
    for name in ("video-room", "video-hd", "video-cancel", "video-muted", "video-muted-mix"):
        native(name)
    native("video-sync", witness=True)
exports = work / "profile/appdata" / ("ReplayAcceptance-" + version) / "ReplayTheSpire/exports"
encoded = list(exports.glob("*.spireplay"))
if len(encoded) != 1:
    raise SystemExit("Expected exactly one basic replay for the cross-profile exchange.")
native("control", profile="receiver")
native("control", profile="receiver")
native("transfer", profile="receiver", extra=("--import-file", str(encoded[0])))
if args.foreign_replay:
    native("control", profile="foreign")
    native("control", profile="foreign")
    native("foreign-transfer", profile="foreign", extra=("--import-file", str(args.foreign_replay.resolve())))
# Continue the original native save before creating any unrelated fixture.
record_and_watch("resume")
native("damage-record")
if native("timing"):
    native("control")
    native("timing-watch")
for scope in ("choices", "facilities", "reward", "event-battle", "event-grid", "crystal", "courier", "characters", "journey"):
    record_and_watch(scope)
if any(r["phase"] == "journey-record" and r["passed"] for r in results):
    native("forum-album")
    if args.ffmpeg:
        native("video-run")
if args.campaign or args.campaign_media:
    record_and_watch("campaign")
    if args.campaign_media and any(r["phase"] == "campaign-watch" and r["passed"] for r in results):
        native("campaign-album")
        if args.ffmpeg:
            native("campaign-film")
if args.ordinary:
    record_and_watch("ordinary")
failed = [r["phase"] for r in results if not r["passed"]]
print(json.dumps(dict(game=version, phases=len(results), failed=failed, report=str(work / "suite.json")), ensure_ascii=True))
raise SystemExit(bool(failed))
