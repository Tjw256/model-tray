"""Publish and verify using installed tools only. No model lifecycle operations."""
import datetime
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parent
commands = [
    ["dotnet", "build", "-c", "Release", "-v", "minimal"],
    ["dotnet", "bin/Release/net10.0-windows/LocalQwenTray.dll", "--self-test"],
    ["dotnet", "bin/Release/net10.0-windows/LocalQwenTray.dll", "--process-test"],
    ["dotnet", "bin/Release/net10.0-windows/LocalQwenTray.dll", "--ui-smoke-test"],
    ["dotnet", "publish", "-c", "Release", "-r", "win-x64", "--self-contained", "false", "-p:UseAppHost=true", "-o", "publish", "-v", "minimal"],
    ["dotnet", "publish/LocalQwenTray.dll", "--self-test"],
    [sys.executable, "verify_artifact.py"],
]
# Machine-specific checks (only present in the original author's setup) run when available.
for optional in ("test_client_context.py", "test_scripts.py"):
    if (root / optional).exists():
        commands.insert(4, [sys.executable, optional])
results = []
for command in commands:
    process = subprocess.run(command, cwd=root, capture_output=True, text=True, timeout=120)
    output = process.stdout + process.stderr
    print(output, end="" if output.endswith("\n") else "\n")
    results.append({"command": command, "exit_code": process.returncode, "passed_checks": sum(line.startswith("PASS ") for line in output.splitlines()), "output": output})
    if process.returncode:
        break
receipt = {"verified_at": datetime.datetime.now(datetime.timezone.utc).isoformat(), "results": results, "total_passed_checks": sum(x["passed_checks"] for x in results), "success": len(results) == len(commands) and all(x["exit_code"] == 0 for x in results)}
(root / "verification.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
print("Total passed checks:", receipt["total_passed_checks"])
raise SystemExit(0 if receipt["success"] else 1)
