"""Copies the vanilla District Crossing models out of the local Timberborn install into Art/vanilla/.

The caravan post model is built on top of them by build_caravan_models.py. The extracted files are game assets:
they stay local (git-ignored) and are only used as build input.

  pip install UnityPy
  python Art/extract_vanilla_models.py [--game-dir "<...>/steamapps/common/Timberborn"]
"""
import argparse
import os
import struct

import UnityPy

MODELS = [f"DistrictCrossing.{faction}{stage}.Model"
          for faction in ("Folktails", "IronTeeth") for stage in ("", ".ConstructionStage0")]
DEFAULT_GAME_DIR = r"C:\Program Files (x86)\Steam\steamapps\common\Timberborn"


def timbermesh_bytes(objects, game_object):
    """The model GameObject carries a MonoBehaviour whose serialized byte[] is the zlib-compressed timbermesh."""
    for component in game_object.m_Component:
        pointer = component.component if hasattr(component, "component") else component[1]
        obj = objects.get(pointer.path_id)
        if obj is None or obj.type.name != "MonoBehaviour":
            continue
        raw = obj.get_raw_data()
        length = struct.unpack_from("<i", raw, 32)[0]
        if length > 0 and raw[36:38] == b"\x78\x9c":
            return raw[36:36 + length]
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-dir", default=DEFAULT_GAME_DIR)
    parser.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "vanilla"))
    args = parser.parse_args()
    env = UnityPy.load(os.path.join(args.game_dir, "Timberborn_Data", "resources.assets"))
    os.makedirs(args.out, exist_ok=True)
    objects = {obj.path_id: obj for obj in env.objects}
    found = set()
    for obj in objects.values():
        if obj.type.name == "GameObject" and obj.peek_name() in MODELS:
            data = timbermesh_bytes(objects, obj.read())
            if data:
                with open(os.path.join(args.out, obj.peek_name() + ".timbermesh"), "wb") as f:
                    f.write(data)
                found.add(obj.peek_name())
                print("extracted", obj.peek_name())
    missing = set(MODELS) - found
    if missing:
        raise SystemExit(f"not found: {sorted(missing)}")


if __name__ == "__main__":
    main()
