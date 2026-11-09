#!/usr/bin/env python3
"""Run the movement calibration in the real game and compare it with the
simulator.

usage: movement_calibrate.py <ssmap.dll> <game_dir> <work_dir> [class]

  ssmap.dll  the built CLI (bin/Release/ssmap.dll)
  game_dir   a scratch copy of mod_tf (gameinfo.txt, bin -> mod_tf/bin) the
             game can run; the calibration map goes into its maps folder and
             the console script into its cfg folder
  work_dir   where the map, its compile, the game's console log and the
             comparison are written

Writes the calibration map and console script (ssmap movecal write),
compiles the map (ssmap all), runs the game with -condebug until the script
quits it, copies console.log into work_dir and prints ssmap movecal
compare's table. The exit code is compare's: 0 when every lane agrees.

Copy the console log under src/SourceSharp.Tests/Fixtures/movement-calibration/
to make it the recorded run the tests compare the simulator with.
"""
import os, shutil, subprocess, sys, time

SDK = os.path.expanduser("~/.steam/debian-installation/steamapps/common/Source SDK Base 2013 Multiplayer")


def run(args, **kw):
    print("+", " ".join(args), flush=True)
    return subprocess.run(args, **kw)


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    ssmap, game, work = (os.path.abspath(a) for a in sys.argv[1:4])
    cls = sys.argv[4] if len(sys.argv) > 4 else "soldier"
    os.makedirs(work, exist_ok=True)
    if run(["dotnet", ssmap, "movecal", "write", work, "-class", cls]).returncode:
        return 1
    vmf = os.path.join(work, "ss_movecal.vmf")
    with open(os.path.join(work, "compile.log"), "w") as log:
        if run(["dotnet", ssmap, "all", "-game", game, "-fast", "-no-nav", vmf], stdout=log, stderr=subprocess.STDOUT).returncode:
            print("compile failed; see compile.log")
            return 1

    shutil.copy(os.path.join(work, "ss_movecal.bsp"), os.path.join(game, "maps", "ss_movecal.bsp"))
    shutil.copy(os.path.join(work, "movecal.cfg"), os.path.join(game, "cfg", "movecal.cfg"))
    console = os.path.join(game, "console.log")
    if os.path.exists(console):
        os.remove(console)

    env = dict(os.environ)
    env["LD_LIBRARY_PATH"] = os.path.join(SDK, "bin", "linux64") + ":" + env.get("LD_LIBRARY_PATH", "")
    env["SDL_VIDEO_X11_WMCLASS"] = "sourcesharp-bg"
    args = [os.path.join(SDK, "hl2_linux64"), "-game", game, "-novid", "-windowed", "-w", "640", "-h", "480",
            "-insecure", "-hushsteam", "-nomessagebox", "-nocrashdialog", "-condebug", "-nojoy",
            # The frame cap cannot change once connected, and the script's
            # waits count frames: one frame a tick needs it set here.
            "+fps_max", "66", "+sv_lan", "1", "+map", "ss_movecal", "+exec", "movecal"]
    proc = subprocess.Popen(args, cwd=SDK, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env)
    deadline = time.time() + 900
    while proc.poll() is None and time.time() < deadline:
        time.sleep(2)
    if proc.poll() is None:
        proc.kill()
        print("the game did not quit in time; comparing what the log has")

    log = os.path.join(work, f"console-{cls}.log")
    shutil.copy(console, log)
    return run(["dotnet", ssmap, "movecal", "compare", log, "-class", cls]).returncode


if __name__ == "__main__":
    sys.exit(main())
