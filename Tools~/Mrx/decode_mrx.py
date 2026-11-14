"""Decode an MMRP take using the matching MMRP Windows x64 Python modules."""
import argparse
import json
from pathlib import Path
import sys


def decode(source, destination):
    import mocap_core
    import recording_archive
    import skeleton
    from mrx_clock import BodySampleCorrector, load_body_clock

    metadata = recording_archive.inspect_archive(source)
    if metadata["bodyFormat"] != "natnet":
        raise ValueError("Only NatNet body recordings are supported.")
    origin = int(metadata["originMmrpTimeNs"])
    correction = BodySampleCorrector(load_body_clock(source))
    reader = mocap_core.MrxDecodedReader()
    if not reader.open(str(source)):
        raise ValueError("Cannot open MRX recording.")
    previous_meta = None
    model = None
    frame_count = 0
    try:
        with (destination / "body.jsonl").open("x", encoding="utf-8") as output:
            while True:
                batch = correction(reader.next_samples())
                if batch is None:
                    break
                for sample in batch:
                    if sample["track"] != "body":
                        continue
                    if sample["metaJson"] != previous_meta:
                        previous_meta = sample["metaJson"]
                        model = skeleton._build_mdef(previous_meta) if previous_meta else None
                    frame = mocap_core.nn_frame(bytes(sample["data"]))
                    subjects = [
                        {key: subject[key] for key in ("id", "name", "seg_names", "parents", "local")}
                        for subject in skeleton.parse_subjects_decoded(frame, model)
                        if subject.get("kind") != "rigid_body"
                    ]
                    if subjects:
                        timestamp = max(0, int(sample["timestampNs"]) - origin)
                        output.write(json.dumps({"timeNs": timestamp, "subjects": subjects},
                                                ensure_ascii=False, allow_nan=False) + "\n")
                        frame_count += 1
    finally:
        reader.close()
    if not frame_count:
        raise ValueError("No NatNet skeleton frames with model definitions were found.")

    info = audio = None
    for record in recording_archive.iter_raw_records(source):
        if record["track"] == 2:
            if record["type"] == 0:
                info = json.loads(record["payload"])
            elif record["type"] == 2:
                audio = record["payload"]
    placement = None
    if audio and info:
        duration = recording_archive.opus_duration_ns(audio)
        placement = recording_archive.audio_placement(info, origin, duration)
        (destination / "audio.ogg").write_bytes(audio)
    with (destination / "take.json").open("x", encoding="utf-8") as output:
        json.dump({"bodyFrames": frame_count, "audio": placement}, output, ensure_ascii=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("modules", type=Path)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    sys.path.insert(0, str(args.modules.resolve(strict=True)))
    args.destination.mkdir(parents=True, exist_ok=False)
    decode(args.source.resolve(strict=True), args.destination)
