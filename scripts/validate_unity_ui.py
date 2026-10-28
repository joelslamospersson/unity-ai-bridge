#!/usr/bin/env python3
"""Run rendered Unity UI regressions in a disposable project.

Uses only Python's standard library. The interaction suite sends pointer events
through Unity's rendered UI Toolkit panel and verifies production handlers.
No API credentials, paid inference or gameplay data are needed.
"""
import argparse
import json
from pathlib import Path
import subprocess
import time


def validate(unity, project, mode):
    artifacts = project / "Library/UnityAIBridge"
    artifacts.mkdir(parents=True, exist_ok=True)
    result = artifacts / ("chat-layout-result.json" if mode == "layout" else "interaction-result.json")
    result.unlink(missing_ok=True)
    method = "RunChatLayoutValidation" if mode == "layout" else "RunInteractionValidation"
    log = artifacts / (mode + "-validation.log")
    process = subprocess.Popen([str(unity), "-projectPath", str(project), "-batchmode", "-executeMethod",
                                "UnityAIBridge.SelfTests." + method, "-logFile", str(log)],
                               stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    deadline = time.monotonic() + 180
    try:
        while process.poll() is None and time.monotonic() < deadline:
            time.sleep(0.1)
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
            raise RuntimeError("Unity validation exceeded 180 seconds; see " + str(log))
        if not result.exists():
            raise RuntimeError("Unity produced no validation result; see " + str(log))
        report = json.loads(result.read_text())
        print(json.dumps({"mode": mode, **report}), flush=True)
        if not report.get("ok") or process.returncode:
            raise RuntimeError("UI validation failed; see " + str(log))
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=10)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", required=True, type=Path)
    parser.add_argument("--project", required=True, type=Path, help="Disposable Unity project containing this bridge; never use your working game project.")
    parser.add_argument("--mode", choices=["all", "layout", "interaction"], default="all")
    args = parser.parse_args()
    for mode in (["layout", "interaction"] if args.mode == "all" else [args.mode]):
        validate(args.unity.resolve(), args.project.resolve(), mode)


if __name__ == "__main__":
    main()
