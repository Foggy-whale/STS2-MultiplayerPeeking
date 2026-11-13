#!/usr/bin/env python3
"""Run two actual macOS STS2 processes on localhost ENet; no Steam/profile writes."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_GAME = Path.home() / "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app"


def run(command, log, **kwargs):
    with log.open("w") as stream:
        subprocess.run([str(x) for x in command], stdout=stream, stderr=subprocess.STDOUT, check=True, **kwargs)


def inspect_logs(output):
    """Retain known native/ModConfig diagnostics explicitly; fail on other errors."""
    errors, notices = [], []
    headless = json.loads((output / "environment.json").read_text())["headless"]
    for role in ("host", "client"):
        finished = False
        lines = (output / (role + "-console.log")).read_text(errors="replace").splitlines()
        for index, line in enumerate(lines):
            if line.startswith(f"[PEEK-TEST] {role} PASS: ") and line.endswith("PASS: "):
                finished = True
            if not (line.startswith("ERROR:") or "[ERROR]" in line or "Unhandled exception" in line
                    or "State divergence" in line or "Desync detected" in line):
                continue
            known = (
                # ModConfig 0.1.3, on each settings screen creation.
                (line.startswith("ERROR: Attempt to disconnect a nonexistent connection")
                 and "'size_changed'" in line and "NSettingsPanel.cs)::RefreshSize" in line)
                # MegaDot 4.5.1 worker pool / background script loading diagnostic.
                or line == "ERROR: Invalid Task ID"
                # Only the dummy renderer's exact diagnostic in --headless mode.
                or (headless and line == 'ERROR: Parameter "t" is null.' and index + 1 < len(lines)
                    and "texture_2d_initialize (servers/rendering/dummy/storage/texture_storage.h:" in lines[index + 1])
                # Shutdown resource diagnostics are recorded, never accepted during a test.
                or (finished and re.fullmatch(
                    r"ERROR: (\d+ RID allocations of type '.+' were leaked at exit\."
                    r"|\d+ resources still in use at exit \(run with --verbose for details\)\."
                    r"|\d+ shaders of type \w+ were never freed)", line) is not None)
            )
            (notices if known else errors).append(role + ": " + line)
    (output / "engine-notices.json").write_text(json.dumps(notices, indent=2))
    if errors:
        raise AssertionError("Game logged unexpected errors:\n" + "\n".join(errors))
    return len(notices)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", type=Path, default=DEFAULT_GAME)
    parser.add_argument("--godot", type=Path, default=Path.home() / "Applications/Godot_mono.app/Contents/MacOS/Godot")
    parser.add_argument("--headless", action="store_true", help="Skip graphics/screenshots; retain scene/behavior assertions")
    parser.add_argument("--disable-peek-patch", action="store_true", help="Negative control: disable recognition of peeking selection screens; must fail")
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if sys.platform != "darwin":
        parser.error("This launcher currently supports macOS app bundles only.")
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 33771))  # Fail early instead of joining another test/game.
    contents = args.game.resolve() / "Contents"
    binary = contents / "MacOS/Slay the Spire 2"
    if not binary.is_file():
        parser.error(f"Game executable missing: {binary}")
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    output = (args.output or ROOT / "dist/multiplayer-tests" / stamp).resolve()
    output.mkdir(parents=True, exist_ok=False)
    # Prevent the Godot editor from importing test screenshots into its resource cache.
    (ROOT / "dist").mkdir(exist_ok=True)
    (ROOT / "dist/.gdignore").touch()
    print(f"Results: {output}", flush=True)
    processes, streams, profiles = [], [], []
    sandbox = Path(tempfile.mkdtemp(prefix="peeking-mp-"))
    try:
        data = contents / ("Resources/data_sts2_macos_arm64" if os.uname().machine == "arm64" else "Resources/data_sts2_macos_x86_64")
        # Match the main project's reference choice, even when running ARM game processes.
        build_data = contents / "Resources/data_sts2_macos_x86_64"
        if not build_data.is_dir():
            build_data = data
        run(["dotnet", "build", ROOT / "MultiplayerPeeking.csproj", "-c", "Release", f"-p:Sts2DataDir={build_data}",
             f"-p:GodotPath={args.godot}", "-p:DeployMod=false", "-p:NuGetAudit=false"], output / "build-mod.log", cwd=ROOT)
        run(["dotnet", "build", ROOT / "tests/multiplayer/MultiplayerPeekingTests.csproj", "-c", "Release", f"-p:GameData={data}", "-p:NuGetAudit=false"], output / "build-test.log", cwd=ROOT)
        pack = output / "MultiplayerPeeking.pck"
        run([args.godot, "--headless", "--path", ROOT, "--export-pack", "BasicExport", pack], output / "export.log",
            env={**os.environ, "DeployMod": "false", "IsInnerGodotExport": "true", "NuGetAudit": "false"})
        mod_dll = ROOT / ".godot/mono/temp/bin/Release/MultiplayerPeeking.dll"
        metadata = {"game": json.loads((contents / "Resources/release_info.json").read_text()),
                    "mod_sha256": hashlib.sha256(mod_dll.read_bytes()).hexdigest(), "headless": args.headless,
                    "negative_control": args.disable_peek_patch}
        (output / "environment.json").write_text(json.dumps(metadata, indent=2))
        modconfigs = list((contents / "MacOS/mods").glob("*/ModConfig.dll"))
        if len(modconfigs) != 1:
            raise RuntimeError(f"Expected one installed ModConfig.dll, found {len(modconfigs)}")
        deadline = time.monotonic() + args.timeout
        for index, role in enumerate(("host", "client")):
            app = sandbox / role / "SlayTheSpire2.app/Contents"
            macos = app / "MacOS"
            macos.mkdir(parents=True)
            shutil.copy2(binary, macos / binary.name)
            for name in ("Resources", "Frameworks", "Info.plist"):
                if (contents / name).exists():
                    (app / name).symlink_to(contents / name, target_is_directory=(contents / name).is_dir())
            profile_name = f"MultiplayerPeeking-Test-{sandbox.name}-{role}"
            profile = Path.home() / "Library/Application Support" / profile_name
            profiles.append(profile)
            (macos / "override.cfg").write_text('[application]\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="' + profile_name + '"\n')
            client_id = 113101 + index
            save = profile / f"default/{client_id}"
            save.mkdir(parents=True)
            settings = {"schema_version": 8, "fullscreen": False, "skip_intro_logo": True,
                        "seen_ea_disclaimer": True, "language": "eng", "fps_limit": 60,
                        "limit_fps_in_background": False, "volume_master": 0,
                        "window_size": {"X": 1280, "Y": 720},
                        "window_position": {"X": 40 + index * 600, "Y": 70 + index * 160},
                        "mod_settings": {"mods_enabled": True}}
            (save / "settings.save").write_text(json.dumps(settings))
            mods = macos / "mods"
            target = mods / "MultiplayerPeeking"
            target.mkdir(parents=True)
            for source in (mod_dll, ROOT / "MultiplayerPeeking.json", pack):
                shutil.copy2(source, target / source.name)
            target = mods / "ModConfig"
            target.mkdir()
            for ext in ("dll", "json", "pck"):
                shutil.copy2(modconfigs[0].with_suffix("." + ext), target)
            target = mods / "MultiplayerPeekingTests"
            target.mkdir()
            shutil.copy2(ROOT / "tests/multiplayer/bin/Release/net9.0/MultiplayerPeekingTests.dll", target)
            shutil.copy2(ROOT / "tests/multiplayer/MultiplayerPeekingTests.json", target)
            command = [str(macos / binary.name), "--force-steam=off", f"--clientId={client_id}",
                       "--fastmp=" + ("host_standard" if role == "host" else "join"), "--max-fps", "60",
                       "--peeking-test-role=" + role, "--peeking-test-output=" + str(output),
                       "--log-file", str(output / (role + "-godot.log"))]
            if args.headless:
                command.append("--headless")
            if args.disable_peek_patch:
                command.append("--peeking-test-disable-peek-patch=true")
            stream = (output / (role + "-console.log")).open("w")
            streams.append(stream)
            process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT, cwd=macos)
            processes.append(process)
            print(f"Started {role}, pid={process.pid}", flush=True)
            if role == "host":
                while not (output / "host.lobby").exists():
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("Host did not reach the ENet lobby; see host-console.log")
                    time.sleep(0.25)
        while any(p.poll() is None for p in processes):
            for role in ("host", "client"):
                result = output / (role + ".result.json")
                if result.exists() and not json.loads(result.read_text())["passed"]:
                    raise RuntimeError(f"{role} assertions failed; see {result}")
            if time.monotonic() > deadline:
                raise TimeoutError("Multiplayer test timed out; see game logs")
            time.sleep(0.25)
        results = [json.loads((output / (role + ".result.json")).read_text()) for role in ("host", "client")]
        if any(p.returncode != 0 for p in processes) or not all(r["passed"] for r in results):
            raise RuntimeError("A game process failed")
        if args.disable_peek_patch:
            raise AssertionError("Negative control unexpectedly passed; the regression was not detected")
        if results[0]["snapshots"] != results[1]["snapshots"]:
            raise AssertionError("Host/client gameplay snapshots differ")
        # A successful probe must not hide unhandled errors from the engine/game.
        notice_count = inspect_logs(output)
        summary = {"passed": True, "checks": {r["role"]: len(r["checks"]) for r in results},
                   "matching_snapshots": list(results[0]["snapshots"]), "engine_notice_count": notice_count, **metadata}
        (output / "summary.json").write_text(json.dumps(summary, indent=2))
        print(json.dumps(summary, indent=2), flush=True)
        return 0
    except Exception as exc:
        (output / "failure.txt").write_text(str(exc))
        print(f"FAIL: {exc}\nLogs: {output}", file=sys.stderr, flush=True)
        return 1
    finally:
        for process in processes:
            if process.poll() is None:
                process.terminate()
        for process in processes:
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        for stream in streams:
            stream.close()
        shutil.rmtree(sandbox)
        for profile in profiles:
            if profile.exists():
                # Keep diagnostic mod settings/logs, never real player profiles.
                shutil.copytree(profile, output / (profile.name.rsplit("-", 1)[-1] + "-test-profile"))
                shutil.rmtree(profile)


if __name__ == "__main__":
    sys.exit(main())
