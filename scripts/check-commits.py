"""Check Conventional Commit subjects for a Git revision range (or the current branch)."""
import argparse
import re
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("end", nargs="?", default="HEAD")
parser.add_argument("--base", help="Exclude history already reachable from this commit")
args = parser.parse_args()
end = subprocess.check_output(["git", "rev-parse", "--verify", "--end-of-options", args.end + "^{commit}"], text=True).strip()
commits = ["git", "log", "--no-merges", "--format=%h%x09%s", end]
if args.base and args.base.strip("0"):
    base = subprocess.check_output(["git", "rev-parse", "--verify", "--end-of-options", args.base + "^{commit}"], text=True).strip()
    commits.extend(["--not", base])
subjects = subprocess.check_output(commits, text=True, encoding="utf-8").splitlines()
pattern = re.compile(r"[a-z][a-z0-9-]*(?:\([^()\r\n]+\))?!?: \S.*")
bad = [line for line in subjects if not pattern.fullmatch(line.partition("\t")[2])]
if bad:
    raise SystemExit("Use type(scope): description\n" + "\n".join(bad))
print(f"Conventional Commits: {len(subjects)} checked")
