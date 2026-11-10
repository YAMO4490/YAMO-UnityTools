"""Project Streamingle's native MRX bundle into YAMO Timeline bone samples."""
import argparse
import base64
import json
from pathlib import Path
import sys


def project(bundle, output):
    import mocap_core
    import skeleton

    model = None
    previous_meta = None
    count = 0
    with (bundle / "samples.jsonl").open(encoding="utf-8") as samples, output.open("x", encoding="utf-8") as body:
        for line in samples:
            sample = json.loads(line)
            if sample["track"] != "body":
                continue
            if sample["bodyFormat"] != "natnet":
                raise ValueError("Only NatNet body recordings are supported.")
            meta = sample.get("meta")
            if meta != previous_meta:
                previous_meta = meta
                model = skeleton._build_mdef(json.dumps(meta)) if meta else None
            frame = mocap_core.nn_frame(base64.b64decode(sample["dataBase64"]))
            subjects = [
                {key: subject[key] for key in ("id", "name", "seg_names", "parents", "local")}
                for subject in skeleton.parse_subjects_decoded(frame, model)
                if subject.get("kind") != "rigid_body"
            ]
            if subjects:
                row = {"timeNs": int(sample["sessionTimeNs"]), "subjects": subjects}
                body.write(json.dumps(row, ensure_ascii=False, allow_nan=False) + "\n")
                count += 1
    if not count:
        raise ValueError("No NatNet skeleton frames with model definitions were found.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("modules", type=Path)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    sys.path.insert(0, str(args.modules.absolute()))
    project(args.bundle, args.output)
