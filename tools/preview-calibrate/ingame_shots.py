#!/usr/bin/env python3
"""Capture in-game TF2 screenshots at a preview's perspective cameras and
build side-by-side comparisons with the preview renders.

usage: ingame_shots.py <map.bsp> <preview_dir> <game_dir> <out_dir>

The preview dir must hold index.json and views/*.png (ssmap preview -individual).
The game dir is a scratch copy of mod_tf (gameinfo.txt, bin -> mod_tf/bin).
"""
import json, os, shutil, subprocess, sys, time, glob
from PIL import Image, ImageDraw

SDK = os.path.expanduser("~/.steam/debian-installation/steamapps/common/Source SDK Base 2013 Multiplayer")

def main():
    bsp, preview, game, out = sys.argv[1:5]
    name = os.path.splitext(os.path.basename(bsp))[0]
    shutil.copy(bsp, os.path.join(game, "maps", name + ".bsp"))
    report = json.load(open(os.path.join(preview, "index.json")))
    cams = [c for c in report["cameras"] if c.get("fov")]
    shots = os.path.join(game, "screenshots")
    for f in glob.glob(os.path.join(shots, "*")):
        os.remove(f)

    # Spectator free-roam: setpos puts the eye where the preview camera is.
    lines = ["cl_hud_playerclass_playermodel_showed_confirm_dialog 1", "cl_hud_playerclass_use_playermodel 0", "con_timestamp 1", "sv_allow_wait_command 1", "sv_cheats 1", "tf_arena_use_queue 0", "mp_teams_unbalance_limit 0", "mp_autoteambalance 0", "mp_forcecamera 0", "fps_max 30", "fov_desired 90", "mat_motion_blur_enabled 0"]
    # One alias per shot, each calling the next: a single console line is
    # limited in length, and a long map's camera list overflows it.
    for i, c in enumerate(cams):
        x, y, z = c["position"]
        nxt = f"s{i + 1}" if i + 1 < len(cams) else "quit"
        # Soldier's eye sits 68 units above his origin; setpos moves the origin.
        pitch, yaw = c["pitch"], c["yaw"]
        lines.append(f'alias s{i} "setpos {x} {y} {z - 68}; setang {pitch} {yaw} 0; wait 90; echo SHOT_{i}; screenshot; wait 30; {nxt}"')
    setup = ["echo SHOTS_BEGIN", "wait 600", "echo SHOTS_AFTERWAIT", "mp_waitingforplayers_cancel 1", "jointeam red", "wait 60",
             "joinclass soldier", "wait 150", "noclip", "cl_drawhud 0", "r_drawviewmodel 0", "wait 30", "s0"]
    lines.append("; ".join(setup))
    open(os.path.join(game, "cfg", "shots.cfg"), "w").write("\n".join(lines) + "\n")

    if os.path.exists(os.path.join(game, "console.log")):
        os.remove(os.path.join(game, "console.log"))
    exe = os.path.join(SDK, "hl2_linux64")
    args = [exe, "-game", os.path.abspath(game), "-novid", "-windowed", "-w", "1024", "-h", "768",
            "-insecure", "-hushsteam", "-nomessagebox", "-nocrashdialog", "-condebug", "-nojoy",
            "+sv_lan", "1", "+map", name, "+exec", "shots"]
    env = dict(os.environ)
    env["LD_LIBRARY_PATH"] = os.path.join(SDK, "bin", "linux64") + ":" + env.get("LD_LIBRARY_PATH", "")
    env["SDL_VIDEO_X11_WMCLASS"] = "sourcesharp-bg"
    proc = subprocess.Popen(args, cwd=SDK, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env)
    deadline = time.time() + 240
    while proc.poll() is None and time.time() < deadline:
        time.sleep(2)
    if proc.poll() is None:
        proc.kill()
    found = sorted(glob.glob(os.path.join(shots, "*.tga")) + glob.glob(os.path.join(shots, "*.jpg")))
    print(f"{len(found)} screenshots for {len(cams)} cameras")
    os.makedirs(out, exist_ok=True)
    for c, f in zip(cams, found):
        ingame = Image.open(f).convert("RGB").resize((1024, 768))
        ingame.save(os.path.join(out, f"ingame-{c['id']}.png"))
        mine = Image.open(os.path.join(preview, [i for i in report["images"] if c["id"] in i][0])).convert("RGB")
        both = Image.new("RGB", (2048 + 8, 768 + 28), (24, 26, 30))
        both.paste(mine, (0, 28)); both.paste(ingame, (1032, 28))
        d = ImageDraw.Draw(both)
        d.text((8, 8), f"PREVIEW  {c['id']}", fill=(255, 255, 255))
        d.text((1040, 8), f"IN GAME (mod_tf)  {c['id']}", fill=(255, 255, 255))
        both.save(os.path.join(out, f"compare-{c['id']}.png"))

if __name__ == "__main__":
    main()
