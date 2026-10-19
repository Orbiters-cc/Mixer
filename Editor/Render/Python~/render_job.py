"""A background Blender that renders the Unity scene: it connects to the editor's Mixer link with the Mixer extension,
waits for the scene and its meshes, renders the request and leaves. Run by Unity Mixer:
blender -b --python render_job.py -- <request.json> <endpoint.json> [extension source dir]"""
import importlib
import json
import os
import sys
import time
import traceback

import addon_utils
import bpy

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
request_path = args[0]
endpoint_path = args[1] if len(args) > 1 else ""
source_dir = args[2] if len(args) > 2 else ""


def log(text):
    print("[Mixer render] " + str(text))
    sys.stdout.flush()


def load_extension():
    """The installed Mixer extension, or the one in the source folder Unity names (development)."""
    if source_dir and os.path.isdir(source_dir):
        sys.path.insert(0, os.path.dirname(source_dir.rstrip("/\\")))
        module = importlib.import_module(os.path.basename(source_dir.rstrip("/\\")))
        module.register()
        return module
    for installed in addon_utils.modules(refresh=False):
        if installed.__name__.startswith("bl_ext.") and installed.__name__.rsplit(".", 1)[-1] == "orbiters_mixer":
            if addon_utils.enable(installed.__name__, default_set=False) is None:
                raise RuntimeError("Could not enable " + installed.__name__)
            return sys.modules[installed.__name__]
    raise RuntimeError("The Mixer extension is not installed in this Blender: open the scene in Blender from Mixer once.")


try:
    with open(request_path, "r", encoding="utf-8") as handle:
        request = json.load(handle)
    extension = load_extension()
    link = importlib.import_module(extension.__name__ + ".link")
    scene_sync = importlib.import_module(extension.__name__ + ".scene_sync")
    render_server = importlib.import_module(extension.__name__ + ".render_server")
    bpy.ops.wm.read_homefile(use_empty=True)
    settings = bpy.context.scene.orbiters_mixer
    with open(endpoint_path, "r", encoding="utf-8") as handle:
        endpoint = json.load(handle)
    settings.scene_guid = endpoint.get("sceneGuid", "")
    settings.endpoint_path = endpoint_path
    settings.auto_connect = True
    deadline = time.monotonic() + 600.0
    ready = False
    while time.monotonic() < deadline:
        wait = link.tick()
        state, _, _, messages, _ = link.state()
        if any("meshes done" in m for m in messages):
            ready = True
            break
        if state not in ("connecting", "connected") and time.monotonic() > deadline - 580:
            raise RuntimeError("Unity's link is not reachable: " + state)
        time.sleep(min(0.05, wait))
    if not ready:
        log("the scene did not finish arriving; rendering what came")
    log("scene has %d objects" % len([o for o in bpy.data.objects if scene_sync.is_unity_object(o)]))
    result = render_server.render(request)
    log(json.dumps(result))
    link.close("rendered")
    if not result.get("ok"):
        sys.exit(2)
except Exception:
    traceback.print_exc()
    sys.exit(1)
