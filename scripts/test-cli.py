import subprocess
import os
import sys

sys.stdout.reconfigure(encoding='utf-8')

script_dir = os.path.dirname(os.path.abspath(__file__))
project_root = os.path.abspath(os.path.join(script_dir, ".."))
samples_dir = os.path.join(project_root, "designs", "各个机型测试")
report_path = os.path.join(script_dir, "cli-test-report.md")
lpb_cmd = os.path.join(project_root, "lpb.cmd")

if not os.path.exists(samples_dir):
    print(f"Samples directory not found at {samples_dir}!")
    sys.exit(1)

# specific test files
single_photo = os.path.join(samples_dir, "华为-Mate80.jpg")
dual_photo = os.path.join(samples_dir, "苹果-双文件.JPG")
dual_video = os.path.join(samples_dir, "苹果-双文件.MOV")

commands_to_test = [
    ["--version"],
    ["--info"],
    ["protocols"],
    ["protocols", "--json"],
    ["merge", dual_photo, dual_video, "-p", "huawei", "-y", "--dry-run"],
    ["merge", "-d", samples_dir, "-p", "motionphoto", "-y", "--dry-run"],
    ["split", single_photo, "-y", "--dry-run"],
    ["split", "-d", samples_dir, "-y", "--dry-run"],
    ["repair", single_photo, "--dry-run"],
    ["repair", "-d", samples_dir, "-y", "--dry-run"],
    ["cover", single_photo, "--at", "2.5", "--dry-run"],
    ["cover", single_photo, "--frame", "10", "--dry-run"],
    ["update-check"]
]

with open(report_path, "w", encoding="utf-8") as f:
    f.write("# CLI Automated Test Report\n\n")
    for cmd_args in commands_to_test:
        f.write(f"## lpb {' '.join(cmd_args)}\n\n")
        print(f"Running: {' '.join(cmd_args)}")
        try:
            result = subprocess.run([lpb_cmd] + cmd_args, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=project_root)
            f.write(f"**Exit Code**: {result.returncode}\n\n")
            f.write(f"### Stdout\n`\n{result.stdout.strip()}\n`\n\n")
            if result.stderr.strip():
                f.write(f"### Stderr\n`\n{result.stderr.strip()}\n`\n\n")
        except Exception as e:
            f.write(f"**Exception**: {str(e)}\n\n")

print(f"\nAll tests completed. Report saved to {report_path}")
