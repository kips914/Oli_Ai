"""Turns a Gradle log into GitHub check annotations (the only channel available here)."""
import pathlib
import re
import sys

text = pathlib.Path(sys.argv[1]).read_text(errors="replace")
lines = text.splitlines()
pattern = re.compile(r"^e: |error:|FAILURE:|What went wrong|> Task .* FAILED|Could not resolve|Caused by|Execution failed")
keep = set()
for i, line in enumerate(lines):
    if pattern.search(line):
        keep.update(range(i, min(len(lines), i + 4)))

body = "\n".join(lines[j] for j in sorted(keep))[:6000] or text[-6000:]
tail = "\n".join(lines[-30:])[:3000]


def emit(title, message):
    message = message.replace("%", "%25").replace("\r", "")
    message = message.replace("\n", "%0A")
    print(f"::error title={title}::{message}")


emit("Gradle build failed", body)
emit("Gradle tail", tail)
